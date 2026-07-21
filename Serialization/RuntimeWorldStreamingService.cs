using System.Diagnostics;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Threading;

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
    double LastUnloadMilliseconds);

public sealed record WorldStreamingDiagnostic(
    long Sequence,
    WorldCellId CellId,
    WorldCellStreamingState State,
    long RequestGeneration,
    string Message);

public readonly record struct RuntimeWorldLoadResult(
    bool Success,
    Guid WorldGuid,
    int CellCount,
    string Diagnostic);

public interface IRuntimeWorldStreamingService
{
    WorldDescriptor? ActiveWorld { get; }
    AssetRef<WorldSourceAsset>? ActiveWorldAsset { get; }
    WorldStreamingBudgets Budgets { get; }

    event Action<WorldCellStreamingSnapshot>? CellStateChanged;
    event Action<AssetRef<WorldSourceAsset>?>? ActiveWorldChanged;

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
    private long m_NextTransitionSequence;
    private long m_NextDiagnosticSequence;
    private long m_BytesInFlight;
    private long m_ReservedStagingBytes;
    private long m_DecodedStagingBytes;
    private long m_PeakBytesInFlight;
    private long m_PeakDecodedStagingBytes;
    private long m_CancellationCount;
    private long m_FailureCount;
    private long m_StaleCompletionCount;
    private long m_BudgetStallCount;
    private double m_LastLoadLatencyMilliseconds;
    private double m_LastActivationMilliseconds;
    private double m_LastUnloadMilliseconds;
    private RuntimeAssetResidencyLease? m_PersistentResidencyLease;

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

    public WorldStreamingBudgets Budgets { get; private set; }

    public IRuntimeAssetResidencyService Residency => m_ResidencyService;

    public event Action<WorldCellStreamingSnapshot>? CellStateChanged;
    public event Action<AssetRef<WorldSourceAsset>?>? ActiveWorldChanged;

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
        if (!world.IsValid)
        {
            return new RuntimeWorldLoadResult(false, Guid.Empty, 0, "World asset ref is empty.");
        }

        Shutdown(unloadActiveCells: true);
        WorldDescriptorLoadResult loaded = m_AssetDatabase.CanReadSourceAssets
            ? WorldDescriptorLoader.LoadSource(m_AssetDatabase, world)
            : WorldAssetCooker.LoadCooked(m_AssetDatabase, world);
        if (!loaded.Success || loaded.Descriptor == null)
        {
            lock (m_Gate) m_ShuttingDown = false;
            return new RuntimeWorldLoadResult(false, world.Guid, 0, loaded.Diagnostic);
        }

        WorldDescriptor descriptor = loaded.Descriptor;
        m_OriginService.ConfigureForWorld(descriptor.Partition);
        SceneLoadResult persistent = m_SceneService.LoadScene(new AssetRef<SceneSourceAsset>(
            descriptor.PersistentScene.Guid,
            "Scene",
            descriptor.PersistentScene.PackageId));
        if (!persistent.Success)
        {
            lock (m_Gate) m_ShuttingDown = false;
            return new RuntimeWorldLoadResult(false, world.Guid, 0, persistent.Diagnostic);
        }

        RuntimeSceneState persistentState = m_SceneService.ActiveScene
            ?? throw new InvalidOperationException(
                "A successful persistent world-scene load did not publish an active scene state.");
        if (!m_SceneService.TryGetSceneInstance(
                persistentState.InstanceId,
                out RuntimeSceneInstanceSnapshot persistentSnapshot))
        {
            m_SceneService.UnloadSceneAtFrameBoundary(persistentState.InstanceId, out _);
            lock (m_Gate) m_ShuttingDown = false;
            return new RuntimeWorldLoadResult(
                false,
                world.Guid,
                0,
                "Persistent world-scene dependencies were unavailable after activation.");
        }

