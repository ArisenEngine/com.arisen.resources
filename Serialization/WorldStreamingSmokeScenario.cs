using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.ECS;
using ArisenEngine.Threading;
using ArisenKernel.Lifecycle;

namespace ArisenEngine.Resources.Serialization;

internal sealed class WorldStreamingSmokeScenarioProvider : IRuntimeSmokeScenarioProvider
{
    private readonly IRuntimeWorldStreamingService m_Streaming;
    private readonly IRuntimeSceneService m_Scenes;
    private readonly IRuntimeAssetResidencyService m_Residency;
    private readonly IWorldOriginService m_Origin;
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly IBackgroundTaskScheduler m_Scheduler;

    public WorldStreamingSmokeScenarioProvider(
        IRuntimeWorldStreamingService streaming,
        IRuntimeSceneService scenes,
        IRuntimeAssetResidencyService residency,
        IWorldOriginService origin,
        IAssetDatabase assetDatabase,
        IBackgroundTaskScheduler scheduler)
    {
        m_Streaming = streaming;
        m_Scenes = scenes;
        m_Residency = residency;
        m_Origin = origin;
        m_AssetDatabase = assetDatabase;
        m_Scheduler = scheduler;
    }

    public bool TryCreateScenario(
        RuntimeSmokeScenarioContext context,
        out IRuntimeSmokeScenario scenario,
        out string diagnostic)
    {
        if (!string.Equals(context.ModeName, "world-streaming", StringComparison.Ordinal))
        {
            scenario = null!;
            diagnostic = $"Resources does not provide smoke scenario '{context.ModeName}'.";
            return false;
        }

        scenario = new WorldStreamingSmokeScenario(
            context,
            m_Streaming,
            m_Scenes,
            m_Residency,
            m_Origin,
            m_AssetDatabase,
            m_Scheduler);
        diagnostic = string.Empty;
        return true;
    }
}

internal sealed class WorldStreamingSmokeScenario : IRuntimeSmokeScenario
{
    private const int SoakCycleCount = 4;

    private readonly RuntimeSmokeScenarioContext m_Context;
    private readonly IRuntimeWorldStreamingService m_Streaming;
    private readonly IRuntimeSceneService m_Scenes;
    private readonly IRuntimeAssetResidencyService m_Residency;
    private readonly IWorldOriginService m_Origin;
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly IBackgroundTaskScheduler m_Scheduler;
    private readonly HashSet<WorldCellStreamingState> m_ObservedStates = new();
    private readonly List<WorldStreamingSmokeCheckpoint> m_Checkpoints = new();
    private readonly Dictionary<Entity, WorldPosition> m_PersistentWorldPositions = new();
    private readonly List<long> m_RebaseStarts = new();
    private readonly List<long> m_RebaseCompletions = new();
    private readonly WorldStreamingSmokePeaks m_Peaks = new();
    private WorldDescriptor? m_World;
    private EntityManager? m_EntityManager;
    private WorldCellDescriptor? m_PrimaryCell;
    private WorldCellDescriptor? m_CancellationCell;
    private WorldCellDescriptor? m_FailureCell;
    private WorldPosition m_NearSource;
    private WorldPosition m_CancellationSource;
    private WorldPosition m_FarSource;
    private WorldStreamingSmokeStage m_Stage;
    private WorldStreamingSmokeBounds? m_FirstLoadedBounds;
    private int m_SoakCyclesCompleted;
    private uint m_LastFrameIndex;
    private string? m_FailureMessage;
    private bool m_ReadyForShutdown;
    private bool m_Complete;
    private bool m_ShutdownDrained;
    private bool m_OriginStable = true;
    private bool m_CountsStable = true;
    private bool m_BoundsStable = true;

    public WorldStreamingSmokeScenario(
        RuntimeSmokeScenarioContext context,
        IRuntimeWorldStreamingService streaming,
        IRuntimeSceneService scenes,
        IRuntimeAssetResidencyService residency,
        IWorldOriginService origin,
        IAssetDatabase assetDatabase,
        IBackgroundTaskScheduler scheduler)
    {
        m_Context = context;
        m_Streaming = streaming;
        m_Scenes = scenes;
        m_Residency = residency;
        m_Origin = origin;
        m_AssetDatabase = assetDatabase;
        m_Scheduler = scheduler;
        OutputPath = string.IsNullOrWhiteSpace(context.OutputPath)
            ? GetDefaultOutputPath(context.WorkspacePath, context.ProfileName)
            : Path.GetFullPath(context.OutputPath);
    }

