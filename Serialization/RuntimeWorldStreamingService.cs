using System.Diagnostics;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Threading;
using ArisenKernel.Diagnostics;

namespace ArisenEngine.Resources.Serialization;

public enum WorldCellStreamingState
{
    Unloaded,
    Queued,
    Reading,
    Decoding,
    Validating,
    WaitingForResources,
    ReadyToActivate,
    Active,
    QueuedToUnload,
    Unloading,
    Failed,
    Cancelled
}

[Flags]
public enum WorldCellDesiredSource
{
    None = 0,
    Runtime = 1 << 0,
    EditPin = 1 << 1,
    EditDependency = 1 << 2
}

public sealed record WorldStreamingBudgets(
    int MaxConcurrentReads,
    long MaxBytesInFlight,
    long MaxDecodedStagingBytes,
    int MaxActivationsPerFrame,
    double MaxActivationMilliseconds,
    int MaxUnloadsPerFrame)
{
    public static WorldStreamingBudgets Default { get; } = new(
        MaxConcurrentReads: 2,
        MaxBytesInFlight: 64L * 1024 * 1024,
        MaxDecodedStagingBytes: 256L * 1024 * 1024,
        MaxActivationsPerFrame: 1,
        MaxActivationMilliseconds: 4.0,
        MaxUnloadsPerFrame: 2);

    public void Validate()
    {
        if (MaxConcurrentReads <= 0 || MaxConcurrentReads > 256 ||
            MaxBytesInFlight <= 0 ||
            MaxDecodedStagingBytes <= 0 ||
            MaxActivationsPerFrame <= 0 ||
            !double.IsFinite(MaxActivationMilliseconds) || MaxActivationMilliseconds <= 0 ||
            MaxUnloadsPerFrame <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(WorldStreamingBudgets),
                "World streaming budgets must be positive and finite.");
        }
    }
}

public sealed record WorldCellStreamingSnapshot(
    WorldCellId CellId,
    WorldCellStreamingState State,
    long RequestGeneration,
    long TransitionSequence,
    bool Desired,
    WorldCellDesiredSource DesiredSources,
    bool Pinned,
    bool ReloadRequested,
    RuntimeSceneInstanceId SceneInstanceId,
    long BytesInFlight,
    long DecodedStagingBytes,
    double LastLoadLatencyMilliseconds,
    string Diagnostic);

public sealed record WorldStreamingMetrics(
    int QueuedCells,
    int ActiveCells,
    int InFlightReads,
    int WaitingForResourcesCells,
    int ReadyCells,
    int FailedCells,
    int CancelledCells,
    long BytesInFlight,
    long DecodedStagingBytes,
    long PeakBytesInFlight,
    long PeakDecodedStagingBytes,
    long CancellationCount,
    long FailureCount,
    long StaleCompletionCount,
    long BudgetStallCount,
    double LastLoadLatencyMilliseconds,
    double LastActivationMilliseconds,
    double LastUnloadMilliseconds,
    long SubscriberFailureCount);

public enum WorldStreamingDiagnosticKind
{
    Cell,
    SubscriberAggregate
}

public sealed record WorldStreamingSubscriberFailure(
    string Notification,
    string Payload,
    string Subscriber,
    string ExceptionType,
    string Message);

public sealed record WorldStreamingDiagnostic(
    long Sequence,
    WorldCellId CellId,
    WorldCellStreamingState State,
    long RequestGeneration,
    string Message)
{
    public WorldStreamingDiagnosticKind Kind { get; init; } = WorldStreamingDiagnosticKind.Cell;
    public string Boundary { get; init; } = string.Empty;
    public IReadOnlyList<WorldStreamingSubscriberFailure> SubscriberFailures { get; init; } =
        Array.Empty<WorldStreamingSubscriberFailure>();
}

public readonly record struct RuntimeWorldLoadResult(
    bool Success,
    Guid WorldGuid,
    int CellCount,
    string Diagnostic)
{
    /// <summary>
    /// True when the world descriptor and persistent scene were staged successfully, but
    /// activation is waiting for frame-boundary residency preparation.
    /// </summary>
    public bool Deferred { get; init; }
}

public readonly record struct RuntimeWorldPresentationSnapshot(
    long Revision,
    AssetRef<WorldSourceAsset>? ActiveWorldAsset,
    AssetRef<WorldSourceAsset>? PendingWorldAsset,
    Guid ActiveWorldGuid);

public interface IRuntimeWorldStreamingService
{
    WorldDescriptor? ActiveWorld { get; }
    AssetRef<WorldSourceAsset>? ActiveWorldAsset { get; }
    RuntimeWorldPresentationSnapshot PresentationSnapshot { get; }
    WorldStreamingBudgets Budgets { get; }

    event Action<WorldCellStreamingSnapshot>? CellStateChanged;
    event Action<AssetRef<WorldSourceAsset>?>? ActiveWorldChanged;
    event Action<RuntimeWorldPresentationSnapshot>? WorldPresentationChanged;

    bool TryConfigureBudgets(WorldStreamingBudgets budgets, out string diagnostic);
    RuntimeWorldLoadResult LoadWorld(AssetRef<WorldSourceAsset> world);
    void SetStreamingSource(WorldPosition position);
    void ClearStreamingSource();
    bool PinCell(WorldCellId cellId);
    bool UnpinCell(WorldCellId cellId);
    bool SetCellPreviewSource(WorldCellId cellId, SceneSourceSnapshot? snapshot);
    bool RequestCellReload(WorldCellId cellId);
    bool RetryCell(WorldCellId cellId);
    IReadOnlyList<WorldCellStreamingSnapshot> GetCells();
    IReadOnlyList<WorldStreamingDiagnostic> GetDiagnostics();
    WorldStreamingMetrics GetMetrics();
}

public sealed class RuntimeWorldStreamingService : IRuntimeWorldStreamingService
{
    private const int MaxDiagnostics = 256;
    private const string ActiveCellLimitDiagnosticPrefix =
        "Cell request was deferred by the world active-cell limit";

    private readonly record struct CapturedSubscriberFailure(
        WorldStreamingSubscriberFailure Diagnostic,
        Exception Error);

    private sealed class PendingPersistentSceneReplacement
    {
        public required long RequestSequence { get; init; }
        public required long ResidencyGeneration { get; init; }
        public required AssetRef<SceneSourceAsset> Scene { get; init; }
        public required SceneSourceSnapshot? Snapshot { get; init; }
        public SceneStagingData? Staging { get; set; }
        public string SourceKind { get; set; } = string.Empty;
        public RuntimeAssetResidencyLease? ResidencyLease { get; set; }
    }

    private sealed class PendingWorldLoad
    {
        public required AssetRef<WorldSourceAsset> World { get; init; }
        public required WorldDescriptor Descriptor { get; init; }
        public required AssetRef<SceneSourceAsset> PersistentScene { get; init; }
        public required SceneStagingData Staging { get; init; }
        public required string SourceKind { get; init; }
        public required RuntimeAssetResidencyLease ResidencyLease { get; init; }
    }

    private readonly object m_LifecycleGate = new();
    private readonly object m_Gate = new();
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly RuntimeSceneService m_SceneService;
    private readonly IBackgroundTaskScheduler m_Scheduler;
    private readonly IWorldCellPayloadLoader m_PayloadLoader;
    private readonly RuntimeAssetResidencyService m_ResidencyService;
    private readonly WorldOriginService m_OriginService;
    private readonly Dictionary<WorldCellId, RuntimeCell> m_Cells = new();
    private readonly Queue<WorldStreamingDiagnostic> m_Diagnostics = new();
    private readonly Queue<WorldCellStreamingSnapshot> m_PendingWorkerNotifications = new();
    private WorldDescriptor? m_ActiveWorld;
    private AssetRef<WorldSourceAsset>? m_ActiveWorldAsset;
    private WorldPosition m_StreamingSource;
    private bool m_HasStreamingSource;
    private bool m_ShuttingDown;
    private int m_LifecycleOwnerThreadId;
    private int m_LifecycleWaiterCount;
    private string m_ActiveLifecycleOperation = string.Empty;
    private long m_NextActivationClaim;
    private long m_NextPersistentReplacementRequest;
    private long m_NextPersistentResidencyGeneration;
    private long m_WorldPresentationRevision;
    private long m_NextTransitionSequence;
    private long m_NextDiagnosticSequence;
    private long m_BytesInFlight;
    private long m_ReservedStagingBytes;
    private long m_DecodedStagingBytes;
    private long m_PeakBytesInFlight;
    private long m_PeakDecodedStagingBytes;
    private long m_CancellationCount;
    private long m_FailureCount;
    private long m_SubscriberFailureCount;
    private long m_StaleCompletionCount;
    private long m_BudgetStallCount;
    private double m_LastLoadLatencyMilliseconds;
    private double m_LastActivationMilliseconds;
    private double m_LastUnloadMilliseconds;
    private RuntimeAssetResidencyLease? m_PersistentResidencyLease;
    private RuntimeSceneInstanceId m_PersistentSceneInstanceId;
    private PendingPersistentSceneReplacement? m_PendingPersistentReplacement;
    private PendingWorldLoad? m_PendingWorldLoad;
    private AssetRef<WorldSourceAsset>? m_PendingWorldPresentationAsset;
    private bool m_PersistentSceneUnloadBlocked;
    private string m_PersistentSceneDiagnostic = string.Empty;

    public RuntimeWorldStreamingService(
        IAssetDatabase assetDatabase,
        RuntimeSceneService sceneService,
        IBackgroundTaskScheduler scheduler,
        WorldStreamingBudgets? budgets = null,
        RuntimeAssetResidencyService? residencyService = null,
        WorldOriginService? originService = null)
        : this(
            assetDatabase,
            sceneService,
            scheduler,
            new WorldCellPayloadLoader(assetDatabase),
            budgets,
            residencyService,
            originService)
    {
    }

