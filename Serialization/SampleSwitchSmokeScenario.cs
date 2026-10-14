using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArisenEngine.Core.Assets;
using ArisenEngine.Threading;
using ArisenKernel.Lifecycle;

namespace ArisenEngine.Resources.Serialization;

internal sealed class SampleSwitchSmokeScenarioProvider : IRuntimeSmokeScenarioProvider
{
    public const string ModeName = "sample-switch";

    private readonly ISampleCatalogueService m_Catalogues;
    private readonly IRuntimeWorldStreamingService m_Streaming;
    private readonly IRuntimeSceneService m_Scenes;
    private readonly IRuntimeAssetResidencyService m_Residency;
    private readonly IBackgroundTaskScheduler m_Scheduler;
    private readonly IAssetDatabase m_AssetDatabase;

    public SampleSwitchSmokeScenarioProvider(
        ISampleCatalogueService catalogues,
        IRuntimeWorldStreamingService streaming,
        IRuntimeSceneService scenes,
        IRuntimeAssetResidencyService residency,
        IBackgroundTaskScheduler scheduler,
        IAssetDatabase assetDatabase)
    {
        m_Catalogues = catalogues;
        m_Streaming = streaming;
        m_Scenes = scenes;
        m_Residency = residency;
        m_Scheduler = scheduler;
        m_AssetDatabase = assetDatabase;
    }

    public bool TryCreateScenario(
        RuntimeSmokeScenarioContext context,
        out IRuntimeSmokeScenario scenario,
        out string diagnostic)
    {
        if (!string.Equals(context.ModeName, ModeName, StringComparison.Ordinal))
        {
            scenario = null!;
            diagnostic = $"Resources does not provide smoke scenario '{context.ModeName}'.";
            return false;
        }

        scenario = new SampleSwitchSmokeScenario(
            context,
            m_Catalogues,
            m_Streaming,
            m_Scenes,
            m_Residency,
            m_Scheduler,
            m_AssetDatabase);
        diagnostic = string.Empty;
        return true;
    }
}

/// <summary>
/// The gallery's switch gate. The process boots whatever its profile selected - the hub scene for
/// a gallery profile - and this scenario then selects every world-backed catalogue entry in
/// declaration order through the runtime world-load path, asserting that each one becomes the
/// active world and that activation supersedes the previous content in place. It is the bounded
/// proof that a content-only sample is selected inside the running process instead of by editing
/// the workspace, and it never touches the hub.
/// </summary>
internal sealed class SampleSwitchSmokeScenario : IRuntimeSmokeScenario
{
    private const uint SettleFrameCountAfterLastSwitch = 30;

    private enum SwitchPhase
    {
        Loading,
        Settling,
        Finished
    }

    private readonly RuntimeSmokeScenarioContext m_Context;
    private readonly ISampleCatalogueService m_Catalogues;
    private readonly IRuntimeWorldStreamingService m_Streaming;
    private readonly IRuntimeSceneService m_Scenes;
    private readonly IRuntimeAssetResidencyService m_Residency;
    private readonly IBackgroundTaskScheduler m_Scheduler;
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly List<SampleSwitchStep> m_Steps = new();

    private SwitchPhase m_Phase = SwitchPhase.Loading;
    private int m_Index;
    private Guid m_ExpectedWorld;
    private Guid m_PreviousWorld;
    private Guid m_FinalActiveWorldGuid;
    private uint m_SettleDeadline;
    private uint m_LastFrame;
    private string? m_FailureMessage;
    private bool m_ReadyForShutdown;
    private bool m_Complete;
    private bool m_ShutdownDrained;

    public SampleSwitchSmokeScenario(
        RuntimeSmokeScenarioContext context,
        ISampleCatalogueService catalogues,
        IRuntimeWorldStreamingService streaming,
        IRuntimeSceneService scenes,
        IRuntimeAssetResidencyService residency,
        IBackgroundTaskScheduler scheduler,
        IAssetDatabase assetDatabase)
    {
        m_Context = context;
        m_Catalogues = catalogues;
        m_Streaming = streaming;
        m_Scenes = scenes;
        m_Residency = residency;
        m_Scheduler = scheduler;
        m_AssetDatabase = assetDatabase;
        OutputPath = string.IsNullOrWhiteSpace(context.OutputPath)
            ? GetDefaultOutputPath(context.WorkspacePath, context.ProfileName)
            : Path.GetFullPath(context.OutputPath);
    }