    public string Name => "world-streaming";
    public string OutputPath { get; }
    public bool IsReadyForShutdown => m_ReadyForShutdown;
    public bool IsComplete => m_Complete;
    public bool Succeeded => m_Complete && m_FailureMessage == null && m_ShutdownDrained;
    public string? FailureMessage => m_FailureMessage;

    public void Start(uint initialFrameIndex)
    {
        m_LastFrameIndex = initialFrameIndex;
        m_World = m_Streaming.ActiveWorld
            ?? throw new InvalidOperationException(
                "World-streaming smoke requires an active startup world.");
        m_EntityManager = m_Scenes.ActiveScene?.EntityManager
            ?? throw new InvalidOperationException(
                "World-streaming smoke requires an active persistent scene.");

        SelectValidationCells(m_World);
        ConfigureValidationBudgets();
        CapturePersistentWorldPositions();
        m_Streaming.CellStateChanged += OnCellStateChanged;
        m_Origin.RebaseStarting += OnRebaseStarting;
        m_Origin.Rebased += OnRebased;

        ScheduleVisualCapture("before", initialFrameIndex);
        m_Stage = WorldStreamingSmokeStage.AwaitBeforeCapture;
    }

    public void BeforeFrame(uint frameIndex)
    {
        m_LastFrameIndex = frameIndex;
    }

    public void AfterFrame(uint frameIndex)
    {
        if (m_ReadyForShutdown) return;
        using var _ = Profiler.Zone("WorldStreamingSmoke.AfterFrame");
        m_LastFrameIndex = frameIndex;
        UpdatePeaks();
        ValidateHardBudgets();
        ValidateOriginStability();
        if (m_FailureMessage != null) return;

        switch (m_Stage)
        {
            case WorldStreamingSmokeStage.AwaitBeforeCapture:
                if (VisualCaptureCompleted("before") && PersistentResourcesReady())
                {
                    CaptureCheckpoint("before", frameIndex, Array.Empty<WorldCellId>());
                    BeginInitialRequest();
                }
                break;
            case WorldStreamingSmokeStage.AwaitInitialPlan:
                ObserveInitialPlan();
                break;
            case WorldStreamingSmokeStage.AwaitCancellationAndPrimary:
                ObserveCancellationAndPrimary(frameIndex);
                break;
            case WorldStreamingSmokeStage.AwaitDuringCapture:
                if (VisualCaptureCompleted("during")) BeginFirstUnload();
                break;
            case WorldStreamingSmokeStage.AwaitFirstUnload:
                if (IsFullyDrained())
                {
                    CaptureCheckpoint("unloaded", frameIndex, Array.Empty<WorldCellId>());
                    BeginSoakLoad();
                }
                break;
            case WorldStreamingSmokeStage.AwaitSoakLoad:
                ObserveSoakLoad(frameIndex);
                break;
            case WorldStreamingSmokeStage.AwaitSoakUnload:
                if (IsFullyDrained())
                {
                    m_SoakCyclesCompleted++;
                    BeginSoakLoad();
                }
                break;
            case WorldStreamingSmokeStage.AwaitAfterCapture:
                if (VisualCaptureCompleted("after")) BeginFinalDrain();
                break;
            case WorldStreamingSmokeStage.AwaitFinalDrain:
                if (IsFullyDrained())
                {
                    m_Streaming.ClearStreamingSource();
                    m_Context.VisualSummaryService?.Seal();
                    m_ReadyForShutdown = true;
                    m_Stage = WorldStreamingSmokeStage.ReadyForShutdown;
                }
                break;
        }

        Profiler.PlotValue("WorldStreamingSmoke.Stage", (int)m_Stage);
        Profiler.PlotValue("WorldStreamingSmoke.SoakCycles", m_SoakCyclesCompleted);
    }