        RuntimeAssetResidencyLease persistentLease;
        try
        {
            persistentLease = m_ResidencyService.AcquireSceneDependencies(
                RuntimeAssetResidencyOwnerId.Persistent(descriptor.WorldGuid),
                persistentSnapshot.Dependencies,
                pinned: true);
        }
        catch (Exception ex)
        {
            m_SceneService.UnloadSceneAtFrameBoundary(persistentState.InstanceId, out _);
            lock (m_Gate) m_ShuttingDown = false;
            return new RuntimeWorldLoadResult(
                false,
                world.Guid,
                0,
                $"Persistent world-scene residency acquisition failed: {ex.Message}");
        }

        lock (m_Gate)
        {
            ResetCountersLocked();
            m_ActiveWorld = descriptor;
            m_ActiveWorldAsset = world;
            m_PersistentResidencyLease = persistentLease;
            m_ShuttingDown = false;
            foreach (WorldCellDescriptor cell in descriptor.Cells)
            {
                m_Cells.Add(cell.Id, new RuntimeCell(cell));
            }
        }

        PlotMetrics();
        ActiveWorldChanged?.Invoke(world);
        return new RuntimeWorldLoadResult(true, descriptor.WorldGuid, descriptor.Cells.Count, string.Empty);
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

        return RequestCellReload(cellId);
    }

    public bool RequestCellReload(WorldCellId cellId)
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

        Publish(changes);
        return true;
    }

    public bool RetryCell(WorldCellId cellId)
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

        Publish(changed);
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

    internal void ProcessAtFrameBoundary()
    {
        using var _ = Profiler.Zone("WorldStreaming.FrameBoundary");
        lock (m_Gate)
        {
            if (m_ActiveWorld == null || m_ShuttingDown) return;
        }

        if (m_SceneService.ActiveScene is { EntityManager: { } entityManager })
        {
            m_OriginService.ProcessAtFrameBoundary(entityManager);
        }
        PlanDesiredCells();
        ProcessCompletedReads();
        m_ResidencyService.ProcessAtFrameBoundary();
        ProcessWaitingResources();
        AdmitQueuedReads();
        UnloadUndesiredCells();
        ActivateReadyCells();
        PlotMetrics();
    }

    internal void Shutdown(bool unloadActiveCells)
    {
        BackgroundTask<CellPayloadLoadResult>[] tasks;
        RuntimeCell[] activeCells;
        bool publishWorldClosed;
        lock (m_Gate)
        {
            m_ShuttingDown = true;
            tasks = m_Cells.Values
                .Where(cell => cell.Task != null)
                .Select(cell => cell.Task!)
                .ToArray();
            foreach (BackgroundTask<CellPayloadLoadResult> task in tasks) task.Cancel();
            activeCells = m_Cells.Values
                .Where(cell => cell.State == WorldCellStreamingState.Active)
                .OrderByDescending(cell => cell.Descriptor.Dependencies.Count)
                .ThenByDescending(cell => cell.Descriptor.Id)
                .ToArray();
        }

        foreach (BackgroundTask<CellPayloadLoadResult> task in tasks)
        {
            task.Wait();
            if (task.TryGetResult(out CellPayloadLoadResult completed))
            {
                completed.Dispose();
            }
        }

        if (unloadActiveCells)
        {
            foreach (RuntimeCell cell in activeCells)
            {
                m_SceneService.UnloadSceneAtFrameBoundary(cell.SceneInstanceId, out _);
            }

            if (m_SceneService.ActiveScene is { InstanceId.IsValid: true } persistent)
            {
                m_SceneService.UnloadSceneAtFrameBoundary(persistent.InstanceId, out _);
            }
        }

        RuntimeAssetResidencyLease[] leases;
        RuntimeAssetResidencyLease? persistentLease;
        lock (m_Gate)
        {
            leases = m_Cells.Values
                .Where(cell => cell.ResidencyLease != null)
                .Select(cell => cell.ResidencyLease!)
                .ToArray();
            persistentLease = m_PersistentResidencyLease;
            m_PersistentResidencyLease = null;
            m_Cells.Clear();
            m_PendingWorkerNotifications.Clear();
            publishWorldClosed = m_ActiveWorld != null || m_ActiveWorldAsset.HasValue;
            m_ActiveWorld = null;
            m_ActiveWorldAsset = null;
            m_HasStreamingSource = false;
            m_BytesInFlight = 0;
            m_ReservedStagingBytes = 0;
            m_DecodedStagingBytes = 0;
        }

        foreach (RuntimeAssetResidencyLease lease in leases) lease.Dispose();
        persistentLease?.Dispose();
        m_ResidencyService.ProcessAtFrameBoundary();
        if (publishWorldClosed) ActiveWorldChanged?.Invoke(null);
    }

    private void PlanDesiredCells()
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
            var dependencyClosure = new HashSet<WorldCellId>();
            foreach (RuntimeCell pinned in m_Cells.Values
                         .Where(cell => cell.Pinned)
                         .OrderBy(cell => cell.Descriptor.Id))
            {
                CollectDependencyClosureLocked(pinned, dependencyClosure);
                selected.UnionWith(dependencyClosure);
            }

            if (m_HasStreamingSource)
            {
                RuntimeCell[] cameraCandidates = m_Cells.Values
                    .Where(cell =>
                    {
                        bool activeLike = cell.State is
                            WorldCellStreamingState.Active or
                            WorldCellStreamingState.QueuedToUnload or
                            WorldCellStreamingState.Unloading;
                        int radius = world.Partition.LoadRadius +
                            (activeLike ? world.Partition.UnloadHysteresis : 0);
                        return !cell.Pinned && ChebyshevDistance(
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
                }
            }

            foreach (RuntimeCell cell in m_Cells.Values.OrderBy(cell => cell.Descriptor.Id))
            {
                cell.Desired = selected.Contains(cell.Descriptor.Id);
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
                    continue;
                }

                if (cell.Task != null && !cell.Task.IsCompleted)
                {
                    cell.RequestGeneration++;
                    cell.Task.Cancel();
                    m_CancellationCount++;
                    cell.Diagnostic = "Cell request was cancelled because it left the desired set.";
                    changes.Add(TransitionLocked(cell, WorldCellStreamingState.Cancelled));
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
                }
                else if (cell.State == WorldCellStreamingState.Queued && cell.Task == null)
                {
                    m_CancellationCount++;
                    cell.Diagnostic = "Queued cell request was cancelled because it left the desired set.";
                    changes.Add(TransitionLocked(cell, WorldCellStreamingState.Cancelled));
                }
            }
        }

        Publish(changes);
    }

    private void ProcessCompletedReads()
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

        Publish(changes);
    }

    private void ProcessWaitingResources()
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

        Publish(changes);
    }

    private void AdmitQueuedReads()
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

        Publish(changes);
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

    private void ActivateReadyCells()
    {
        using var _ = Profiler.Zone("WorldStreaming.Activate");
        long started = Stopwatch.GetTimestamp();
        int activated = 0;
        while (activated < Budgets.MaxActivationsPerFrame &&
               Stopwatch.GetElapsedTime(started).TotalMilliseconds < Budgets.MaxActivationMilliseconds)
        {
            RuntimeCell? cell;
            lock (m_Gate)
            {
                int activeCount = m_Cells.Values.Count(candidate =>
                    candidate.State == WorldCellStreamingState.Active);
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
                        candidate.Descriptor.Dependencies.All(dependency =>
                            m_Cells[dependency].State == WorldCellStreamingState.Active))
                    .OrderBy(candidate => candidate.Pinned ? 0 : 1)
                    .ThenBy(candidate => m_HasStreamingSource
                        ? ChebyshevDistance(sourceCoordinate, candidate.Descriptor.Key.Coordinate)
                        : int.MaxValue)
                    .ThenBy(candidate => candidate.Descriptor.Id)
                    .FirstOrDefault();
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
            try
            {
                WorldDescriptor activeWorld;
                lock (m_Gate) activeWorld = m_ActiveWorld!;
                placedStaging = SceneStagingPlacement.PlaceCell(
                    cell.Staging!,
                    WorldPartitionCoordinates.GetCellOrigin(
                        activeWorld.Partition,
                        cell.Descriptor.Key.Coordinate),
                    m_OriginService.CurrentOrigin);
            }
            catch (Exception ex)
            {
                WorldCellStreamingSnapshot failed;
                lock (m_Gate)
                {
                    m_FailureCount++;
                    cell.Diagnostic = $"Cell placement failed: {ex.Message}";
                    ReleaseStagingLocked(cell);
                    ReleaseResidencyLocked(cell);
                    failed = TransitionLocked(cell, WorldCellStreamingState.Failed);
                    AddDiagnosticLocked(cell);
                }
                Publish(failed);
                activated++;
                continue;
            }

            var activation = m_SceneService.ActivatePreparedAdditiveAtFrameBoundary(
                scene,
                placedStaging,
                cell.SourceKind);
            double activationMilliseconds = Stopwatch.GetElapsedTime(activationStarted).TotalMilliseconds;
            WorldCellStreamingSnapshot changed;
            lock (m_Gate)
            {
                m_LastActivationMilliseconds = activationMilliseconds;
                if (activation.Result.Success)
                {
                    cell.SceneInstanceId = activation.InstanceId;
                    cell.UnloadBlocked = false;
                    cell.Diagnostic = activation.Result.Diagnostic;
                    ReleaseStagingLocked(cell);
                    changed = TransitionLocked(cell, WorldCellStreamingState.Active);
                }
                else
                {
                    m_FailureCount++;
                    cell.Diagnostic = activation.Result.Diagnostic;
                    ReleaseStagingLocked(cell);
                    ReleaseResidencyLocked(cell);
                    changed = TransitionLocked(cell, WorldCellStreamingState.Failed);
                    AddDiagnosticLocked(cell);
                }
            }

            Publish(changed);
            activated++;
        }
    }

    private void UnloadUndesiredCells()
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

            Publish(queued);
            Publish(unloading);
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

            Publish(completed);
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
        m_Diagnostics.Enqueue(new WorldStreamingDiagnostic(
            ++m_NextDiagnosticSequence,
            cell.Descriptor.Id,
            cell.State,
            cell.RequestGeneration,
            cell.Diagnostic));
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
            m_LastUnloadMilliseconds);
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
        Profiler.PlotValue("WorldStreaming.StaleCompletions", metrics.StaleCompletionCount);
        Profiler.PlotValue("WorldStreaming.BudgetStalls", metrics.BudgetStallCount);
        Profiler.PlotValue("WorldStreaming.LastLoadLatencyMs", metrics.LastLoadLatencyMilliseconds);
        Profiler.PlotValue("WorldStreaming.LastActivationMs", metrics.LastActivationMilliseconds);
        Profiler.PlotValue("WorldStreaming.LastUnloadMs", metrics.LastUnloadMilliseconds);
    }

    private void Publish(IEnumerable<WorldCellStreamingSnapshot> snapshots)
    {
        foreach (WorldCellStreamingSnapshot snapshot in snapshots) Publish(snapshot);
    }

    private void Publish(WorldCellStreamingSnapshot? snapshot)
    {
        if (snapshot == null) return;
        Action<WorldCellStreamingSnapshot>? handlers = CellStateChanged;
        if (handlers == null) return;
        foreach (Action<WorldCellStreamingSnapshot> handler in handlers.GetInvocationList())
        {
            try { handler(snapshot); }
            catch { }
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
        m_StaleCompletionCount = 0;
        m_BudgetStallCount = 0;
        m_LastLoadLatencyMilliseconds = 0;
        m_LastActivationMilliseconds = 0;
        m_LastUnloadMilliseconds = 0;
    }

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
        public long TransitionSequence { get; set; }
        public bool Desired { get; set; }
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