    public string Name => SampleSwitchSmokeScenarioProvider.ModeName;
    public string OutputPath { get; }
    public bool IsReadyForShutdown => m_ReadyForShutdown;
    public bool IsComplete => m_Complete;
    public bool Succeeded => m_Complete && m_FailureMessage == null && m_ShutdownDrained;
    public string? FailureMessage => m_FailureMessage;

    public void Start(uint initialFrameIndex)
    {
        m_LastFrame = initialFrameIndex;
        foreach (SampleDefinition sample in m_Catalogues.Samples)
        {
            if (sample.World == null)
            {
                // A scene-only sample has no world to enter in-process; it boots through its own
                // profile and a process restart (D8), and its own gate launches it directly.
                continue;
            }

            m_Steps.Add(new SampleSwitchStep(sample.Id, sample.World.Guid, sample.World.PackageId));
        }

        if (m_Steps.Count == 0)
        {
            ReportFailure("The catalogue declares no world-backed sample to switch to.");
            m_ReadyForShutdown = true;
            return;
        }

        m_PreviousWorld = m_Streaming.PresentationSnapshot.ActiveWorldGuid;
        BeginSwitch(initialFrameIndex);
    }

    public void BeforeFrame(uint frameIndex)
    {
    }

    public void AfterFrame(uint frameIndex)
    {
        m_LastFrame = frameIndex;
        if (m_Phase == SwitchPhase.Finished)
        {
            return;
        }

        RuntimeWorldPresentationSnapshot snapshot = m_Streaming.PresentationSnapshot;
        if (m_Phase == SwitchPhase.Loading)
        {
            if (snapshot.PendingWorldAsset is { } pending && pending.Guid == m_ExpectedWorld)
            {
                m_Steps[m_Index].PendingObserved = true;
            }

            if (snapshot.ActiveWorldGuid == m_ExpectedWorld)
            {
                m_Steps[m_Index].ActivatedFrame = frameIndex;
                m_Steps[m_Index].AlreadyActive = m_PreviousWorld == m_ExpectedWorld;
                m_PreviousWorld = m_ExpectedWorld;
                if (m_Index + 1 < m_Steps.Count)
                {
                    m_Index++;
                    BeginSwitch(frameIndex);
                }
                else
                {
                    m_FinalActiveWorldGuid = snapshot.ActiveWorldGuid;
                    m_Phase = SwitchPhase.Settling;
                    m_SettleDeadline = frameIndex + SettleFrameCountAfterLastSwitch;
                }

                return;
            }

            // A new load clears the active world while the next persistent scene stages, so an
            // empty active world is the expected supersession gap. A different non-empty world
            // becoming active means the process is running content the scenario never selected,
            // which cannot be attributed to the catalogue.
            if (snapshot.ActiveWorldGuid != m_PreviousWorld &&
                snapshot.ActiveWorldGuid != Guid.Empty)
            {
                ReportFailure(
                    $"Sample '{m_Steps[m_Index].SampleId}' expected world {m_ExpectedWorld:D} to " +
                    $"become active, but world {snapshot.ActiveWorldGuid:D} did.");
                return;
            }
        }
        else if (m_Phase == SwitchPhase.Settling && frameIndex >= m_SettleDeadline)
        {
            m_Phase = SwitchPhase.Finished;
            m_ReadyForShutdown = true;
        }
    }

    public void ReportFailure(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        m_FailureMessage ??= message;
        if (!m_ReadyForShutdown)
        {
            m_Phase = SwitchPhase.Finished;
            m_ReadyForShutdown = true;
        }
    }