    public void ReportFailure(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) message = "Unknown world-streaming smoke failure.";
        m_FailureMessage ??= message;
        m_Context.VisualSummaryService?.Seal();
        m_ReadyForShutdown = true;
        m_Stage = WorldStreamingSmokeStage.ReadyForShutdown;
    }

    public void AfterShutdown()
    {
        m_Streaming.CellStateChanged -= OnCellStateChanged;
        m_Origin.RebaseStarting -= OnRebaseStarting;
        m_Origin.Rebased -= OnRebased;

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
                    "Shutdown left world cells, scene instances, tasks, cooked handles, or residency entries alive.";
            }

            if (!RequiredStatesObserved())
            {
                m_FailureMessage ??=
                    "The scenario did not observe every required queued, active, cancelled, unloaded, and failed state.";
            }

            if (m_SoakCyclesCompleted != SoakCycleCount)
            {
                m_FailureMessage ??=
                    $"Completed {m_SoakCyclesCompleted} soak cycle(s), expected {SoakCycleCount}.";
            }

            if (m_RebaseStarts.Count != m_RebaseCompletions.Count ||
                !m_RebaseStarts.SequenceEqual(m_RebaseCompletions))
            {
                m_OriginStable = false;
                m_FailureMessage ??= "World-origin rebase start/completion hooks were unbalanced.";
            }
        }
        catch (Exception ex)
        {
            m_FailureMessage ??= $"Shutdown inspection failed: {ex.Message}";
        }

        m_Complete = true;
        WriteArtifact();
    }

    private void SelectValidationCells(WorldDescriptor world)
    {
        WorldCellDescriptor[] ordered = world.Cells
            .OrderBy(cell => cell.EstimatedCpuBytes)
            .ThenBy(cell => cell.Id)
            .ToArray();
        if (ordered.Length < 3)
        {
            throw new InvalidOperationException(
                "World-streaming smoke requires at least three cells.");
        }

        long largestNormalEstimate = ordered[^2].EstimatedCpuBytes;
        WorldCellDescriptor failure = ordered[^1];
        WorldCellDescriptor[] normals = ordered
            .Where(cell => cell.EstimatedCpuBytes <= largestNormalEstimate)
            .OrderBy(cell => Math.Abs(cell.Key.Coordinate.X))
            .ThenBy(cell => cell.Id)
            .Take(2)
            .ToArray();
        if (normals.Length != 2 || failure.EstimatedCpuBytes <= largestNormalEstimate)
        {
            throw new InvalidOperationException(
                "World-streaming smoke requires two normal cells and one cell with a larger CPU estimate.");
        }

        m_PrimaryCell = normals[0];
        m_CancellationCell = normals[1];
        m_FailureCell = failure;
        m_NearSource = GetCellCenter(world.Partition, m_PrimaryCell.Key.Coordinate);
        m_CancellationSource = FindCancellationSource(
            world.Partition,
            m_PrimaryCell.Key.Coordinate,
            m_CancellationCell.Key.Coordinate);
        int farX = world.Cells.Max(cell => cell.Key.Coordinate.X) +
            world.Partition.LoadRadius +
            world.Partition.UnloadHysteresis +
            3;
        m_FarSource = GetCellCenter(
            world.Partition,
            new WorldCellCoordinate(
                farX,
                m_PrimaryCell.Key.Coordinate.Y,
                m_PrimaryCell.Key.Coordinate.Z));
    }

    private void ConfigureValidationBudgets()
    {
        long stagingLimit = Math.Max(
            m_PrimaryCell!.EstimatedCpuBytes,
            m_CancellationCell!.EstimatedCpuBytes);
        var budgets = new WorldStreamingBudgets(
            MaxConcurrentReads: 1,
            MaxBytesInFlight: Math.Max(64L * 1024 * 1024, stagingLimit),
            MaxDecodedStagingBytes: stagingLimit,
            MaxActivationsPerFrame: 1,
            MaxActivationMilliseconds: 100.0,
            MaxUnloadsPerFrame: 1);
        if (!m_Streaming.TryConfigureBudgets(budgets, out string diagnostic))
        {
            throw new InvalidOperationException(diagnostic);
        }
    }

    private void CapturePersistentWorldPositions()
    {
        RuntimeSceneState persistent = m_Scenes.ActiveScene!;
        foreach (Entity entity in persistent.AuthoringEntities.RuntimeEntities)
        {
            if (!m_EntityManager!.HasComponent<TransformComponent>(entity)) continue;
            Vector3 local = m_EntityManager.GetComponent<TransformComponent>(entity).Position;
            m_PersistentWorldPositions.Add(entity, m_Origin.ToWorld(local));
        }
    }

    private void BeginInitialRequest()
    {
        m_Streaming.SetStreamingSource(m_NearSource);
        m_Stage = WorldStreamingSmokeStage.AwaitInitialPlan;
    }

    private void ObserveInitialPlan()
    {
        WorldCellStreamingSnapshot cancellation = GetCell(m_CancellationCell!.Id);
        WorldCellStreamingSnapshot failure = GetCell(m_FailureCell!.Id);
        if (failure.State != WorldCellStreamingState.Failed ||
            cancellation.State != WorldCellStreamingState.Queued)
        {
            return;
        }

        m_Streaming.SetStreamingSource(m_CancellationSource);
        m_Stage = WorldStreamingSmokeStage.AwaitCancellationAndPrimary;
    }

    private void ObserveCancellationAndPrimary(uint frameIndex)
    {
        if (!m_ObservedStates.Contains(WorldCellStreamingState.Cancelled) ||
            GetCell(m_PrimaryCell!.Id).State != WorldCellStreamingState.Active)
        {
            return;
        }

        CaptureCheckpoint("during", frameIndex, [m_PrimaryCell.Id]);
        if (ScheduleVisualCapture("during", checked(frameIndex + 1)))
        {
            m_Stage = WorldStreamingSmokeStage.AwaitDuringCapture;
        }
        else
        {
            BeginFirstUnload();
        }
    }

    private void BeginFirstUnload()
    {
        m_Streaming.SetStreamingSource(m_FarSource);
        m_Stage = WorldStreamingSmokeStage.AwaitFirstUnload;
    }

    private void BeginSoakLoad()
    {
        m_Streaming.SetStreamingSource(m_NearSource);
        m_Stage = WorldStreamingSmokeStage.AwaitSoakLoad;
    }

    private void ObserveSoakLoad(uint frameIndex)
    {
        WorldCellId[] expected = [m_PrimaryCell!.Id, m_CancellationCell!.Id];
        if (!ActiveCellIds().SequenceEqual(expected.Order())) return;

        WorldStreamingSmokeCheckpoint checkpoint = CaptureCheckpoint(
            m_SoakCyclesCompleted == SoakCycleCount ? "after" : "soak-loaded",
            frameIndex,
            expected);
        ValidateSoakBounds(checkpoint);
        if (m_FailureMessage != null) return;

        if (m_SoakCyclesCompleted < SoakCycleCount)
        {
            m_Streaming.SetStreamingSource(m_FarSource);
            m_Stage = WorldStreamingSmokeStage.AwaitSoakUnload;
            return;
        }

        if (ScheduleVisualCapture("after", checked(frameIndex + 1)))
        {
            m_Stage = WorldStreamingSmokeStage.AwaitAfterCapture;
        }
        else
        {
            BeginFinalDrain();
        }
    }

    private void BeginFinalDrain()
    {
        m_Streaming.SetStreamingSource(m_FarSource);
        m_Stage = WorldStreamingSmokeStage.AwaitFinalDrain;
    }

    private bool IsFullyDrained()
    {
        WorldStreamingMetrics streaming = m_Streaming.GetMetrics();
        RuntimeAssetResidencyMetrics residency = m_Residency.GetMetrics();
        return ActiveCellIds().Length == 0 &&
            m_PrimaryCell != null &&
            m_CancellationCell != null &&
            GetCell(m_PrimaryCell.Id).State == WorldCellStreamingState.Unloaded &&
            GetCell(m_CancellationCell.Id).State is
                WorldCellStreamingState.Unloaded or WorldCellStreamingState.Cancelled &&
            streaming.InFlightReads == 0 &&
            streaming.BytesInFlight == 0 &&
            streaming.DecodedStagingBytes == 0 &&
            m_Scheduler.OutstandingTaskCount == 0 &&
            residency.WaitingAssetCount == 0 &&
            residency.PendingDisposalCount == 0;
    }

    private bool PersistentResourcesReady()
    {
        RuntimeAssetResidencyMetrics residency = m_Residency.GetMetrics();
        return residency.WaitingAssetCount == 0 &&
            residency.FailedAssetCount == 0 &&
            residency.PendingDisposalCount == 0;
    }

    private WorldStreamingSmokeCheckpoint CaptureCheckpoint(
        string name,
        uint frameIndex,
        IReadOnlyList<WorldCellId> expectedActiveCells)
    {
        RuntimeSceneInstanceSnapshot[] instances = m_Scenes.GetSceneInstances()
            .Where(instance => instance.State == RuntimeSceneInstanceState.Active)
            .ToArray();
        var expectedComponents = SumComponents(instances);
        var actualComponents = ReadComponents(m_EntityManager!);
        int expectedEntities = instances.Sum(instance => instance.EntityCount);
        WorldCellId[] actualActive = ActiveCellIds();
        bool activeSetMatches = actualActive.SequenceEqual(expectedActiveCells.Order());
        bool countsMatch =
            expectedEntities == m_EntityManager!.EntityCount &&
            expectedComponents == actualComponents;
        if (!activeSetMatches || !countsMatch)
        {
            m_CountsStable = false;
            ReportFailure(
                $"Checkpoint '{name}' found stale/missing ECS ownership or an unexpected active-cell set.");
        }

        RuntimeAssetResidencyMetrics residency = m_Residency.GetMetrics();
        if (residency.WaitingAssetCount != 0 || residency.FailedAssetCount != 0)
        {
            ReportFailure(
                $"Checkpoint '{name}' has missing required prepared resources.");
        }

        LoadedCookedAssetDiagnostic[] handles =
            m_AssetDatabase.GetLoadedCookedAssetDiagnostics().ToArray();
        var checkpoint = new WorldStreamingSmokeCheckpoint(
            name,
            frameIndex,
            actualActive.Select(id => id.ToString()).ToArray(),
            expectedEntities,
            m_EntityManager.EntityCount,
            m_EntityManager.AllocatedSlotCount,
            expectedComponents,
            actualComponents,
            m_Streaming.GetMetrics(),
            residency,
            new WorldStreamingSmokeHandleMetrics(
                handles.Length,
                handles.Sum(handle => handle.RefCount),
                handles.Sum(handle => handle.SizeInBytes)),
            m_Origin.GetSnapshot(),
            activeSetMatches && countsMatch &&
                residency.WaitingAssetCount == 0 &&
                residency.FailedAssetCount == 0);
        m_Checkpoints.Add(checkpoint);
        return checkpoint;
    }

    private void ValidateSoakBounds(WorldStreamingSmokeCheckpoint checkpoint)
    {
        var current = new WorldStreamingSmokeBounds(
            checkpoint.AllocatedEntitySlots,
            checkpoint.Handles.LoadedHandleCount,
            checkpoint.Residency.ResidentAssetCount,
            checkpoint.Residency.ReadyAssetCount,
            checkpoint.Residency.PreparedDescriptorCount);
        if (m_FirstLoadedBounds == null)
        {
            m_FirstLoadedBounds = current;
            return;
        }

        if (current.AllocatedEntitySlots > m_FirstLoadedBounds.AllocatedEntitySlots ||
            current.LoadedHandleCount > m_FirstLoadedBounds.LoadedHandleCount ||
            current.ResidentAssetCount > m_FirstLoadedBounds.ResidentAssetCount ||
            current.PreparedResourceCount > m_FirstLoadedBounds.PreparedResourceCount ||
            current.DescriptorCount > m_FirstLoadedBounds.DescriptorCount)
        {
            m_BoundsStable = false;
            ReportFailure("Repeated streaming cycles exceeded the first loaded-cycle capacity bounds.");
        }
    }

    private void ValidateHardBudgets()
    {
        WorldStreamingMetrics streaming = m_Streaming.GetMetrics();
        RuntimeAssetResidencyMetrics residency = m_Residency.GetMetrics();
        if (streaming.InFlightReads > m_Streaming.Budgets.MaxConcurrentReads ||
            streaming.BytesInFlight > m_Streaming.Budgets.MaxBytesInFlight ||
            streaming.DecodedStagingBytes > m_Streaming.Budgets.MaxDecodedStagingBytes ||
            residency.CpuCookedBytes > m_Residency.Budgets.MaxCpuCookedBytes ||
            residency.PreparedGpuBytes > m_Residency.Budgets.MaxPreparedGpuBytes)
        {
            m_BoundsStable = false;
            ReportFailure("Streaming or residency metrics exceeded a configured hard budget.");
        }
    }

    private void ValidateOriginStability()
    {
        if (m_EntityManager == null) return;
        foreach ((Entity entity, WorldPosition expected) in m_PersistentWorldPositions)
        {
            if (!m_EntityManager.IsAlive(entity) ||
                !m_EntityManager.HasComponent<TransformComponent>(entity))
            {
                m_OriginStable = false;
                ReportFailure($"Persistent entity '{entity}' disappeared during origin rebasing.");
                return;
            }

            Vector3 local = m_EntityManager.GetComponent<TransformComponent>(entity).Position;
            WorldPosition actual = m_Origin.ToWorld(local);
            if (!NearlyEqual(expected, actual, 0.001))
            {
                m_OriginStable = false;
                ReportFailure(
                    $"Persistent entity '{entity}' jumped during origin rebasing.");
                return;
            }
        }

        if (m_RebaseStarts.Count != m_RebaseCompletions.Count ||
            !m_RebaseStarts.SequenceEqual(m_RebaseCompletions))
        {
            m_OriginStable = false;
            ReportFailure("World-origin rebase hooks did not complete within one frame boundary.");
        }
    }

    private void UpdatePeaks()
    {
        if (m_EntityManager == null) return;
        WorldStreamingMetrics streaming = m_Streaming.GetMetrics();
        RuntimeAssetResidencyMetrics residency = m_Residency.GetMetrics();
        int handles = m_AssetDatabase.GetLoadedCookedAssetDiagnostics().Count;
        m_Peaks.AllocatedEntitySlots = Math.Max(
            m_Peaks.AllocatedEntitySlots,
            m_EntityManager.AllocatedSlotCount);
        m_Peaks.LoadedAssetHandles = Math.Max(m_Peaks.LoadedAssetHandles, handles);
        m_Peaks.DecodedStagingBytes = Math.Max(
            m_Peaks.DecodedStagingBytes,
            streaming.PeakDecodedStagingBytes);
        m_Peaks.BytesInFlight = Math.Max(
            m_Peaks.BytesInFlight,
            streaming.PeakBytesInFlight);
        m_Peaks.ResidentAssets = Math.Max(
            m_Peaks.ResidentAssets,
            residency.ResidentAssetCount);
        m_Peaks.PreparedResources = Math.Max(
            m_Peaks.PreparedResources,
            residency.ReadyAssetCount);
        m_Peaks.PreparedGpuBytes = Math.Max(
            m_Peaks.PreparedGpuBytes,
            residency.PeakPreparedGpuBytes);
        m_Peaks.PreparedDescriptors = Math.Max(
            m_Peaks.PreparedDescriptors,
            residency.PreparedDescriptorCount);
        m_Peaks.PendingDisposals = Math.Max(
            m_Peaks.PendingDisposals,
            residency.PendingDisposalCount);
    }

    private bool ScheduleVisualCapture(string name, uint frameIndex)
    {
        IRuntimeVisualSummaryService? visual = m_Context.VisualSummaryService;
        if (visual == null) return false;
        if (!visual.TryScheduleCapture(name, frameIndex, out _))
        {
            ReportFailure($"Could not schedule visual-summary checkpoint '{name}'.");
        }
        return m_FailureMessage == null;
    }

    private bool VisualCaptureCompleted(string name)
    {
        IRuntimeVisualSummaryService? visual = m_Context.VisualSummaryService;
        if (visual == null) return true;
        if (!visual.TryGetCaptureResult(name, out RuntimeVisualSummaryCaptureResult result))
        {
            ReportFailure($"Visual-summary checkpoint '{name}' was not registered.");
            return false;
        }

        if (result.State == RuntimeVisualSummaryCaptureState.Failed)
        {
            ReportFailure(
                result.FailureMessage ?? $"Visual-summary checkpoint '{name}' failed.");
            return false;
        }

        return result.State == RuntimeVisualSummaryCaptureState.Succeeded;
    }

    private void OnCellStateChanged(WorldCellStreamingSnapshot snapshot)
    {
        lock (m_ObservedStates) m_ObservedStates.Add(snapshot.State);
    }

    private void OnRebaseStarting(WorldOriginRebase rebase)
    {
        m_RebaseStarts.Add(rebase.Sequence);
    }

    private void OnRebased(WorldOriginRebase rebase)
    {
        m_RebaseCompletions.Add(rebase.Sequence);
    }

    private bool RequiredStatesObserved()
    {
        lock (m_ObservedStates)
        {
            return m_ObservedStates.Contains(WorldCellStreamingState.Queued) &&
                m_ObservedStates.Contains(WorldCellStreamingState.Active) &&
                m_ObservedStates.Contains(WorldCellStreamingState.Cancelled) &&
                m_ObservedStates.Contains(WorldCellStreamingState.Unloaded) &&
                m_ObservedStates.Contains(WorldCellStreamingState.Failed);
        }
    }

    private WorldCellStreamingSnapshot GetCell(WorldCellId id) =>
        m_Streaming.GetCells().Single(cell => cell.CellId == id);

    private WorldCellId[] ActiveCellIds() => m_Streaming.GetCells()
        .Where(cell => cell.State == WorldCellStreamingState.Active)
        .Select(cell => cell.CellId)
        .Order()
        .ToArray();

    private void WriteArtifact()
    {
        RuntimeVisualSummaryCaptureResult[] visualCaptures =
            m_Context.VisualSummaryService?.GetCaptureResults().ToArray() ?? [];
        WorldCellStreamingState[] observed;
        lock (m_ObservedStates) observed = m_ObservedStates.Order().ToArray();
        var artifact = new WorldStreamingSmokeArtifact(
            SchemaVersion: 1,
            CapturedAtUtc: DateTime.UtcNow,
            Mode: Name,
            Profile: m_Context.ProfileName,
            WorldGuid: m_World?.WorldGuid ?? Guid.Empty,
            Passed: Succeeded,
            Failure: m_FailureMessage,
            RequestedSoakCycles: SoakCycleCount,
            CompletedSoakCycles: m_SoakCyclesCompleted,
            ObservedStates: observed.Select(state => state.ToString()).ToArray(),
            Checkpoints: m_Checkpoints.ToArray(),
            VisualCaptures: visualCaptures,
            Peaks: m_Peaks,
            Checks: new WorldStreamingSmokeChecks(
                RequiredStatesObserved(),
                m_CountsStable,
                m_BoundsStable,
                m_OriginStable,
                visualCaptures.Length == 0 || visualCaptures.All(capture =>
                    capture.State == RuntimeVisualSummaryCaptureState.Succeeded),
                m_ShutdownDrained),
            Diagnostics: m_Streaming.GetDiagnostics()
                .Select(diagnostic => diagnostic.Message)
                .Distinct(StringComparer.Ordinal)
                .ToArray());

        string? directory = Path.GetDirectoryName(OutputPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
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

    private static RuntimeSceneComponentCounts SumComponents(
        IReadOnlyList<RuntimeSceneInstanceSnapshot> instances)
    {
        var result = new RuntimeSceneComponentCounts();
        for (int index = 0; index < instances.Count; index++)
        {
            RuntimeSceneComponentCounts value = instances[index].ComponentCounts;
            result = new RuntimeSceneComponentCounts(
                result.CameraCount + value.CameraCount,
                result.MeshRendererCount + value.MeshRendererCount,
                result.DirectionalLightCount + value.DirectionalLightCount,
                result.PointLightCount + value.PointLightCount,
                result.SpotLightCount + value.SpotLightCount,
                result.EnvironmentCount + value.EnvironmentCount);
        }
        return result;
    }

    private static RuntimeSceneComponentCounts ReadComponents(EntityManager world) => new(
        PoolCount<CameraComponent>(world),
        PoolCount<MeshRendererComponent>(world),
        PoolCount<DirectionalLightComponent>(world),
        PoolCount<PointLightComponent>(world),
        PoolCount<SpotLightComponent>(world),
        PoolCount<SceneEnvironmentComponent>(world));

    private static int PoolCount<T>(EntityManager world) where T : struct, IComponent =>
        world.HasPool<T>() ? world.GetPool<T>().Count : 0;

    private static WorldPosition GetCellCenter(
        WorldPartitionSettings partition,
        WorldCellCoordinate coordinate)
    {
        WorldPosition origin = WorldPartitionCoordinates.GetCellOrigin(partition, coordinate);
        return new WorldPosition(
            origin.X + partition.CellSize.X * 0.5,
            origin.Y + partition.CellSize.Y * 0.5,
            origin.Z + partition.CellSize.Z * 0.5);
    }

    private static WorldPosition FindCancellationSource(
        WorldPartitionSettings partition,
        WorldCellCoordinate retained,
        WorldCellCoordinate cancelled)
    {
        int radius = partition.LoadRadius;
        for (int x = retained.X - radius; x <= retained.X + radius; x++)
        {
            var candidate = new WorldCellCoordinate(x, retained.Y, retained.Z);
            if (ChebyshevDistance(candidate, cancelled) > radius)
            {
                return GetCellCenter(partition, candidate);
            }
        }

        throw new InvalidOperationException(
            "World-streaming smoke could not find a source that retains the primary cell while cancelling the queued cell.");
    }

    private static int ChebyshevDistance(
        WorldCellCoordinate left,
        WorldCellCoordinate right) =>
        (int)Math.Max(
            Math.Abs((long)left.X - right.X),
            Math.Max(
                Math.Abs((long)left.Y - right.Y),
                Math.Abs((long)left.Z - right.Z)));

    private static bool NearlyEqual(WorldPosition left, WorldPosition right, double epsilon) =>
        Math.Abs(left.X - right.X) <= epsilon &&
        Math.Abs(left.Y - right.Y) <= epsilon &&
        Math.Abs(left.Z - right.Z) <= epsilon;

    private static string GetDefaultOutputPath(string workspacePath, string profileName)
    {
        string safeProfile = string.Concat(profileName.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        return Path.GetFullPath(Path.Combine(
            workspacePath,
            ".arisen",
            "Logs",
            $"world-streaming-summary-{safeProfile}-latest.json"));
    }
}

internal enum WorldStreamingSmokeStage
{
    None,
    AwaitBeforeCapture,
    AwaitInitialPlan,
    AwaitCancellationAndPrimary,
    AwaitDuringCapture,
    AwaitFirstUnload,
    AwaitSoakLoad,
    AwaitSoakUnload,
    AwaitAfterCapture,
    AwaitFinalDrain,
    ReadyForShutdown
}

internal sealed record WorldStreamingSmokeArtifact(
    int SchemaVersion,
    DateTime CapturedAtUtc,
    string Mode,
    string Profile,
    Guid WorldGuid,
    bool Passed,
    string? Failure,
    int RequestedSoakCycles,
    int CompletedSoakCycles,
    IReadOnlyList<string> ObservedStates,
    IReadOnlyList<WorldStreamingSmokeCheckpoint> Checkpoints,
    IReadOnlyList<RuntimeVisualSummaryCaptureResult> VisualCaptures,
    WorldStreamingSmokePeaks Peaks,
    WorldStreamingSmokeChecks Checks,
    IReadOnlyList<string> Diagnostics);

internal sealed record WorldStreamingSmokeCheckpoint(
    string Name,
    uint FrameIndex,
    IReadOnlyList<string> ActiveCellIds,
    int ExpectedEntityCount,
    int ActualEntityCount,
    int AllocatedEntitySlots,
    RuntimeSceneComponentCounts ExpectedComponents,
    RuntimeSceneComponentCounts ActualComponents,
    WorldStreamingMetrics Streaming,
    RuntimeAssetResidencyMetrics Residency,
    WorldStreamingSmokeHandleMetrics Handles,
    WorldOriginSnapshot Origin,
    bool Passed);

internal sealed record WorldStreamingSmokeHandleMetrics(
    int LoadedHandleCount,
    int ReferenceCount,
    long LoadedBytes);

internal sealed record WorldStreamingSmokeBounds(
    int AllocatedEntitySlots,
    int LoadedHandleCount,
    int ResidentAssetCount,
    int PreparedResourceCount,
    int DescriptorCount);

internal sealed class WorldStreamingSmokePeaks
{
    public int AllocatedEntitySlots { get; set; }
    public int LoadedAssetHandles { get; set; }
    public long BytesInFlight { get; set; }
    public long DecodedStagingBytes { get; set; }
    public int ResidentAssets { get; set; }
    public int PreparedResources { get; set; }
    public long PreparedGpuBytes { get; set; }
    public int PreparedDescriptors { get; set; }
    public int PendingDisposals { get; set; }
}

internal sealed record WorldStreamingSmokeChecks(
    bool RequiredStatesObserved,
    bool EntityAndComponentCountsStable,
    bool MemoryBoundsStable,
    bool OriginStable,
    bool VisualCapturesPassed,
    bool ShutdownDrained);