    internal RuntimeWorldStreamingService(
        IAssetDatabase assetDatabase,
        RuntimeSceneService sceneService,
        IBackgroundTaskScheduler scheduler,
        IWorldCellPayloadLoader payloadLoader,
        WorldStreamingBudgets? budgets = null,
        RuntimeAssetResidencyService? residencyService = null,
        WorldOriginService? originService = null)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_SceneService = sceneService ?? throw new ArgumentNullException(nameof(sceneService));
        m_Scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        m_PayloadLoader = payloadLoader ?? throw new ArgumentNullException(nameof(payloadLoader));
        m_ResidencyService = residencyService ?? new RuntimeAssetResidencyService(assetDatabase);
        m_OriginService = originService ?? new WorldOriginService();
        Budgets = budgets ?? WorldStreamingBudgets.Default;
        Budgets.Validate();
        m_SceneService.SetWorldPersistentSceneReplacementHandler(
            QueuePersistentSceneReplacement);
    }

    public WorldDescriptor? ActiveWorld
    {
        get
        {
            lock (m_Gate) return m_ActiveWorld;
        }
    }

    public AssetRef<WorldSourceAsset>? ActiveWorldAsset
    {
        get
        {
            lock (m_Gate) return m_ActiveWorldAsset;
        }
    }

    public RuntimeWorldPresentationSnapshot PresentationSnapshot
    {
        get
        {
            lock (m_Gate) return CreateWorldPresentationSnapshotLocked();
        }
    }

    public WorldStreamingBudgets Budgets { get; private set; }

    public IRuntimeAssetResidencyService Residency => m_ResidencyService;

    internal bool IsShuttingDown
    {
        get
        {
            lock (m_Gate) return m_ShuttingDown;
        }
    }

    internal bool PersistentSceneUnloadBlocked
    {
        get
        {
            lock (m_Gate) return m_PersistentSceneUnloadBlocked;
        }
    }

    internal string PersistentSceneDiagnostic
    {
        get
        {
            lock (m_Gate) return m_PersistentSceneDiagnostic;
        }
    }

    internal int PendingLifecycleOperationCount
    {
        get
        {
            lock (m_LifecycleGate) return m_LifecycleWaiterCount;
        }
    }

    public event Action<WorldCellStreamingSnapshot>? CellStateChanged;
    public event Action<AssetRef<WorldSourceAsset>?>? ActiveWorldChanged;
    public event Action<RuntimeWorldPresentationSnapshot>? WorldPresentationChanged;

    public bool TryConfigureBudgets(WorldStreamingBudgets budgets, out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        budgets.Validate();
        lock (m_Gate)
        {
            bool hasStreamingWork = m_Cells.Values.Any(cell =>
                cell.State != WorldCellStreamingState.Unloaded ||
                cell.Task != null ||
                cell.Staging != null ||
                cell.ResidencyLease != null);
            if (hasStreamingWork)
            {
                diagnostic =
                    "World streaming budgets can only change before the first cell request or after all cells unload.";
                return false;
            }

            Budgets = budgets;
            diagnostic = string.Empty;
            return true;
        }
    }

    public RuntimeWorldLoadResult LoadWorld(AssetRef<WorldSourceAsset> world)
    {
        using LifecycleOperationScope lifecycle = EnterLifecycleOperation(nameof(LoadWorld));
        List<CapturedSubscriberFailure>? subscriberFailures = null;
        m_SceneService.BeginWorldLifecycleMutation();
        try
        {
            return LoadWorldCore(world, ref subscriberFailures);
        }
        finally
        {
            m_SceneService.EndWorldLifecycleMutation();
            ReportSubscriberFailures(nameof(LoadWorld), subscriberFailures);
        }
    }

    private RuntimeWorldLoadResult LoadWorldCore(
        AssetRef<WorldSourceAsset> world,
        ref List<CapturedSubscriberFailure>? subscriberFailures)
    {
        if (!world.IsValid)
        {
            return new RuntimeWorldLoadResult(false, Guid.Empty, 0, "World asset ref is empty.");
        }

        BeginWorldPresentationRequest(world, ref subscriberFailures);

        if (!ShutdownCore(
                true,
                world,
                ref subscriberFailures,
                out string shutdownDiagnostic))
        {
            lock (m_Gate) m_ShuttingDown = false;
            RuntimeWorldLoadResult failure = new(
                false,
                world.Guid,
                0,
                $"Existing world shutdown failed: {shutdownDiagnostic}");
            ClearWorldPresentationRequest(world, ref subscriberFailures);
            return failure;
        }
        WorldDescriptorLoadResult loaded = m_AssetDatabase.CanReadSourceAssets
            ? WorldDescriptorLoader.LoadSource(m_AssetDatabase, world)
            : WorldAssetCooker.LoadCooked(m_AssetDatabase, world);
        if (!loaded.Success || loaded.Descriptor == null)
        {
            lock (m_Gate) m_ShuttingDown = false;
            ClearWorldPresentationRequest(world, ref subscriberFailures);
            return new RuntimeWorldLoadResult(false, world.Guid, 0, loaded.Diagnostic);
        }

        WorldDescriptor descriptor = loaded.Descriptor;
        m_OriginService.ConfigureForWorld(descriptor.Partition);
        AssetRef<SceneSourceAsset> persistentScene = new(
            descriptor.PersistentScene.Guid,
            "Scene",
            descriptor.PersistentScene.PackageId);
        SceneStagingData persistentStaging;
        string stagingDiagnostic;
        bool staged = m_AssetDatabase.CanReadSourceAssets
            ? SceneAssetLoader.TryLoadSceneStaging(
                m_AssetDatabase,
                persistentScene,
                out persistentStaging,
                out stagingDiagnostic)
            : SceneAssetCooker.TryLoadCookedStaging(
                m_AssetDatabase,
                persistentScene,
                out persistentStaging,
                out stagingDiagnostic);
        string persistentSourceKind = m_AssetDatabase.CanReadSourceAssets ? "source" : "cooked";
        if (!staged)
        {
            lock (m_Gate) m_ShuttingDown = false;
            ClearWorldPresentationRequest(world, ref subscriberFailures);
            return new RuntimeWorldLoadResult(false, world.Guid, 0, stagingDiagnostic);
        }

        RuntimeAssetResidencyLease persistentLease;
        long persistentResidencyGeneration;
        lock (m_Gate)
        {
            persistentResidencyGeneration = ++m_NextPersistentResidencyGeneration;
        }
        try
        {
            persistentLease = m_ResidencyService.AcquireSceneDependencies(
                RuntimeAssetResidencyOwnerId.Persistent(
                    descriptor.WorldGuid,
                    persistentResidencyGeneration),
                SceneAssetCooker.GetDependencies(persistentStaging),
                pinned: true);
        }
        catch (Exception ex)
        {
            lock (m_Gate) m_ShuttingDown = false;
            ClearWorldPresentationRequest(world, ref subscriberFailures);
            return new RuntimeWorldLoadResult(
                false,
                world.Guid,
                0,
                $"Persistent world-scene residency acquisition failed: {ex.Message}");
        }

        if (persistentLease.State != RuntimePreparedAssetState.Ready)
        {
            string residencyDiagnostic = persistentLease.Diagnostic;
            RuntimePreparedAssetState state = persistentLease.State;
            if (state == RuntimePreparedAssetState.Waiting)
            {
                lock (m_Gate)
                {
                    m_PendingWorldLoad = new PendingWorldLoad
                    {
                        World = world,
                        Descriptor = descriptor,
                        PersistentScene = persistentScene,
                        Staging = persistentStaging,
                        SourceKind = persistentSourceKind,
                        ResidencyLease = persistentLease
                    };
                    m_ShuttingDown = false;
                }
                m_SceneService.SetWorldPersistentStartupPending(true);

                return new RuntimeWorldLoadResult(
                    true,
                    descriptor.WorldGuid,
                    descriptor.Cells.Count,
                    $"Persistent world-scene residency is waiting for frame-boundary preparation. " +
                    residencyDiagnostic)
                {
                    Deferred = true
                };
            }

            persistentLease.Dispose();
            lock (m_Gate) m_ShuttingDown = false;
            ClearWorldPresentationRequest(world, ref subscriberFailures);
            return new RuntimeWorldLoadResult(
                false,
                world.Guid,
                0,
                $"Persistent world-scene residency is {state} after bounded setup: " +
                residencyDiagnostic);
        }

        RuntimeSceneInstanceId persistentInstanceId;
        SceneLoadResult persistent;
        try
        {
            (persistentInstanceId, persistent) =
                m_SceneService.ActivatePreparedPersistentAtLifecycleBoundary(
                    persistentScene,
                    persistentStaging,
                    persistentSourceKind);
        }
        catch (Exception ex)
        {
            persistentLease.Dispose();
            lock (m_Gate) m_ShuttingDown = false;
            ClearWorldPresentationRequest(world, ref subscriberFailures);
            return new RuntimeWorldLoadResult(
                false,
                world.Guid,
                0,
                $"Persistent world-scene activation failed: {ex.Message}");
        }

        if (!persistent.Success)
        {
            RuntimeWorldLoadResult failure = FailPersistentSceneLoad(
                world,
                descriptor,
                persistentScene,
                persistentInstanceId,
                persistentLease,
                $"Persistent world-scene activation failed: {persistent.Diagnostic}");
            ClearWorldPresentationRequest(world, ref subscriberFailures);
            return failure;
        }

        if (!m_SceneService.TryGetSceneInstance(
                persistentInstanceId,
                out RuntimeSceneInstanceSnapshot persistentSnapshot) ||
            persistentSnapshot.State != RuntimeSceneInstanceState.Active ||
            persistentSnapshot.Kind != RuntimeSceneInstanceKind.Persistent ||
            !IsSameScene(persistentSnapshot.Scene, persistentScene))
        {
            string identityDiagnostic =
                $"Persistent world-scene activation did not leave the requested active instance " +
                $"'{persistentInstanceId}'.";
            RuntimeWorldLoadResult failure = FailPersistentSceneLoad(
                world,
                descriptor,
                persistentScene,
                persistentInstanceId,
                persistentLease,
                identityDiagnostic);
            ClearWorldPresentationRequest(world, ref subscriberFailures);
            return failure;
        }

        RuntimeWorldPresentationSnapshot activePresentationSnapshot;
        lock (m_Gate)
        {
            ResetCountersLocked();
            m_ActiveWorld = descriptor;
            m_ActiveWorldAsset = world;
            m_PersistentResidencyLease = persistentLease;
            m_PersistentSceneInstanceId = persistentInstanceId;
            m_PersistentSceneUnloadBlocked = false;
            m_PersistentSceneDiagnostic = string.Empty;
            m_PendingWorldPresentationAsset = null;
            m_ShuttingDown = false;
            foreach (WorldCellDescriptor cell in descriptor.Cells)
            {
                m_Cells.Add(cell.Id, new RuntimeCell(cell));
            }
            activePresentationSnapshot = AdvanceWorldPresentationLocked();
        }

        PlotMetrics();
        PublishWorldPresentationChanged(
            activePresentationSnapshot,
            ref subscriberFailures);
        PublishActiveWorldChanged(world, ref subscriberFailures);
        return new RuntimeWorldLoadResult(true, descriptor.WorldGuid, descriptor.Cells.Count, string.Empty);
    }

    private void BeginWorldPresentationRequest(
        AssetRef<WorldSourceAsset> world,
        ref List<CapturedSubscriberFailure>? subscriberFailures)
    {
        RuntimeWorldPresentationSnapshot presentationSnapshot;
        lock (m_Gate)
        {
            m_PendingWorldPresentationAsset = world;
            presentationSnapshot = AdvanceWorldPresentationLocked();
        }

        PublishWorldPresentationChanged(presentationSnapshot, ref subscriberFailures);
    }

    private void ClearWorldPresentationRequest(
        AssetRef<WorldSourceAsset> world,
        ref List<CapturedSubscriberFailure>? subscriberFailures)
    {
        RuntimeWorldPresentationSnapshot presentationSnapshot = default;
        bool cleared = false;
        lock (m_Gate)
        {
            if (m_PendingWorldPresentationAsset == world)
            {
                m_PendingWorldPresentationAsset = null;
                presentationSnapshot = AdvanceWorldPresentationLocked();
                cleared = true;
            }
        }

        if (cleared)
        {
            PublishWorldPresentationChanged(presentationSnapshot, ref subscriberFailures);
        }
    }

    private RuntimeWorldLoadResult FailPersistentSceneLoad(
        AssetRef<WorldSourceAsset> world,
        WorldDescriptor descriptor,
        AssetRef<SceneSourceAsset> expectedScene,
        RuntimeSceneInstanceId instanceId,
        RuntimeAssetResidencyLease persistentLease,
        string failureDiagnostic)
    {
        bool retained = false;
        string cleanupDiagnostic = string.Empty;
        if (instanceId.IsValid &&
            m_SceneService.TryGetSceneInstance(
                instanceId,
                out RuntimeSceneInstanceSnapshot snapshot) &&
            snapshot.Kind == RuntimeSceneInstanceKind.Persistent &&
            IsSameScene(snapshot.Scene, expectedScene))
        {
            if (snapshot.State == RuntimeSceneInstanceState.Active)
            {
                bool unloaded = m_SceneService.UnloadSceneAtWorldLifecycleBoundary(
                    instanceId,
                    out cleanupDiagnostic);
                retained = !unloaded;
            }
            else if (snapshot.State == RuntimeSceneInstanceState.QueuedForUnload)
            {
                retained = true;
                cleanupDiagnostic =
                    $"Persistent scene '{instanceId}' remains queued for unload and still owns ECS state.";
            }
        }

        string diagnostic = failureDiagnostic;
        bool releaseLease = !retained;
        lock (m_Gate)
        {
            m_ShuttingDown = false;
            if (retained)
            {
                m_SceneService.SetWorldPersistentRollbackBlocked(true);
                m_PersistentSceneInstanceId = instanceId;
                m_PersistentResidencyLease = persistentLease;
                m_PersistentSceneUnloadBlocked = true;
                m_PersistentSceneDiagnostic =
                    $"World '{descriptor.WorldGuid:D}' retained persistent scene " +
                    $"'{instanceId}' after rollback rejection: {cleanupDiagnostic}";
                diagnostic = $"{failureDiagnostic} {m_PersistentSceneDiagnostic}";
            }
            else
            {
                m_SceneService.SetWorldPersistentRollbackBlocked(false);
                m_PersistentSceneInstanceId = RuntimeSceneInstanceId.Invalid;
                m_PersistentResidencyLease = null;
                m_PersistentSceneUnloadBlocked = false;
                m_PersistentSceneDiagnostic = string.Empty;
            }
        }

        if (releaseLease)
        {
            persistentLease.Dispose();
        }

        return new RuntimeWorldLoadResult(false, world.Guid, 0, diagnostic);
    }

    private void QueuePersistentSceneReplacement(
        AssetRef<SceneSourceAsset> scene,
        SceneSourceSnapshot? snapshot)
    {
        using LifecycleOperationScope lifecycle =
            EnterLifecycleOperation("QueuePersistentSceneReplacement");
        RuntimeAssetResidencyLease? supersededLease;
        lock (m_Gate)
        {
            if (m_ShuttingDown || m_ActiveWorld == null ||
                !m_ActiveWorldAsset.HasValue ||
                !m_PersistentSceneInstanceId.IsValid)
            {
                throw new InvalidOperationException(
                    "Persistent scene preview requires a fully active world lifecycle owner.");
            }

            AssetRef<SceneSourceAsset> expectedScene = new(
                m_ActiveWorld.PersistentScene.Guid,
                "Scene",
                m_ActiveWorld.PersistentScene.PackageId);
            if (!IsSameScene(expectedScene, scene))
            {
                throw new InvalidOperationException(
                    "Persistent scene preview must target the active world's persistent scene.");
            }

            supersededLease = m_PendingPersistentReplacement?.ResidencyLease;
            m_PendingPersistentReplacement = new PendingPersistentSceneReplacement
            {
                RequestSequence = ++m_NextPersistentReplacementRequest,
                ResidencyGeneration = ++m_NextPersistentResidencyGeneration,
                Scene = scene,
                Snapshot = snapshot
            };
        }

        supersededLease?.Dispose();
    }

    private void PreparePendingPersistentReplacement()
    {
        PendingPersistentSceneReplacement? pending;
        WorldDescriptor? activeWorld;
        lock (m_Gate)
        {
            pending = m_PendingPersistentReplacement;
            activeWorld = m_ActiveWorld;
            if (pending == null || pending.ResidencyLease != null || activeWorld == null)
            {
                return;
            }
        }

        SceneStagingData staging;
        string stagingDiagnostic;
        bool staged = pending.Snapshot != null
            ? SceneAssetLoader.TryLoadSceneStaging(
                m_AssetDatabase,
                pending.Snapshot,
                out staging,
                out stagingDiagnostic)
            : m_AssetDatabase.CanReadSourceAssets
                ? SceneAssetLoader.TryLoadSceneStaging(
                    m_AssetDatabase,
                    pending.Scene,
                    out staging,
                    out stagingDiagnostic)
                : SceneAssetCooker.TryLoadCookedStaging(
                    m_AssetDatabase,
                    pending.Scene,
                    out staging,
                    out stagingDiagnostic);
        if (!staged)
        {
            FailPendingPersistentReplacement(pending, stagingDiagnostic, publishFailure: true);
            return;
        }

        RuntimeAssetResidencyLease replacementLease;
        try
        {
            replacementLease = m_ResidencyService.AcquireSceneDependencies(
                RuntimeAssetResidencyOwnerId.Persistent(
                    activeWorld.WorldGuid,
                    pending.ResidencyGeneration),
                SceneAssetCooker.GetDependencies(staging),
                pinned: true);
        }
        catch (Exception ex)
        {
            FailPendingPersistentReplacement(
                pending,
                $"Persistent scene preview residency acquisition failed: {ex.Message}",
                publishFailure: true);
            return;
        }

        lock (m_Gate)
        {
            if (!ReferenceEquals(m_PendingPersistentReplacement, pending))
            {
                replacementLease.Dispose();
                return;
            }

            pending.Staging = staging;
            pending.SourceKind = pending.Snapshot != null || m_AssetDatabase.CanReadSourceAssets
                ? "source"
                : "cooked";
            pending.ResidencyLease = replacementLease;
        }
    }

    private void ActivateReadyPersistentReplacement()
    {
        PendingPersistentSceneReplacement? pending;
        RuntimeSceneInstanceId expectedInstanceId;
        lock (m_Gate)
        {
            pending = m_PendingPersistentReplacement;
            expectedInstanceId = m_PersistentSceneInstanceId;
            if (pending?.ResidencyLease == null || pending.Staging == null)
            {
                return;
            }
        }

        RuntimePreparedAssetState residencyState = pending.ResidencyLease.State;
        if (residencyState == RuntimePreparedAssetState.Waiting)
        {
            return;
        }
        if (residencyState == RuntimePreparedAssetState.Failed)
        {
            FailPendingPersistentReplacement(
                pending,
                $"Persistent scene preview residency preparation failed: " +
                pending.ResidencyLease.Diagnostic,
                publishFailure: true);
            return;
        }

        (RuntimeSceneInstanceId replacementInstanceId, SceneLoadResult result) =
            m_SceneService.ActivatePreparedWorldPersistentReplacementAtLifecycleBoundary(
                expectedInstanceId,
                pending.Scene,
                pending.Snapshot,
                pending.Staging,
                pending.SourceKind);
        if (!result.Success)
        {
            FailPendingPersistentReplacement(
                pending,
                result.Diagnostic,
                publishFailure: false);
            return;
        }

        if (!m_SceneService.TryGetSceneInstance(
                replacementInstanceId,
                out RuntimeSceneInstanceSnapshot replacementSnapshot) ||
            replacementSnapshot.State != RuntimeSceneInstanceState.Active ||
            replacementSnapshot.Kind != RuntimeSceneInstanceKind.Persistent ||
            !IsSameScene(replacementSnapshot.Scene, pending.Scene))
        {
            throw new InvalidOperationException(
                $"Persistent scene preview activation did not publish exact active instance " +
                $"'{replacementInstanceId}'.");
        }

        RuntimeAssetResidencyLease? previousLease;
        lock (m_Gate)
        {
            if (!ReferenceEquals(m_PendingPersistentReplacement, pending) ||
                m_PersistentSceneInstanceId != expectedInstanceId)
            {
                throw new InvalidOperationException(
                    "Persistent scene preview ownership changed during its lifecycle transaction.");
            }

            previousLease = m_PersistentResidencyLease;
            m_PersistentResidencyLease = pending.ResidencyLease;
            m_PersistentSceneInstanceId = replacementInstanceId;
            pending.ResidencyLease = null;
            m_PendingPersistentReplacement = null;
            m_PersistentSceneUnloadBlocked = false;
            m_PersistentSceneDiagnostic = string.Empty;
        }

        previousLease?.Dispose();
    }

    private void FailPendingPersistentReplacement(
        PendingPersistentSceneReplacement pending,
        string diagnostic,
        bool publishFailure)
    {
        RuntimeAssetResidencyLease? failedLease;
        lock (m_Gate)
        {
            if (!ReferenceEquals(m_PendingPersistentReplacement, pending))
            {
                return;
            }

            failedLease = pending.ResidencyLease;
            pending.ResidencyLease = null;
            m_PendingPersistentReplacement = null;
        }

        failedLease?.Dispose();
        if (publishFailure)
        {
            m_SceneService.ReportWorldPersistentReplacementFailure(
                pending.Scene,
                pending.Snapshot,
                diagnostic);
        }
    }

    private static bool IsSameScene(
        AssetRef<SceneSourceAsset> left,
        AssetRef<SceneSourceAsset> right)
    {
        return left.Guid == right.Guid &&
               string.Equals(left.PackageId, right.PackageId, StringComparison.OrdinalIgnoreCase);
    }

    public void SetStreamingSource(WorldPosition position)
    {
        if (!position.IsFinite)
        {
            throw new ArgumentException("World streaming source must be finite.", nameof(position));
        }

        lock (m_Gate)
        {
            m_StreamingSource = position;
            m_HasStreamingSource = true;
        }
        m_OriginService.RequestPrimarySource(position);
    }

    public void ClearStreamingSource()
    {
        lock (m_Gate) m_HasStreamingSource = false;
        m_OriginService.ClearPrimarySource();
    }

    public bool PinCell(WorldCellId cellId)
    {
        lock (m_Gate)
        {
            if (!m_Cells.TryGetValue(cellId, out RuntimeCell? cell)) return false;
            cell.Pinned = true;
            cell.ResidencyLease?.SetPinned(true);
            return true;
        }
    }

    public bool UnpinCell(WorldCellId cellId)
    {
        lock (m_Gate)
        {
            if (!m_Cells.TryGetValue(cellId, out RuntimeCell? cell)) return false;
            cell.Pinned = false;
            cell.ResidencyLease?.SetPinned(false);
            return true;
        }
    }

    public bool SetCellPreviewSource(WorldCellId cellId, SceneSourceSnapshot? snapshot)
    {
        List<CapturedSubscriberFailure>? subscriberFailures = null;
        try
        {
            return SetCellPreviewSourceCore(cellId, snapshot, ref subscriberFailures);
        }
        finally
        {
            ReportSubscriberFailures(nameof(SetCellPreviewSource), subscriberFailures);
        }
    }

    private bool SetCellPreviewSourceCore(
        WorldCellId cellId,
        SceneSourceSnapshot? snapshot,
        ref List<CapturedSubscriberFailure>? subscriberFailures)
    {
        if (!m_AssetDatabase.CanReadSourceAssets)
        {
            throw new InvalidOperationException(
                "World-cell source previews require explicit source-asset access.");
        }

        lock (m_Gate)
        {
            if (!m_Cells.TryGetValue(cellId, out RuntimeCell? cell)) return false;
            if (snapshot is { } value)
            {
                if (!value.IsValid ||
                    value.Scene.Guid != cell.Descriptor.Scene.Guid ||
                    !string.Equals(
                        value.Scene.PackageId,
                        cell.Descriptor.Scene.PackageId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        $"Preview source identity does not match world cell '{cellId}'.",
                        nameof(snapshot));
                }
            }

            cell.PreviewSource = snapshot;
        }

        return RequestCellReloadCore(cellId, ref subscriberFailures);
    }

    public bool RequestCellReload(WorldCellId cellId)
    {
        List<CapturedSubscriberFailure>? subscriberFailures = null;
        try
        {
            return RequestCellReloadCore(cellId, ref subscriberFailures);
        }
        finally
        {
            ReportSubscriberFailures(nameof(RequestCellReload), subscriberFailures);
        }
    }

    private bool RequestCellReloadCore(
        WorldCellId cellId,
        ref List<CapturedSubscriberFailure>? subscriberFailures)
    {
        var changes = new List<WorldCellStreamingSnapshot>(2);
        lock (m_Gate)
        {
            if (!m_Cells.TryGetValue(cellId, out RuntimeCell? cell)) return false;

            if (cell.State == WorldCellStreamingState.Active)
            {
                cell.ReloadRequested = true;
                cell.UnloadBlocked = false;
                cell.Diagnostic = "Cell reload requested.";
                changes.Add(SnapshotLocked(cell));
            }
            else if (cell.ActivationClaim != 0)
            {
                cell.ReloadRequested = true;
                cell.Diagnostic =
                    "Claimed cell activation was superseded by a reload and will be discarded.";
                changes.Add(SnapshotLocked(cell));
            }
            else if (cell.Task is { IsCompleted: false })
            {
                cell.RequestGeneration++;
                cell.Task.Cancel();
                m_CancellationCount++;
                cell.Diagnostic = "In-flight cell request was superseded by a reload.";
                changes.Add(TransitionLocked(cell, WorldCellStreamingState.Cancelled));
                if (cell.Desired || cell.Pinned)
                {
                    changes.Add(TransitionLocked(cell, WorldCellStreamingState.Queued));
                }
            }
            else if (cell.State is
                     WorldCellStreamingState.WaitingForResources or
                     WorldCellStreamingState.ReadyToActivate)
            {
                ReleaseStagingLocked(cell);
                ReleaseResidencyLocked(cell);
                cell.Diagnostic = "Prepared cell request was superseded by a reload.";
                changes.Add(TransitionLocked(cell, WorldCellStreamingState.Cancelled));
                if (cell.Desired || cell.Pinned)
                {
                    changes.Add(TransitionLocked(cell, WorldCellStreamingState.Queued));
                }
            }
            else if (cell.State is WorldCellStreamingState.Failed or WorldCellStreamingState.Cancelled)
            {
                changes.Add(TransitionLocked(cell, WorldCellStreamingState.Unloaded));
            }
            else
            {
                cell.Diagnostic = "Cell reload will use the latest preview on its next load.";
                changes.Add(SnapshotLocked(cell));
            }
        }

        Publish(changes, ref subscriberFailures);
        return true;
    }

    public bool RetryCell(WorldCellId cellId)
    {
        List<CapturedSubscriberFailure>? subscriberFailures = null;
        try
        {
            return RetryCellCore(cellId, ref subscriberFailures);
        }
        finally
        {
            ReportSubscriberFailures(nameof(RetryCell), subscriberFailures);
        }
    }

    private bool RetryCellCore(
        WorldCellId cellId,
        ref List<CapturedSubscriberFailure>? subscriberFailures)
    {
        WorldCellStreamingSnapshot? changed = null;
        lock (m_Gate)
        {
            if (!m_Cells.TryGetValue(cellId, out RuntimeCell? cell))
            {
                return false;
            }

            if (cell.State == WorldCellStreamingState.Active && cell.UnloadBlocked)
            {
                cell.UnloadBlocked = false;
                cell.Diagnostic = string.Empty;
                changed = SnapshotLocked(cell);
            }
            else if (cell.State is WorldCellStreamingState.Failed or WorldCellStreamingState.Cancelled)
            {
                cell.Diagnostic = string.Empty;
                changed = TransitionLocked(
                    cell,
                    cell.Desired || cell.Pinned
                        ? WorldCellStreamingState.Queued
                        : WorldCellStreamingState.Unloaded);
            }
            else
            {
                return false;
            }

        }

        Publish(changed, ref subscriberFailures);
        return true;
    }

    public IReadOnlyList<WorldCellStreamingSnapshot> GetCells()
    {
        lock (m_Gate)
        {
            return m_Cells.Values.OrderBy(cell => cell.Descriptor.Id).Select(SnapshotLocked).ToArray();
        }
    }

    public IReadOnlyList<WorldStreamingDiagnostic> GetDiagnostics()
    {
        lock (m_Gate) return m_Diagnostics.ToArray();
    }

    public WorldStreamingMetrics GetMetrics()
    {
        lock (m_Gate) return BuildMetricsLocked();
    }

    internal SceneLoadResult? ProcessAtFrameBoundary()
    {
        using LifecycleOperationScope lifecycle =
            EnterLifecycleOperation(nameof(ProcessAtFrameBoundary));
        List<CapturedSubscriberFailure>? subscriberFailures = null;
        SceneLoadResult? pendingSceneResult = null;
        try
        {
            ProcessAtFrameBoundaryCore(ref subscriberFailures);
            pendingSceneResult = m_SceneService.ProcessPendingSceneLoadAtFrameBoundary();
        }
        finally
        {
            ReportSubscriberFailures(nameof(ProcessAtFrameBoundary), subscriberFailures);
        }

        return pendingSceneResult;
    }

    private void ProcessAtFrameBoundaryCore(
        ref List<CapturedSubscriberFailure>? subscriberFailures)
    {
        using var _ = Profiler.Zone("WorldStreaming.FrameBoundary");
        bool hasPendingWorldLoad;
        bool cleanupOnly;
        lock (m_Gate)
        {
            if (m_ShuttingDown) return;
            hasPendingWorldLoad = m_PendingWorldLoad != null;
            cleanupOnly = m_ActiveWorld == null && !hasPendingWorldLoad;
        }

        if (cleanupOnly)
        {
            // Deferred activation may have released its final lease after the prior
            // frame's setup pass. This owner-thread sweep evicts those inactive
            // resources without reopening activation or admitting world work.
            m_ResidencyService.ProcessAtFrameBoundary();
            return;
        }

        if (hasPendingWorldLoad)
        {
            // The sole provider setup pass for this engine frame. Deferred startup has no
            // active cell work yet, so activation can consume this pass directly.
            m_ResidencyService.ProcessAtFrameBoundary();
            ActivatePendingWorldLoad(ref subscriberFailures);
        }

        lock (m_Gate)
        {
            if (m_ActiveWorld == null || m_ShuttingDown) return;
        }

        PreparePendingPersistentReplacement();
        if (m_SceneService.ActiveScene is { EntityManager: { } entityManager })
        {
            m_OriginService.ProcessAtFrameBoundary(entityManager);
        }
        PlanDesiredCells(ref subscriberFailures);
        ProcessCompletedReads(ref subscriberFailures);
        if (!hasPendingWorldLoad)
        {
            m_ResidencyService.ProcessAtFrameBoundary();
        }
        ActivateReadyPersistentReplacement();
        ProcessWaitingResources(ref subscriberFailures);
        AdmitQueuedReads(ref subscriberFailures);
        UnloadUndesiredCells(ref subscriberFailures);
        ActivateReadyCells(ref subscriberFailures);
        PlotMetrics();
    }

    private void ActivatePendingWorldLoad(
        ref List<CapturedSubscriberFailure>? subscriberFailures)
    {
        PendingWorldLoad? pending;
        lock (m_Gate)
        {
            pending = m_PendingWorldLoad;
            if (pending == null || m_ShuttingDown)
            {
                return;
            }
        }

        RuntimePreparedAssetState state = pending.ResidencyLease.State;
        if (state == RuntimePreparedAssetState.Waiting)
        {
            return;
        }

        if (state == RuntimePreparedAssetState.Failed)
        {
            string diagnostic = pending.ResidencyLease.Diagnostic;
            pending.ResidencyLease.Dispose();
            ClearPendingWorldLoad(pending, ref subscriberFailures);
            throw new InvalidOperationException(
                $"Persistent world-scene residency preparation failed for deferred world " +
                $"'{pending.World.Guid:D}': {diagnostic}");
        }

        RuntimeSceneInstanceId persistentInstanceId;
        SceneLoadResult persistent;
        RuntimeWorldPresentationSnapshot activePresentationSnapshot = default;
        m_SceneService.BeginWorldLifecycleMutation();
        try
        {
            try
            {
                (persistentInstanceId, persistent) =
                    m_SceneService.ActivatePreparedPersistentAtLifecycleBoundary(
                        pending.PersistentScene,
                        pending.Staging,
                        pending.SourceKind);
            }
            catch
            {
                pending.ResidencyLease.Dispose();
                ClearPendingWorldLoad(pending, ref subscriberFailures);
                throw;
            }

            bool identityValid = persistent.Success &&
                m_SceneService.TryGetSceneInstance(
                    persistentInstanceId,
                    out RuntimeSceneInstanceSnapshot persistentSnapshot) &&
                persistentSnapshot.State == RuntimeSceneInstanceState.Active &&
                persistentSnapshot.Kind == RuntimeSceneInstanceKind.Persistent &&
                IsSameScene(persistentSnapshot.Scene, pending.PersistentScene);
            bool ownershipValid;
            lock (m_Gate) ownershipValid = ReferenceEquals(m_PendingWorldLoad, pending);
            if (!identityValid || !ownershipValid)
            {
                RuntimeWorldLoadResult rollback = FailPersistentSceneLoad(
                    pending.World,
                    pending.Descriptor,
                    pending.PersistentScene,
                    persistentInstanceId,
                    pending.ResidencyLease,
                    !persistent.Success
                        ? $"Deferred persistent world-scene activation failed: {persistent.Diagnostic}"
                        : !identityValid
                            ? $"Deferred persistent world-scene activation did not publish exact active " +
                              $"instance '{persistentInstanceId}'."
                            : "Deferred persistent world load ownership changed during activation.");
                ClearPendingWorldLoad(pending, ref subscriberFailures);
                throw new InvalidOperationException(rollback.Diagnostic);
            }

            lock (m_Gate)
            {
                ResetCountersLocked();
                m_ActiveWorld = pending.Descriptor;
                m_ActiveWorldAsset = pending.World;
                m_PersistentResidencyLease = pending.ResidencyLease;
                m_PersistentSceneInstanceId = persistentInstanceId;
                m_PersistentSceneUnloadBlocked = false;
                m_PersistentSceneDiagnostic = string.Empty;
                m_ShuttingDown = false;
                m_PendingWorldLoad = null;
                m_PendingWorldPresentationAsset = null;
                foreach (WorldCellDescriptor cell in pending.Descriptor.Cells)
                {
                    m_Cells.Add(cell.Id, new RuntimeCell(cell));
                }
                activePresentationSnapshot = AdvanceWorldPresentationLocked();
            }

            m_SceneService.SetWorldPersistentStartupPending(false);
        }
        finally
        {
            m_SceneService.EndWorldLifecycleMutation();
        }

        PlotMetrics();
        PublishWorldPresentationChanged(
            activePresentationSnapshot,
            ref subscriberFailures);
        PublishActiveWorldChanged(pending.World, ref subscriberFailures);
    }

    private void ClearPendingWorldLoad(
        PendingWorldLoad pending,
        ref List<CapturedSubscriberFailure>? subscriberFailures)
    {
        bool detached = false;
        RuntimeWorldPresentationSnapshot presentationSnapshot = default;
        lock (m_Gate)
        {
            if (ReferenceEquals(m_PendingWorldLoad, pending))
            {
                m_PendingWorldLoad = null;
                m_PendingWorldPresentationAsset = null;
                m_ShuttingDown = false;
                detached = true;
                presentationSnapshot = AdvanceWorldPresentationLocked();
            }
        }

        if (detached)
        {
            m_SceneService.SetWorldPersistentStartupPending(false);
            PublishWorldPresentationChanged(
                presentationSnapshot,
                ref subscriberFailures);
        }
    }

    internal void Shutdown(bool unloadActiveCells)
    {
        using LifecycleOperationScope lifecycle = EnterLifecycleOperation(nameof(Shutdown));
        List<CapturedSubscriberFailure>? subscriberFailures = null;
        m_SceneService.BeginWorldLifecycleMutation();
        try
        {
            if (!ShutdownCore(
                    unloadActiveCells,
                    null,
                    ref subscriberFailures,
                    out string diagnostic))
            {
                throw new InvalidOperationException(
                    $"Runtime world streaming shutdown failed: {diagnostic}");
            }
        }
        finally
        {
            m_SceneService.EndWorldLifecycleMutation();
            ReportSubscriberFailures(nameof(Shutdown), subscriberFailures);
        }
    }

    private bool ShutdownCore(
        bool unloadActiveCells,
        AssetRef<WorldSourceAsset>? replacementPresentation,
        ref List<CapturedSubscriberFailure>? subscriberFailures,
        out string diagnostic)
    {
        BackgroundTask<CellPayloadLoadResult>[] tasks;
        bool publishActiveWorldClosed;
        RuntimeWorldPresentationSnapshot? presentationSnapshot;
        lock (m_Gate)
        {
            m_ShuttingDown = true;
        }

        if (unloadActiveCells)
        {
            if (!m_SceneService.UnloadAllScenesAtWorldLifecycleBoundary(out diagnostic))
            {
                lock (m_Gate) m_ShuttingDown = false;
                return false;
            }
        }

        lock (m_Gate)
        {
            tasks = m_Cells.Values
                .Where(cell => cell.Task != null)
                .Select(cell => cell.Task!)
                .ToArray();
            foreach (BackgroundTask<CellPayloadLoadResult> task in tasks) task.Cancel();
        }

        foreach (BackgroundTask<CellPayloadLoadResult> task in tasks)
        {
            task.Wait();
            if (task.TryGetResult(out CellPayloadLoadResult completed))
            {
                completed.Dispose();
            }
        }

        RuntimeAssetResidencyLease[] leases;
        RuntimeAssetResidencyLease? persistentLease;
        RuntimeAssetResidencyLease? pendingPersistentLease;
        RuntimeAssetResidencyLease? pendingWorldLease;
        lock (m_Gate)
        {
            publishActiveWorldClosed = m_ActiveWorld != null || m_ActiveWorldAsset.HasValue;
            bool publishPresentationChanged =
                publishActiveWorldClosed ||
                m_PendingWorldPresentationAsset != replacementPresentation;
            leases = m_Cells.Values
                .Where(cell => cell.ResidencyLease != null)
                .Select(cell => cell.ResidencyLease!)
                .ToArray();
            persistentLease = m_PersistentResidencyLease;
            pendingPersistentLease = m_PendingPersistentReplacement?.ResidencyLease;
            pendingWorldLease = m_PendingWorldLoad?.ResidencyLease;
            m_PersistentResidencyLease = null;
            m_PendingPersistentReplacement = null;
            m_PendingWorldLoad = null;
            m_PendingWorldPresentationAsset = replacementPresentation;
            m_PersistentSceneInstanceId = RuntimeSceneInstanceId.Invalid;
            m_PersistentSceneUnloadBlocked = false;
            m_PersistentSceneDiagnostic = string.Empty;
            m_Cells.Clear();
            m_PendingWorkerNotifications.Clear();
            m_ActiveWorld = null;
            m_ActiveWorldAsset = null;
            m_HasStreamingSource = false;
            m_BytesInFlight = 0;
            m_ReservedStagingBytes = 0;
            m_DecodedStagingBytes = 0;
            presentationSnapshot = publishPresentationChanged
                ? AdvanceWorldPresentationLocked()
                : null;
        }

        if (presentationSnapshot.HasValue)
        {
            PublishWorldPresentationChanged(
                presentationSnapshot.Value,
                ref subscriberFailures);
        }
        if (publishActiveWorldClosed)
        {
            PublishActiveWorldChanged(null, ref subscriberFailures);
        }

        var cleanupFailures = new List<Exception>();
        foreach (RuntimeAssetResidencyLease lease in leases)
        {
            TryWorldCleanup(
                "cell residency lease release",
                lease.Dispose,
                cleanupFailures);
        }
        if (persistentLease != null)
        {
            TryWorldCleanup(
                "persistent residency lease release",
                persistentLease.Dispose,
                cleanupFailures);
        }
        if (pendingPersistentLease != null)
        {
            TryWorldCleanup(
                "pending persistent residency lease release",
                pendingPersistentLease.Dispose,
                cleanupFailures);
        }
        if (pendingWorldLease != null)
        {
            TryWorldCleanup(
                "pending world residency lease release",
                pendingWorldLease.Dispose,
                cleanupFailures);
        }
        TryWorldCleanup(
            "persistent-scene admission reset",
            () => m_SceneService.SetWorldPersistentStartupPending(false),
            cleanupFailures);
        TryWorldCleanup(
            "residency frame-boundary cleanup",
            m_ResidencyService.ProcessAtFrameBoundary,
            cleanupFailures);

        if (cleanupFailures.Count > 0)
        {
            diagnostic = new AggregateException(
                "World streaming post-state cleanup failed.",
                cleanupFailures).Message;
            return false;
        }

        diagnostic = string.Empty;
        return true;
    }

    private static void TryWorldCleanup(
        string stage,
        Action cleanup,
        ICollection<Exception> failures)
    {
        try
        {
            cleanup();
        }
        catch (Exception error)
        {
            failures.Add(new InvalidOperationException(
                $"World streaming failed to complete {stage}.",
                error));
        }
    }

    private void PlanDesiredCells(ref List<CapturedSubscriberFailure>? subscriberFailures)
    {
        using var _ = Profiler.Zone("WorldStreaming.PlanRequests");
        var changes = new List<WorldCellStreamingSnapshot>();
        lock (m_Gate)
        {
            DrainWorkerNotificationsLocked(changes);
            WorldDescriptor world = m_ActiveWorld!;
            WorldCellCoordinate sourceCoordinate = m_HasStreamingSource
                ? WorldPartitionCoordinates.GetCoordinate(world.Partition, m_StreamingSource)
                : default;

            var selected = new HashSet<WorldCellId>();
            var editDesired = new HashSet<WorldCellId>();
            var runtimeDesired = new HashSet<WorldCellId>();
            var dependencyClosure = new HashSet<WorldCellId>();
            foreach (RuntimeCell pinned in m_Cells.Values
                         .Where(cell => cell.Pinned)
                         .OrderBy(cell => cell.Descriptor.Id))
            {
                CollectDependencyClosureLocked(pinned, dependencyClosure);
                editDesired.UnionWith(dependencyClosure);
                selected.UnionWith(dependencyClosure);
            }

            if (m_HasStreamingSource)
            {
                RuntimeCell[] cameraCandidates = m_Cells.Values
                    .Where(cell =>
                    {
                        bool activeLike = cell.ActivationClaim != 0 || cell.State is
                            WorldCellStreamingState.Active or
                            WorldCellStreamingState.QueuedToUnload or
                            WorldCellStreamingState.Unloading;
                        int radius = world.Partition.LoadRadius +
                            (activeLike ? world.Partition.UnloadHysteresis : 0);
                        return ChebyshevDistance(
                            sourceCoordinate,
                            cell.Descriptor.Key.Coordinate) <= radius;
                    })
                    .OrderBy(cell => ChebyshevDistance(
                        sourceCoordinate,
                        cell.Descriptor.Key.Coordinate))
                    .ThenBy(cell => GetLayerPriority(world, cell.Descriptor.Key.Layer))
                    .ThenBy(cell => cell.Descriptor.Id)
                    .ToArray();
                foreach (RuntimeCell candidate in cameraCandidates)
                {
                    CollectDependencyClosureLocked(candidate, dependencyClosure);
                    int additionalCount = dependencyClosure.Count(id => !selected.Contains(id));
                    if (selected.Count + additionalCount > world.Partition.MaxActiveCells)
                    {
                        m_BudgetStallCount++;
                        candidate.Diagnostic =
                            $"{ActiveCellLimitDiagnosticPrefix} ({world.Partition.MaxActiveCells}).";
                        continue;
                    }

                    selected.UnionWith(dependencyClosure);
                    runtimeDesired.UnionWith(dependencyClosure);
                }
            }

            foreach (RuntimeCell cell in m_Cells.Values.OrderBy(cell => cell.Descriptor.Id))
            {
                WorldCellDesiredSource desiredSources = WorldCellDesiredSource.None;
                if (runtimeDesired.Contains(cell.Descriptor.Id))
                {
                    desiredSources |= WorldCellDesiredSource.Runtime;
                }
                if (cell.Pinned)
                {
                    desiredSources |= WorldCellDesiredSource.EditPin;
                }
                else if (editDesired.Contains(cell.Descriptor.Id))
                {
                    desiredSources |= WorldCellDesiredSource.EditDependency;
                }

                bool selectionChanged =
                    cell.DesiredSources != desiredSources ||
                    cell.Desired != (desiredSources != WorldCellDesiredSource.None);
                cell.DesiredSources = desiredSources;
                cell.Desired = desiredSources != WorldCellDesiredSource.None;
                if (cell.Desired)
                {
                    if (cell.Diagnostic.StartsWith(
                            ActiveCellLimitDiagnosticPrefix,
                            StringComparison.Ordinal))
                    {
                        cell.Diagnostic = string.Empty;
                    }

                    if (cell.State is WorldCellStreamingState.Unloaded or WorldCellStreamingState.Cancelled)
                    {
                        changes.Add(TransitionLocked(cell, WorldCellStreamingState.Queued));
                    }
                    else if (selectionChanged)
                    {
                        changes.Add(SnapshotLocked(cell));
                    }
                    continue;
                }

                bool transitioned = false;
                if (cell.ActivationClaim != 0)
                {
                    if (selectionChanged)
                    {
                        m_CancellationCount++;
                        cell.Diagnostic =
                            "Claimed cell activation will be discarded because the cell left the desired set.";
                        changes.Add(SnapshotLocked(cell));
                    }
                    transitioned = true;
                }
                else if (cell.Task != null && !cell.Task.IsCompleted)
                {
                    cell.RequestGeneration++;
                    cell.Task.Cancel();
                    m_CancellationCount++;
                    cell.Diagnostic = "Cell request was cancelled because it left the desired set.";
                    changes.Add(TransitionLocked(cell, WorldCellStreamingState.Cancelled));
                    transitioned = true;
                }
                else if (cell.State is
                         WorldCellStreamingState.WaitingForResources or
                         WorldCellStreamingState.ReadyToActivate)
                {
                    ReleaseStagingLocked(cell);
                    ReleaseResidencyLocked(cell);
                    m_CancellationCount++;
                    cell.Diagnostic =
                        "Validated cell staging and residency were discarded because the cell left the desired set.";
                    changes.Add(TransitionLocked(cell, WorldCellStreamingState.Cancelled));
                    transitioned = true;
                }
                else if (cell.State == WorldCellStreamingState.Queued && cell.Task == null)
                {
                    m_CancellationCount++;
                    cell.Diagnostic = "Queued cell request was cancelled because it left the desired set.";
                    changes.Add(TransitionLocked(cell, WorldCellStreamingState.Cancelled));
                    transitioned = true;
                }

                if (selectionChanged && !transitioned)
                {
                    changes.Add(SnapshotLocked(cell));
                }
            }
        }

        Publish(changes, ref subscriberFailures);
    }

    private void ProcessCompletedReads(ref List<CapturedSubscriberFailure>? subscriberFailures)
    {
        var changes = new List<WorldCellStreamingSnapshot>();
        lock (m_Gate)
        {
            DrainWorkerNotificationsLocked(changes);
            foreach (RuntimeCell cell in m_Cells.Values.OrderBy(cell => cell.Descriptor.Id))
            {
                BackgroundTask<CellPayloadLoadResult>? task = cell.Task;
                if (task == null || !task.IsCompleted) continue;

                m_BytesInFlight -= cell.BytesInFlight;
                m_ReservedStagingBytes -= cell.StagingReservationBytes;
                cell.BytesInFlight = 0;
                cell.StagingReservationBytes = 0;
                cell.Task = null;
                bool hasResult = task.TryGetResult(out CellPayloadLoadResult result);
                if (task.Sequence != cell.TaskSequence || cell.ScheduledGeneration != cell.RequestGeneration)
                {
                    if (hasResult) result.Dispose();
                    m_StaleCompletionCount++;
                    cell.Diagnostic = "Stale cell completion was discarded by request generation.";
                    if (cell.Desired)
                    {
                        changes.Add(TransitionLocked(cell, WorldCellStreamingState.Queued));
                    }
                    continue;
                }

                if (task.Status == BackgroundTaskStatus.Cancelled)
                {
                    cell.Diagnostic = "Cell worker request was cancelled.";
                    changes.Add(TransitionLocked(cell, WorldCellStreamingState.Cancelled));
                    continue;
                }

                if (task.Status == BackgroundTaskStatus.Failed || !hasResult)
                {
                    m_FailureCount++;
                    cell.Diagnostic = task.Failure?.Message ?? "Cell worker request failed without a result.";
                    WorldCellStreamingSnapshot failed = TransitionLocked(
                        cell,
                        WorldCellStreamingState.Failed);
                    AddDiagnosticLocked(cell);
                    changes.Add(failed);
                    continue;
                }

                if (!cell.Desired)
                {
                    result.Dispose();
                    m_StaleCompletionCount++;
                    cell.Diagnostic = "Completed cell staging was discarded because the cell is no longer desired.";
                    changes.Add(TransitionLocked(cell, WorldCellStreamingState.Cancelled));
                    continue;
                }

                if (m_DecodedStagingBytes + result.StagingBytes > Budgets.MaxDecodedStagingBytes)
                {
                    result.Dispose();
                    m_FailureCount++;
                    cell.Diagnostic =
                        $"Decoded staging ({result.StagingBytes} bytes) exceeds the remaining staging budget.";
                    WorldCellStreamingSnapshot failed = TransitionLocked(
                        cell,
                        WorldCellStreamingState.Failed);
                    AddDiagnosticLocked(cell);
                    changes.Add(failed);
                    continue;
                }

                cell.Staging = result.Staging;
                cell.SourceKind = result.SourceKind;
                cell.ResidencyLease = result.DetachResidencyLease()
                    ?? throw new InvalidOperationException(
                        "A completed world-cell payload did not retain its acquired residency lease.");
                cell.ResidencyLease.SetPinned(cell.Pinned);
                result.Dispose();
                cell.DecodedStagingBytes = result.StagingBytes;
                m_DecodedStagingBytes += result.StagingBytes;
                m_PeakDecodedStagingBytes = Math.Max(m_PeakDecodedStagingBytes, m_DecodedStagingBytes);
                cell.LastLoadLatencyMilliseconds = Stopwatch.GetElapsedTime(cell.RequestStartedTimestamp).TotalMilliseconds;
                m_LastLoadLatencyMilliseconds = cell.LastLoadLatencyMilliseconds;
                RuntimePreparedAssetState residencyState = cell.ResidencyLease.State;
                if (residencyState == RuntimePreparedAssetState.Failed)
                {
                    m_FailureCount++;
                    cell.Diagnostic = cell.ResidencyLease.Diagnostic;
                    ReleaseStagingLocked(cell);
                    ReleaseResidencyLocked(cell);
                    WorldCellStreamingSnapshot failed = TransitionLocked(
                        cell,
                        WorldCellStreamingState.Failed);
                    AddDiagnosticLocked(cell);
                    changes.Add(failed);
                }
                else if (residencyState == RuntimePreparedAssetState.Ready)
                {
                    cell.Diagnostic =
                        "Cell payload and required resources are ready for frame-boundary activation.";
                    changes.Add(TransitionLocked(cell, WorldCellStreamingState.ReadyToActivate));
                }
                else
                {
                    cell.Diagnostic =
                        "Cell payload is validated and waiting for required prepared resources.";
                    changes.Add(TransitionLocked(cell, WorldCellStreamingState.WaitingForResources));
                }
            }
        }

        Publish(changes, ref subscriberFailures);
    }

    private void ProcessWaitingResources(ref List<CapturedSubscriberFailure>? subscriberFailures)
    {
        using var _ = Profiler.Zone("WorldStreaming.WaitForResources");
        var changes = new List<WorldCellStreamingSnapshot>();
        lock (m_Gate)
        {
            foreach (RuntimeCell cell in m_Cells.Values
                         .Where(candidate =>
                             candidate.State == WorldCellStreamingState.WaitingForResources)
                         .OrderBy(candidate => candidate.Descriptor.Id))
            {
                RuntimeAssetResidencyLease? lease = cell.ResidencyLease;
                if (lease == null)
                {
                    m_FailureCount++;
                    cell.Diagnostic =
                        "Cell lost its residency lease while waiting for prepared resources.";
                    ReleaseStagingLocked(cell);
                    WorldCellStreamingSnapshot failed = TransitionLocked(
                        cell,
                        WorldCellStreamingState.Failed);
                    AddDiagnosticLocked(cell);
                    changes.Add(failed);
                    continue;
                }

                RuntimePreparedAssetState state = lease.State;
                if (state == RuntimePreparedAssetState.Waiting) continue;
                if (state == RuntimePreparedAssetState.Failed)
                {
                    m_FailureCount++;
                    cell.Diagnostic = lease.Diagnostic;
                    ReleaseStagingLocked(cell);
                    ReleaseResidencyLocked(cell);
                    WorldCellStreamingSnapshot failed = TransitionLocked(
                        cell,
                        WorldCellStreamingState.Failed);
                    AddDiagnosticLocked(cell);
                    changes.Add(failed);
                    continue;
                }

                cell.Diagnostic =
                    "Cell payload and required resources are ready for frame-boundary activation.";
                changes.Add(TransitionLocked(cell, WorldCellStreamingState.ReadyToActivate));
            }
        }

        Publish(changes, ref subscriberFailures);
    }

    private void AdmitQueuedReads(ref List<CapturedSubscriberFailure>? subscriberFailures)
    {
        var changes = new List<WorldCellStreamingSnapshot>();
        lock (m_Gate)
        {
            int activeReads = m_Cells.Values.Count(cell => cell.Task is { IsCompleted: false });
            WorldCellCoordinate sourceCoordinate = m_HasStreamingSource
                ? WorldPartitionCoordinates.GetCoordinate(m_ActiveWorld!.Partition, m_StreamingSource)
                : default;
            RuntimeCell[] queued = m_Cells.Values
                .Where(cell => cell.State == WorldCellStreamingState.Queued && cell.Task == null)
                .OrderBy(cell => cell.Pinned ? 0 : 1)
                .ThenBy(cell => m_HasStreamingSource
                    ? ChebyshevDistance(sourceCoordinate, cell.Descriptor.Key.Coordinate)
                    : int.MaxValue)
                .ThenBy(cell => GetLayerPriority(m_ActiveWorld!, cell.Descriptor.Key.Layer))
                .ThenBy(cell => cell.Descriptor.Id)
                .ToArray();
            foreach (RuntimeCell cell in queued)
            {
                long inFlightBytes = GetInFlightReservation(cell.Descriptor);
                long stagingReservation = GetStagingReservation(cell.Descriptor, inFlightBytes);
                if (inFlightBytes > Budgets.MaxBytesInFlight ||
                    stagingReservation > Budgets.MaxDecodedStagingBytes)
                {
                    m_FailureCount++;
                    cell.Diagnostic =
                        $"Cell requires {inFlightBytes} in-flight bytes and {stagingReservation} staging bytes, " +
                        $"exceeding configured limits {Budgets.MaxBytesInFlight} and " +
                        $"{Budgets.MaxDecodedStagingBytes}.";
                    WorldCellStreamingSnapshot failed = TransitionLocked(
                        cell,
                        WorldCellStreamingState.Failed);
                    AddDiagnosticLocked(cell);
                    changes.Add(failed);
                    continue;
                }

                if (activeReads >= Budgets.MaxConcurrentReads ||
                    m_BytesInFlight + inFlightBytes > Budgets.MaxBytesInFlight ||
                    m_ReservedStagingBytes + m_DecodedStagingBytes + stagingReservation > Budgets.MaxDecodedStagingBytes)
                {
                    m_BudgetStallCount++;
                    continue;
                }

                cell.RequestGeneration++;
                cell.ScheduledGeneration = cell.RequestGeneration;
                cell.RequestStartedTimestamp = Stopwatch.GetTimestamp();
                cell.BytesInFlight = inFlightBytes;
                cell.StagingReservationBytes = stagingReservation;
                m_BytesInFlight += inFlightBytes;
                m_ReservedStagingBytes += stagingReservation;
                m_PeakBytesInFlight = Math.Max(m_PeakBytesInFlight, m_BytesInFlight);
                long generation = cell.ScheduledGeneration;
                Guid worldGuid = m_ActiveWorld!.WorldGuid;
                cell.Task = m_Scheduler.Schedule(
                    $"WorldStreaming.CellRead/{cell.Descriptor.Id}",
                    cancellationToken => LoadCellAndAcquireResidency(
                        cell.Descriptor,
                        worldGuid,
                        generation,
                        cell.Pinned,
                        cell.PreviewSource,
                        cancellationToken));
                cell.TaskSequence = cell.Task.Sequence;
                activeReads++;
                changes.Add(SnapshotLocked(cell));
            }
        }

        Publish(changes, ref subscriberFailures);
    }

    private CellPayloadLoadResult LoadCellAndAcquireResidency(
        WorldCellDescriptor cell,
        Guid worldGuid,
        long generation,
        bool pinned,
        SceneSourceSnapshot? previewSource,
        CancellationToken cancellationToken)
    {
        CellPayloadLoadResult result = m_PayloadLoader.Load(
            cell,
            generation,
            previewSource,
            state => UpdateWorkerState(cell.Id, generation, state),
            cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var _ = Profiler.Zone("WorldStreaming.AcquireResidency");
            RuntimeAssetResidencyLease lease = m_ResidencyService.AcquireSceneDependencies(
                RuntimeAssetResidencyOwnerId.Cell(
                    worldGuid,
                    cell.Id,
                    generation),
                SceneAssetCooker.GetDependencies(result.Staging),
                pinned,
                cancellationToken);
            result.AttachResidencyLease(lease);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private void ActivateReadyCells(ref List<CapturedSubscriberFailure>? subscriberFailures)
    {
        using var _ = Profiler.Zone("WorldStreaming.Activate");
        long started = Stopwatch.GetTimestamp();
        int activated = 0;
        while (activated < Budgets.MaxActivationsPerFrame &&
               Stopwatch.GetElapsedTime(started).TotalMilliseconds < Budgets.MaxActivationMilliseconds)
        {
            RuntimeCell? cell;
            long activationClaim = 0;
            SceneStagingData? stagedScene = null;
            string sourceKind = string.Empty;
            WorldDescriptor? activeWorld = null;
            lock (m_Gate)
            {
                if (m_ShuttingDown || m_ActiveWorld == null) break;

                int activeCount = m_Cells.Values.Count(candidate =>
                    candidate.State == WorldCellStreamingState.Active ||
                    candidate.ActivationClaim != 0);
                int desiredCount = m_Cells.Values.Count(candidate => candidate.Desired);
                int activeLimit = Math.Max(m_ActiveWorld!.Partition.MaxActiveCells, desiredCount);
                if (activeCount >= activeLimit)
                {
                    m_BudgetStallCount++;
                    break;
                }

                WorldCellCoordinate sourceCoordinate = m_HasStreamingSource
                    ? WorldPartitionCoordinates.GetCoordinate(m_ActiveWorld!.Partition, m_StreamingSource)
                    : default;
                cell = m_Cells.Values
                    .Where(candidate =>
                        candidate.State == WorldCellStreamingState.ReadyToActivate &&
                        candidate.ActivationClaim == 0 &&
                        candidate.Descriptor.Dependencies.All(dependency =>
                            m_Cells[dependency].State == WorldCellStreamingState.Active))
                    .OrderBy(candidate => candidate.Pinned ? 0 : 1)
                    .ThenBy(candidate => m_HasStreamingSource
                        ? ChebyshevDistance(sourceCoordinate, candidate.Descriptor.Key.Coordinate)
                        : int.MaxValue)
                    .ThenBy(candidate => candidate.Descriptor.Id)
                    .FirstOrDefault();
                if (cell != null)
                {
                    activationClaim = ++m_NextActivationClaim;
                    cell.ActivationClaim = activationClaim;
                    stagedScene = cell.Staging
                        ?? throw new InvalidOperationException(
                            $"Ready world cell '{cell.Descriptor.Id}' has no staged scene payload.");
                    sourceKind = cell.SourceKind;
                    activeWorld = m_ActiveWorld;
                }
            }

            if (cell == null) break;
            using var cellZone = Profiler.Zone(
                $"WorldStreaming.Activate/{cell.Descriptor.Id}");
            var scene = new AssetRef<SceneSourceAsset>(
                cell.Descriptor.Scene.Guid,
                "Scene",
                cell.Descriptor.Scene.PackageId);
            long activationStarted = Stopwatch.GetTimestamp();
            SceneStagingData placedStaging;
            WorldPosition cellOrigin;
            try
            {
                cellOrigin = WorldPartitionCoordinates.GetCellOrigin(
                    activeWorld!.Partition,
                    cell.Descriptor.Key.Coordinate);
                placedStaging = SceneStagingPlacement.PlaceCell(
                    stagedScene!,
                    cellOrigin,
                    m_OriginService.CurrentOrigin);
            }
            catch (Exception ex)
            {
                var changes = new List<WorldCellStreamingSnapshot>(2);
                lock (m_Gate)
                {
                    EnsureActivationClaimLocked(cell, activationClaim);
                    if (IsActivationSupersededLocked(cell))
                    {
                        CompleteSupersededActivationLocked(
                            cell,
                            $"Superseded cell activation was discarded after placement failed: {ex.Message}",
                            changes);
                    }
                    else
                    {
                        CompleteFailedActivationLocked(
                            cell,
                            $"Cell placement failed: {ex.Message}",
                            changes);
                    }
                }
                Publish(changes, ref subscriberFailures);
                activated++;
                continue;
            }

            var activation = m_SceneService.ActivatePreparedAdditiveAtFrameBoundary(
                scene,
                placedStaging,
                sourceKind,
                new SceneComponentActivationContext(
                    cell.Descriptor.Id,
                    cellOrigin,
                    cell.Descriptor.Bounds));
            double activationMilliseconds = Stopwatch.GetElapsedTime(activationStarted).TotalMilliseconds;
            var activationChanges = new List<WorldCellStreamingSnapshot>(2);
            bool unloadSupersededInstance = false;
            lock (m_Gate)
            {
                EnsureActivationClaimLocked(cell, activationClaim);
                m_LastActivationMilliseconds = activationMilliseconds;
                if (activation.Result.Success)
                {
                    cell.SceneInstanceId = activation.InstanceId;
                    if (IsActivationSupersededLocked(cell))
                    {
                        unloadSupersededInstance = true;
                    }
                    else
                    {
                        ReleaseActivationClaimLocked(cell);
                        cell.UnloadBlocked = false;
                        cell.Diagnostic = activation.Result.Diagnostic;
                        ReleaseStagingLocked(cell);
                        activationChanges.Add(TransitionLocked(
                            cell,
                            WorldCellStreamingState.Active));
                    }
                }
                else if (IsActivationSupersededLocked(cell))
                {
                    CompleteSupersededActivationLocked(
                        cell,
                        "Superseded cell activation was discarded after scene activation rejected the payload.",
                        activationChanges);
                }
                else
                {
                    CompleteFailedActivationLocked(
                        cell,
                        activation.Result.Diagnostic,
                        activationChanges);
                }
            }

            if (unloadSupersededInstance)
            {
                long unloadStarted = Stopwatch.GetTimestamp();
                bool unloaded = m_SceneService.UnloadSceneAtFrameBoundary(
                    activation.InstanceId,
                    out string unloadDiagnostic);
                double unloadMilliseconds = Stopwatch.GetElapsedTime(unloadStarted).TotalMilliseconds;
                lock (m_Gate)
                {
                    EnsureActivationClaimLocked(cell, activationClaim);
                    m_LastUnloadMilliseconds = unloadMilliseconds;
                    if (unloaded)
                    {
                        CompleteSupersededActivationLocked(
                            cell,
                            "Superseded cell activation was unloaded before its staging and residency ownership were released.",
                            activationChanges);
                    }
                    else
                    {
                        ReleaseActivationClaimLocked(cell);
                        cell.UnloadBlocked = true;
                        cell.ReloadRequested = true;
                        cell.Diagnostic =
                            $"Superseded cell activation could not be unloaded and remains owned: {unloadDiagnostic}";
                        ReleaseStagingLocked(cell);
                        m_FailureCount++;
                        activationChanges.Add(TransitionLocked(
                            cell,
                            WorldCellStreamingState.Active));
                        AddDiagnosticLocked(cell);
                    }
                }
            }

            Publish(activationChanges, ref subscriberFailures);
            activated++;
        }
    }

    private static void EnsureActivationClaimLocked(RuntimeCell cell, long activationClaim)
    {
        if (activationClaim == 0 || cell.ActivationClaim != activationClaim ||
            cell.State != WorldCellStreamingState.ReadyToActivate)
        {
            throw new InvalidOperationException(
                $"World cell '{cell.Descriptor.Id}' lost activation claim {activationClaim} while activation owned its staging and residency.");
        }
    }

    private bool IsActivationSupersededLocked(RuntimeCell cell) =>
        m_ShuttingDown || cell.ReloadRequested || !cell.Desired;

    private void ReleaseActivationClaimLocked(RuntimeCell cell)
    {
        if (cell.ActivationClaim == 0)
        {
            throw new InvalidOperationException(
                $"World cell '{cell.Descriptor.Id}' has no activation claim to release.");
        }

        cell.ActivationClaim = 0;
    }

    private void CompleteSupersededActivationLocked(
        RuntimeCell cell,
        string diagnostic,
        List<WorldCellStreamingSnapshot> changes)
    {
        ReleaseActivationClaimLocked(cell);
        cell.SceneInstanceId = RuntimeSceneInstanceId.Invalid;
        cell.UnloadBlocked = false;
        cell.ReloadRequested = false;
        cell.Diagnostic = diagnostic;
        ReleaseStagingLocked(cell);
        ReleaseResidencyLocked(cell);
        changes.Add(TransitionLocked(cell, WorldCellStreamingState.Cancelled));
        if (!m_ShuttingDown && (cell.Desired || cell.Pinned))
        {
            changes.Add(TransitionLocked(cell, WorldCellStreamingState.Queued));
        }
    }

    private void CompleteFailedActivationLocked(
        RuntimeCell cell,
        string diagnostic,
        List<WorldCellStreamingSnapshot> changes)
    {
        ReleaseActivationClaimLocked(cell);
        m_FailureCount++;
        cell.Diagnostic = diagnostic;
        ReleaseStagingLocked(cell);
        ReleaseResidencyLocked(cell);
        changes.Add(TransitionLocked(cell, WorldCellStreamingState.Failed));
        AddDiagnosticLocked(cell);
    }

    private void UnloadUndesiredCells(ref List<CapturedSubscriberFailure>? subscriberFailures)
    {
        using var _ = Profiler.Zone("WorldStreaming.Unload");
        int unloaded = 0;
        while (unloaded < Budgets.MaxUnloadsPerFrame)
        {
            RuntimeCell? cell;
            WorldCellStreamingSnapshot queued;
            WorldCellStreamingSnapshot unloading;
            lock (m_Gate)
            {
                cell = m_Cells.Values
                    .Where(candidate =>
                        candidate.State == WorldCellStreamingState.Active &&
                        (candidate.ReloadRequested || !candidate.Desired) &&
                        !candidate.UnloadBlocked &&
                        !m_Cells.Values.Any(other =>
                            other.State == WorldCellStreamingState.Active &&
                            other.Descriptor.Dependencies.Contains(candidate.Descriptor.Id)))
                    .OrderByDescending(candidate => candidate.Descriptor.Dependencies.Count)
                    .ThenByDescending(candidate => candidate.Descriptor.Id)
                    .FirstOrDefault();
                if (cell == null) break;
                queued = TransitionLocked(cell, WorldCellStreamingState.QueuedToUnload);
                unloading = TransitionLocked(cell, WorldCellStreamingState.Unloading);
            }

            Publish(queued, ref subscriberFailures);
            Publish(unloading, ref subscriberFailures);
            using var cellZone = Profiler.Zone(
                $"WorldStreaming.Unload/{cell.Descriptor.Id}");
            long unloadStarted = Stopwatch.GetTimestamp();
            bool success = m_SceneService.UnloadSceneAtFrameBoundary(
                cell.SceneInstanceId,
                out string diagnostic);
            double unloadMilliseconds = Stopwatch.GetElapsedTime(unloadStarted).TotalMilliseconds;
            WorldCellStreamingSnapshot completed;
            lock (m_Gate)
            {
                m_LastUnloadMilliseconds = unloadMilliseconds;
                cell.Diagnostic = diagnostic;
                if (success)
                {
                    cell.SceneInstanceId = RuntimeSceneInstanceId.Invalid;
                    cell.UnloadBlocked = false;
                    cell.ReloadRequested = false;
                    ReleaseResidencyLocked(cell);
                    completed = TransitionLocked(cell, WorldCellStreamingState.Unloaded);
                }
                else
                {
                    m_FailureCount++;
                    cell.UnloadBlocked = true;
                    completed = TransitionLocked(cell, WorldCellStreamingState.Active);
                    AddDiagnosticLocked(cell);
                }
            }

            Publish(completed, ref subscriberFailures);
            unloaded++;
        }
    }

    private void UpdateWorkerState(
        WorldCellId cellId,
        long generation,
        WorldCellStreamingState state)
    {
        lock (m_Gate)
        {
            if (!m_Cells.TryGetValue(cellId, out RuntimeCell? cell) ||
                cell.RequestGeneration != generation ||
                cell.State == WorldCellStreamingState.Cancelled)
            {
                return;
            }

            m_PendingWorkerNotifications.Enqueue(TransitionLocked(cell, state));
        }
    }

    private void DrainWorkerNotificationsLocked(List<WorldCellStreamingSnapshot> destination)
    {
        while (m_PendingWorkerNotifications.Count > 0)
        {
            destination.Add(m_PendingWorkerNotifications.Dequeue());
        }
    }

    private void CollectDependencyClosureLocked(
        RuntimeCell root,
        HashSet<WorldCellId> destination)
    {
        destination.Clear();
        var pending = new Stack<WorldCellId>();
        pending.Push(root.Descriptor.Id);
        while (pending.Count > 0)
        {
            WorldCellId id = pending.Pop();
            if (!destination.Add(id)) continue;
            foreach (WorldCellId dependency in m_Cells[id].Descriptor.Dependencies)
            {
                pending.Push(dependency);
            }
        }
    }

    private WorldCellStreamingSnapshot TransitionLocked(
        RuntimeCell cell,
        WorldCellStreamingState next)
    {
        if (cell.State != next && !IsLegalTransition(cell.State, next))
        {
            throw new InvalidOperationException(
                $"Illegal world-cell transition {cell.State} -> {next} for '{cell.Descriptor.Id}'.");
        }

        cell.State = next;
        cell.TransitionSequence = ++m_NextTransitionSequence;
        return SnapshotLocked(cell);
    }

    private static bool IsLegalTransition(
        WorldCellStreamingState current,
        WorldCellStreamingState next)
    {
        return current switch
        {
            WorldCellStreamingState.Unloaded => next == WorldCellStreamingState.Queued,
            WorldCellStreamingState.Queued => next is WorldCellStreamingState.Reading or WorldCellStreamingState.Cancelled or WorldCellStreamingState.Failed,
            WorldCellStreamingState.Reading => next is WorldCellStreamingState.Decoding or WorldCellStreamingState.Cancelled or WorldCellStreamingState.Failed,
            WorldCellStreamingState.Decoding => next is WorldCellStreamingState.Validating or WorldCellStreamingState.Cancelled or WorldCellStreamingState.Failed,
            WorldCellStreamingState.Validating => next is WorldCellStreamingState.WaitingForResources or WorldCellStreamingState.ReadyToActivate or WorldCellStreamingState.Cancelled or WorldCellStreamingState.Failed,
            WorldCellStreamingState.WaitingForResources => next is WorldCellStreamingState.ReadyToActivate or WorldCellStreamingState.Cancelled or WorldCellStreamingState.Failed,
            WorldCellStreamingState.ReadyToActivate => next is WorldCellStreamingState.Active or WorldCellStreamingState.Cancelled or WorldCellStreamingState.Failed,
            WorldCellStreamingState.Active => next == WorldCellStreamingState.QueuedToUnload,
            WorldCellStreamingState.QueuedToUnload => next is WorldCellStreamingState.Unloading or WorldCellStreamingState.Active,
            WorldCellStreamingState.Unloading => next is WorldCellStreamingState.Unloaded or WorldCellStreamingState.Active,
            WorldCellStreamingState.Failed => next is WorldCellStreamingState.Queued or WorldCellStreamingState.Unloaded,
            WorldCellStreamingState.Cancelled => next is WorldCellStreamingState.Queued or WorldCellStreamingState.Unloaded,
            _ => false
        };
    }

    private void ReleaseStagingLocked(RuntimeCell cell)
    {
        m_DecodedStagingBytes -= cell.DecodedStagingBytes;
        cell.DecodedStagingBytes = 0;
        cell.Staging = null;
        cell.SourceKind = string.Empty;
    }

    private static void ReleaseResidencyLocked(RuntimeCell cell)
    {
        cell.ResidencyLease?.Dispose();
        cell.ResidencyLease = null;
    }

    private void AddDiagnosticLocked(RuntimeCell cell)
    {
        EnqueueDiagnosticLocked(new WorldStreamingDiagnostic(
            ++m_NextDiagnosticSequence,
            cell.Descriptor.Id,
            cell.State,
            cell.RequestGeneration,
            cell.Diagnostic));
    }

    private void EnqueueDiagnosticLocked(WorldStreamingDiagnostic diagnostic)
    {
        m_Diagnostics.Enqueue(diagnostic);
        while (m_Diagnostics.Count > MaxDiagnostics) m_Diagnostics.Dequeue();
    }

    private WorldCellStreamingSnapshot SnapshotLocked(RuntimeCell cell)
    {
        return new WorldCellStreamingSnapshot(
            cell.Descriptor.Id,
            cell.State,
            cell.RequestGeneration,
            cell.TransitionSequence,
            cell.Desired,
            cell.DesiredSources,
            cell.Pinned,
            cell.ReloadRequested,
            cell.SceneInstanceId,
            cell.BytesInFlight,
            cell.DecodedStagingBytes,
            cell.LastLoadLatencyMilliseconds,
            cell.Diagnostic);
    }

    private WorldStreamingMetrics BuildMetricsLocked()
    {
        return new WorldStreamingMetrics(
            m_Cells.Values.Count(cell => cell.State == WorldCellStreamingState.Queued),
            m_Cells.Values.Count(cell => cell.State == WorldCellStreamingState.Active),
            m_Cells.Values.Count(cell => cell.Task is { IsCompleted: false }),
            m_Cells.Values.Count(cell => cell.State == WorldCellStreamingState.WaitingForResources),
            m_Cells.Values.Count(cell => cell.State == WorldCellStreamingState.ReadyToActivate),
            m_Cells.Values.Count(cell => cell.State == WorldCellStreamingState.Failed),
            m_Cells.Values.Count(cell => cell.State == WorldCellStreamingState.Cancelled),
            m_BytesInFlight,
            m_DecodedStagingBytes,
            m_PeakBytesInFlight,
            m_PeakDecodedStagingBytes,
            m_CancellationCount,
            m_FailureCount,
            m_StaleCompletionCount,
            m_BudgetStallCount,
            m_LastLoadLatencyMilliseconds,
            m_LastActivationMilliseconds,
            m_LastUnloadMilliseconds,
            m_SubscriberFailureCount);
    }

    private void PlotMetrics()
    {
        WorldStreamingMetrics metrics = GetMetrics();
        Profiler.PlotValue("WorldStreaming.Queued", metrics.QueuedCells);
        Profiler.PlotValue("WorldStreaming.Active", metrics.ActiveCells);
        Profiler.PlotValue("WorldStreaming.InFlightReads", metrics.InFlightReads);
        Profiler.PlotValue("WorldStreaming.WaitingForResources", metrics.WaitingForResourcesCells);
        Profiler.PlotValue("WorldStreaming.Ready", metrics.ReadyCells);
        Profiler.PlotValue("WorldStreaming.Failed", metrics.FailedCells);
        Profiler.PlotValue("WorldStreaming.Cancelled", metrics.CancelledCells);
        Profiler.PlotValue("WorldStreaming.BytesInFlight", metrics.BytesInFlight);
        Profiler.PlotValue("WorldStreaming.DecodedStagingBytes", metrics.DecodedStagingBytes);
        Profiler.PlotValue("WorldStreaming.PeakBytesInFlight", metrics.PeakBytesInFlight);
        Profiler.PlotValue("WorldStreaming.PeakDecodedStagingBytes", metrics.PeakDecodedStagingBytes);
        Profiler.PlotValue("WorldStreaming.Cancellations", metrics.CancellationCount);
        Profiler.PlotValue("WorldStreaming.Failures", metrics.FailureCount);
        Profiler.PlotValue("WorldStreaming.SubscriberFailures", metrics.SubscriberFailureCount);
        Profiler.PlotValue("WorldStreaming.StaleCompletions", metrics.StaleCompletionCount);
        Profiler.PlotValue("WorldStreaming.BudgetStalls", metrics.BudgetStallCount);
        Profiler.PlotValue("WorldStreaming.LastLoadLatencyMs", metrics.LastLoadLatencyMilliseconds);
        Profiler.PlotValue("WorldStreaming.LastActivationMs", metrics.LastActivationMilliseconds);
        Profiler.PlotValue("WorldStreaming.LastUnloadMs", metrics.LastUnloadMilliseconds);
    }

    private void Publish(
        IEnumerable<WorldCellStreamingSnapshot> snapshots,
        ref List<CapturedSubscriberFailure>? subscriberFailures)
    {
        foreach (WorldCellStreamingSnapshot snapshot in snapshots)
        {
            Publish(snapshot, ref subscriberFailures);
        }
    }

    private void Publish(
        WorldCellStreamingSnapshot? snapshot,
        ref List<CapturedSubscriberFailure>? subscriberFailures)
    {
        if (snapshot == null) return;
        Action<WorldCellStreamingSnapshot>? handlers = CellStateChanged;
        if (handlers == null) return;
        Delegate[] subscribers = handlers.GetInvocationList();
        for (int index = 0; index < subscribers.Length; index++)
        {
            var subscriber = (Action<WorldCellStreamingSnapshot>)subscribers[index];
            try
            {
                subscriber(snapshot);
            }
            catch (Exception error)
            {
                CaptureSubscriberFailure(
                    "CellStateChanged",
                    $"Cell={snapshot.CellId}, State={snapshot.State}, " +
                    $"Generation={snapshot.RequestGeneration}, Transition={snapshot.TransitionSequence}",
                    index,
                    subscriber,
                    error,
                    ref subscriberFailures);
            }
        }
    }

    private void PublishActiveWorldChanged(
        AssetRef<WorldSourceAsset>? world,
        ref List<CapturedSubscriberFailure>? subscriberFailures)
    {
        Action<AssetRef<WorldSourceAsset>?>? handlers = ActiveWorldChanged;
        if (handlers == null) return;

        Delegate[] subscribers = handlers.GetInvocationList();
        for (int index = 0; index < subscribers.Length; index++)
        {
            var subscriber = (Action<AssetRef<WorldSourceAsset>?>)subscribers[index];
            try
            {
                subscriber(world);
            }
            catch (Exception error)
            {
                string payload = world is { } active
                    ? $"World={active.Guid:D}, Package={active.PackageId}"
                    : "World=<closed>";
                CaptureSubscriberFailure(
                    "ActiveWorldChanged",
                    payload,
                    index,
                    subscriber,
                    error,
                    ref subscriberFailures);
            }
        }
    }

    private void PublishWorldPresentationChanged(
        RuntimeWorldPresentationSnapshot snapshot,
        ref List<CapturedSubscriberFailure>? subscriberFailures)
    {
        Action<RuntimeWorldPresentationSnapshot>? handlers = WorldPresentationChanged;
        if (handlers == null) return;

        Delegate[] subscribers = handlers.GetInvocationList();
        for (int index = 0; index < subscribers.Length; index++)
        {
            var subscriber = (Action<RuntimeWorldPresentationSnapshot>)subscribers[index];
            try
            {
                subscriber(snapshot);
            }
            catch (Exception error)
            {
                CaptureSubscriberFailure(
                    "WorldPresentationChanged",
                    $"Revision={snapshot.Revision}, " +
                    $"Active={FormatWorldPresentationAsset(snapshot.ActiveWorldAsset)}, " +
                    $"Pending={FormatWorldPresentationAsset(snapshot.PendingWorldAsset)}, " +
                    $"ActiveWorldGuid={snapshot.ActiveWorldGuid:D}",
                    index,
                    subscriber,
                    error,
                    ref subscriberFailures);
            }
        }
    }

    private static string FormatWorldPresentationAsset(
        AssetRef<WorldSourceAsset>? world) =>
        world is { } value
            ? $"{value.Guid:D}/{value.PackageId}"
            : "<none>";

    private static void CaptureSubscriberFailure(
        string notification,
        string payload,
        int subscriberIndex,
        Delegate subscriber,
        Exception error,
        ref List<CapturedSubscriberFailure>? subscriberFailures)
    {
        string declaringType = subscriber.Method.DeclaringType?.FullName
            ?? subscriber.Target?.GetType().FullName
            ?? "<unknown>";
        string identity = $"#{subscriberIndex + 1} '{declaringType}.{subscriber.Method.Name}'";
        var diagnostic = new WorldStreamingSubscriberFailure(
            notification,
            payload,
            identity,
            error.GetType().FullName ?? error.GetType().Name,
            error.Message);
        (subscriberFailures ??= new List<CapturedSubscriberFailure>())
            .Add(new CapturedSubscriberFailure(diagnostic, error));
    }

    private void ReportSubscriberFailures(
        string boundary,
        List<CapturedSubscriberFailure>? capturedFailures)
    {
        if (capturedFailures == null || capturedFailures.Count == 0) return;

        var diagnostics = new WorldStreamingSubscriberFailure[capturedFailures.Count];
        var attributedErrors = new Exception[capturedFailures.Count];
        for (int index = 0; index < capturedFailures.Count; index++)
        {
            CapturedSubscriberFailure captured = capturedFailures[index];
            diagnostics[index] = captured.Diagnostic;
            attributedErrors[index] = new InvalidOperationException(
                $"World-streaming notification '{captured.Diagnostic.Notification}' subscriber " +
                $"{captured.Diagnostic.Subscriber} failed for {captured.Diagnostic.Payload}.",
                captured.Error);
        }

        var aggregate = new AggregateException(
            $"World-streaming boundary '{boundary}' completed with " +
            $"{capturedFailures.Count} subscriber callback failure(s).",
            attributedErrors);
        string message = aggregate.Message + " " + string.Join(
            " | ",
            diagnostics.Select(failure =>
                $"{failure.Notification} {failure.Subscriber} for {failure.Payload}: " +
                $"{failure.ExceptionType}: {failure.Message}"));
        lock (m_Gate)
        {
            m_SubscriberFailureCount += capturedFailures.Count;
            EnqueueDiagnosticLocked(new WorldStreamingDiagnostic(
                ++m_NextDiagnosticSequence,
                default,
                WorldCellStreamingState.Unloaded,
                0,
                message)
            {
                Kind = WorldStreamingDiagnosticKind.SubscriberAggregate,
                Boundary = boundary,
                SubscriberFailures = diagnostics
            });
        }

        try
        {
            KernelLog.Error(aggregate.ToString());
        }
        catch (Exception reportingError)
        {
            Console.Error.WriteLine(
                $"[ERROR] World-streaming subscriber aggregate logging failed: {reportingError}\n{aggregate}");
        }
    }

    private void ResetCountersLocked()
    {
        m_Cells.Clear();
        m_Diagnostics.Clear();
        m_PendingWorkerNotifications.Clear();
        m_BytesInFlight = 0;
        m_ReservedStagingBytes = 0;
        m_DecodedStagingBytes = 0;
        m_PeakBytesInFlight = 0;
        m_PeakDecodedStagingBytes = 0;
        m_CancellationCount = 0;
        m_FailureCount = 0;
        m_SubscriberFailureCount = 0;
        m_StaleCompletionCount = 0;
        m_BudgetStallCount = 0;
        m_LastLoadLatencyMilliseconds = 0;
        m_LastActivationMilliseconds = 0;
        m_LastUnloadMilliseconds = 0;
    }

    private RuntimeWorldPresentationSnapshot AdvanceWorldPresentationLocked()
    {
        m_WorldPresentationRevision = checked(m_WorldPresentationRevision + 1);
        return CreateWorldPresentationSnapshotLocked();
    }

    private RuntimeWorldPresentationSnapshot CreateWorldPresentationSnapshotLocked() => new(
        m_WorldPresentationRevision,
        m_ActiveWorldAsset,
        m_PendingWorldPresentationAsset,
        m_ActiveWorld?.WorldGuid ?? Guid.Empty);

    private static int ChebyshevDistance(WorldCellCoordinate left, WorldCellCoordinate right)
    {
        long x = Math.Abs((long)left.X - right.X);
        long y = Math.Abs((long)left.Y - right.Y);
        long z = Math.Abs((long)left.Z - right.Z);
        return (int)Math.Min(int.MaxValue, Math.Max(x, Math.Max(y, z)));
    }

    private static int GetLayerPriority(WorldDescriptor world, string layer)
    {
        for (int index = 0; index < world.Layers.Count; index++)
        {
            if (string.Equals(world.Layers[index].Id, layer, StringComparison.Ordinal))
            {
                return world.Layers[index].Priority;
            }
        }

        return int.MaxValue;
    }

    private static long GetInFlightReservation(WorldCellDescriptor cell)
    {
        if (cell.ScenePayloadBytes > 0) return cell.ScenePayloadBytes;
        return Math.Max(1, Math.Min(cell.EstimatedCpuBytes, 16L * 1024 * 1024));
    }

    private static long GetStagingReservation(WorldCellDescriptor cell, long inFlightBytes)
    {
        return Math.Max(inFlightBytes, cell.EstimatedCpuBytes);
    }

    private LifecycleOperationScope EnterLifecycleOperation(string operation)
    {
        // Residency callbacks can be waiting on an operation that already owns the
        // world lifecycle gate. Reject callback reentry before joining that wait.
        m_ResidencyService.EnsureWorldLifecycleMutationAllowed();
        int threadId = Environment.CurrentManagedThreadId;
        // Scene lifecycle notifications are published while the scene operation
        // gate is owned. Reject same-thread observer reentry before waiting on a
        // world lifecycle operation held by that scene operation.
        if (m_SceneService.IsSceneOperationOwnedByCurrentThread)
        {
            throw new InvalidOperationException(
                $"World-streaming lifecycle operation '{operation}' cannot run reentrantly " +
                "from a scene lifecycle observer.");
        }

        lock (m_LifecycleGate)
        {
            if (m_LifecycleOwnerThreadId == threadId)
            {
                throw new InvalidOperationException(
                    $"World-streaming lifecycle operation '{operation}' cannot run reentrantly " +
                    $"while '{m_ActiveLifecycleOperation}' is active.");
            }

            m_LifecycleWaiterCount++;
            try
            {
                while (m_LifecycleOwnerThreadId != 0)
                {
                    Monitor.Wait(m_LifecycleGate);
                }

                m_LifecycleOwnerThreadId = threadId;
                m_ActiveLifecycleOperation = operation;
            }
            finally
            {
                m_LifecycleWaiterCount--;
            }
        }

        return new LifecycleOperationScope(this, threadId);
    }

    private void ExitLifecycleOperation(int threadId)
    {
        lock (m_LifecycleGate)
        {
            if (m_LifecycleOwnerThreadId != threadId)
            {
                throw new InvalidOperationException(
                    "World-streaming lifecycle ownership was released by a non-owning thread.");
            }

            m_LifecycleOwnerThreadId = 0;
            m_ActiveLifecycleOperation = string.Empty;
            Monitor.PulseAll(m_LifecycleGate);
        }
    }

    private readonly struct LifecycleOperationScope : IDisposable
    {
        private readonly RuntimeWorldStreamingService m_Owner;
        private readonly int m_ThreadId;

        public LifecycleOperationScope(RuntimeWorldStreamingService owner, int threadId)
        {
            m_Owner = owner;
            m_ThreadId = threadId;
        }

        public void Dispose()
        {
            m_Owner.ExitLifecycleOperation(m_ThreadId);
        }
    }

    private sealed class RuntimeCell
    {
        public RuntimeCell(WorldCellDescriptor descriptor)
        {
            Descriptor = descriptor;
        }

        public WorldCellDescriptor Descriptor { get; }
        public WorldCellStreamingState State { get; set; } = WorldCellStreamingState.Unloaded;
        public long RequestGeneration { get; set; }
        public long ScheduledGeneration { get; set; }
        public long ActivationClaim { get; set; }
        public long TransitionSequence { get; set; }
        public bool Desired { get; set; }
        public WorldCellDesiredSource DesiredSources { get; set; }
        public bool Pinned { get; set; }
        public bool ReloadRequested { get; set; }
        public SceneSourceSnapshot? PreviewSource { get; set; }
        public BackgroundTask<CellPayloadLoadResult>? Task { get; set; }
        public long TaskSequence { get; set; }
        public long RequestStartedTimestamp { get; set; }
        public long BytesInFlight { get; set; }
        public long StagingReservationBytes { get; set; }
        public SceneStagingData? Staging { get; set; }
        public RuntimeAssetResidencyLease? ResidencyLease { get; set; }
        public string SourceKind { get; set; } = string.Empty;
        public long DecodedStagingBytes { get; set; }
        public RuntimeSceneInstanceId SceneInstanceId { get; set; }
        public bool UnloadBlocked { get; set; }
        public double LastLoadLatencyMilliseconds { get; set; }
        public string Diagnostic { get; set; } = string.Empty;
    }
}

internal sealed class CellPayloadLoadResult : IDisposable
{
    private RuntimeAssetResidencyLease? m_ResidencyLease;

    public CellPayloadLoadResult(
        SceneStagingData staging,
        string sourceKind,
        long payloadBytes,
        long stagingBytes)
    {
        Staging = staging;
        SourceKind = sourceKind;
        PayloadBytes = payloadBytes;
        StagingBytes = stagingBytes;
    }

    public SceneStagingData Staging { get; }
    public string SourceKind { get; }
    public long PayloadBytes { get; }
    public long StagingBytes { get; }

    public void AttachResidencyLease(RuntimeAssetResidencyLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (Interlocked.CompareExchange(ref m_ResidencyLease, lease, null) != null)
        {
            lease.Dispose();
            throw new InvalidOperationException(
                "A world-cell payload can retain only one residency lease.");
        }
    }

    public RuntimeAssetResidencyLease? DetachResidencyLease() =>
        Interlocked.Exchange(ref m_ResidencyLease, null);

    public void Dispose()
    {
        Interlocked.Exchange(ref m_ResidencyLease, null)?.Dispose();
    }
}

internal interface IWorldCellPayloadLoader
{
    CellPayloadLoadResult Load(
        WorldCellDescriptor cell,
        long generation,
        SceneSourceSnapshot? previewSource,
        Action<WorldCellStreamingState> reportState,
        CancellationToken cancellationToken);
}

internal sealed class WorldCellPayloadLoader : IWorldCellPayloadLoader
{
    private readonly IAssetDatabase m_AssetDatabase;

    public WorldCellPayloadLoader(IAssetDatabase assetDatabase)
    {
        m_AssetDatabase = assetDatabase;
    }

    public CellPayloadLoadResult Load(
        WorldCellDescriptor cell,
        long generation,
        SceneSourceSnapshot? previewSource,
        Action<WorldCellStreamingState> reportState,
        CancellationToken cancellationToken)
    {
        using var _ = Profiler.Zone("WorldStreaming.ReadDecodeValidate");
        var sceneRef = new AssetRef<SceneSourceAsset>(
            cell.Scene.Guid,
            "Scene",
            cell.Scene.PackageId);
        reportState(WorldCellStreamingState.Reading);
        cancellationToken.ThrowIfCancellationRequested();

        SceneStagingData staging;
        long payloadBytes;
        string sourceKind;
        if (previewSource is { } preview)
        {
            if (!m_AssetDatabase.CanReadSourceAssets)
            {
                throw new InvalidOperationException(
                    "A world-cell preview source reached a cooked-only runtime.");
            }

            reportState(WorldCellStreamingState.Decoding);
            payloadBytes = System.Text.Encoding.UTF8.GetByteCount(preview.SourceText);
            if (!SceneAssetLoader.TryBuildSceneStaging(
                    m_AssetDatabase,
                    cell.Scene.Guid,
                    preview.SourcePath,
                    preview.SourceText,
                    out staging,
                    out string diagnostic))
            {
                throw new InvalidDataException(diagnostic);
            }

            sourceKind = "editor-preview";
        }
        else if (m_AssetDatabase.CanReadSourceAssets)
        {
            AssetRecord sourceAsset;
            string source;
            using (Profiler.Zone("WorldStreaming.Read"))
            {
                if (!m_AssetDatabase.TryGetAsset(sceneRef, out AssetRecord? resolvedSourceAsset) ||
                    !File.Exists(resolvedSourceAsset.SourcePath))
                {
                    throw new FileNotFoundException(
                        $"World cell scene source '{cell.Scene.Guid:D}' is missing.");
                }

                sourceAsset = resolvedSourceAsset;
                payloadBytes = new FileInfo(sourceAsset.SourcePath).Length;
                source = File.ReadAllText(sourceAsset.SourcePath);
            }

            cancellationToken.ThrowIfCancellationRequested();
            reportState(WorldCellStreamingState.Decoding);
            using (Profiler.Zone("WorldStreaming.Decode"))
            {
                if (!SceneAssetLoader.TryBuildSceneStaging(
                        m_AssetDatabase,
                        cell.Scene.Guid,
                        sourceAsset.SourcePath,
                        source,
                        out staging,
                        out string diagnostic))
                {
                    throw new InvalidDataException(diagnostic);
                }
            }

            sourceKind = "source";
        }
        else
        {
            CookedAssetRecord artifact;
            byte[] payload;
            using (Profiler.Zone("WorldStreaming.Read"))
            {
                if (!m_AssetDatabase.TryGetCookedArtifact(
                        cell.Scene.Guid,
                        cell.Scene.Variant,
                        out CookedAssetRecord? resolvedArtifact) ||
                    !File.Exists(resolvedArtifact.Path))
                {
                    throw new FileNotFoundException(
                        $"World cell cooked scene '{cell.Scene.Guid:D}:{cell.Scene.Variant}' is missing.");
                }

                artifact = resolvedArtifact;
                payloadBytes = artifact.SizeInBytes;
                if (cell.ScenePayloadBytes > 0 && payloadBytes != cell.ScenePayloadBytes)
                {
                    throw new InvalidDataException(
                        $"World cell scene '{cell.Scene.Guid:D}' size changed from {cell.ScenePayloadBytes} to {payloadBytes} bytes.");
                }

                payload = File.ReadAllBytes(artifact.Path);
            }

            cancellationToken.ThrowIfCancellationRequested();
            reportState(WorldCellStreamingState.Decoding);
            using (Profiler.Zone("WorldStreaming.Decode"))
            {
                if (!SceneAssetCooker.TryReadPayload(
                        m_AssetDatabase,
                        cell.Scene.Guid,
                        payload,
                        artifact.Path,
                        out staging,
                        out string diagnostic))
                {
                    throw new InvalidDataException(diagnostic);
                }
            }

            sourceKind = "cooked";
        }

        cancellationToken.ThrowIfCancellationRequested();
        reportState(WorldCellStreamingState.Validating);
        using (Profiler.Zone("WorldStreaming.Validate"))
        {
            for (int index = 0; index < staging.Entities.Length; index++)
            {
                if (!SceneStagingValidation.TryValidate(
                        staging.Entities[index],
                        index,
                        staging.DiagnosticPath,
                        out string diagnostic))
                {
                    throw new InvalidDataException(diagnostic);
                }
            }

            if (!SceneAssetLoader.TryValidateStagedHierarchy(staging, out string hierarchyDiagnostic))
            {
                throw new InvalidDataException(hierarchyDiagnostic);
            }
        }

        long stagingBytes = checked(payloadBytes + (staging.Entities.LongLength * 256));
        return new CellPayloadLoadResult(staging, sourceKind, payloadBytes, stagingBytes);
    }
}