    public void AfterShutdown()
    {
        try
        {
            m_ShutdownDrained =
                m_Streaming.ActiveWorld == null &&
                m_Streaming.GetCells().Count == 0 &&
                m_Scenes.GetSceneInstances().Count == 0 &&
                m_Scheduler.OutstandingTaskCount == 0 &&
                m_AssetDatabase.GetLoadedCookedAssetDiagnostics().Count == 0 &&
                m_Residency.GetResources().Count == 0;
            if (!m_ShutdownDrained)
            {
                m_FailureMessage ??=
                    "Shutdown left world cells, scene instances, tasks, cooked handles, or residency " +
                    "entries alive.";
            }
        }
        catch (Exception ex)
        {
            m_FailureMessage ??= $"Shutdown drain inspection failed: {ex.Message}";
        }

        m_Complete = true;
        WriteSummary();
    }

    private void BeginSwitch(uint frameIndex)
    {
        SampleSwitchStep step = m_Steps[m_Index];
        m_ExpectedWorld = step.WorldGuid;
        step.LoadedFrame = frameIndex;
        RuntimeWorldLoadResult result = m_Streaming.LoadWorld(
            new AssetRef<WorldSourceAsset>(step.WorldGuid, "World", step.WorldPackageId));
        if (!result.Success)
        {
            ReportFailure(
                $"Catalogued sample '{step.SampleId}' could not load its world: {result.Diagnostic}");
            return;
        }

        step.Deferred = result.Deferred;
    }

    private void WriteSummary()
    {
        var artifact = new SampleSwitchSmokeArtifact(
            SchemaVersion: 1,
            Mode: Name,
            Profile: m_Context.ProfileName,
            Workspace: Path.GetFullPath(m_Context.WorkspacePath),
            Passed: Succeeded,
            Frames: m_LastFrame,
            // Captured while the content was still live: shutdown clears the active world before the
            // artifact is written.
            FinalActiveWorldGuid: m_FinalActiveWorldGuid.ToString("D"),
            Steps: m_Steps.Select(step => new SampleSwitchSmokeStepArtifact(
                step.SampleId,
                step.WorldGuid.ToString("D"),
                step.WorldPackageId,
                step.LoadedFrame,
                step.ActivatedFrame,
                step.PendingObserved,
                step.AlreadyActive,
                step.Deferred)).ToArray(),
            Failure: m_FailureMessage);

        string? directory = Path.GetDirectoryName(OutputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = OutputPath + ".tmp." + Guid.NewGuid().ToString("N");
        string json = JsonSerializer.Serialize(artifact, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        });
        File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
        File.Move(temporaryPath, OutputPath, overwrite: true);
    }

    private static string GetDefaultOutputPath(string workspacePath, string profileName)
    {
        string safeProfile = string.Concat(profileName.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        return Path.GetFullPath(Path.Combine(
            workspacePath,
            ".arisen",
            "Logs",
            $"sample-switch-summary-{safeProfile}-latest.json"));
    }

    private sealed class SampleSwitchStep
    {
        public SampleSwitchStep(string sampleId, Guid worldGuid, string worldPackageId)
        {
            SampleId = sampleId;
            WorldGuid = worldGuid;
            WorldPackageId = worldPackageId;
        }

        public string SampleId { get; }
        public Guid WorldGuid { get; }
        public string WorldPackageId { get; }
        public uint LoadedFrame { get; set; }
        public uint? ActivatedFrame { get; set; }
        public bool PendingObserved { get; set; }
        public bool AlreadyActive { get; set; }
        public bool Deferred { get; set; }
    }

    private sealed record SampleSwitchSmokeArtifact(
        int SchemaVersion,
        string Mode,
        string Profile,
        string Workspace,
        bool Passed,
        uint Frames,
        string FinalActiveWorldGuid,
        IReadOnlyList<SampleSwitchSmokeStepArtifact> Steps,
        string? Failure);

    private sealed record SampleSwitchSmokeStepArtifact(
        string SampleId,
        string WorldGuid,
        string WorldPackageId,
        uint LoadedFrame,
        uint? ActivatedFrame,
        bool PendingObserved,
        bool AlreadyActive,
        bool Deferred);
}
