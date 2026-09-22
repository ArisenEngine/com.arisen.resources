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
    // The shadow captures stand on the authored viewer pose so their cascades frame what a player
    // sees, and the outdoor-atmosphere gate reads a distance-haze progression across the near, mid,
    // and far ones: pulling the camera back has to brighten the frame. That reading only holds while
    // every derived pose stands clear of the region's vegetation, because a pose inside a canopy
    // fills the frame with near foliage and inverts the progression. The authored view direction
    // runs through a grove between 18 and 34 world units behind the camera, so the middle sample
    // steps past it rather than into it: at 40 units the pose carries the canopy share the authored
    // pose has - 19 percent of the frame occluded closer than 20 m, against the authored pose's 19
    // percent and the far pose's 11 percent - and the retreat samples stay 40 and 64 units out.
    private const float MidShadowCameraRetreat = 40.0f;
    private const float FarShadowCameraRetreat = 64.0f;

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
    private WorldPosition m_AdmissionSource;
    private WorldPosition m_ViewSource;
    private WorldPosition m_CancellationSource;
    private WorldPosition m_FarSource;
    private Entity m_ValidationCamera;
    private WorldPosition m_ValidationCameraWorldPosition;
    private Quaternion m_ValidationCameraRotation;
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
    private bool m_HasValidationCamera;

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
        if (!TryBeginAfterStartupWorldReady(initialFrameIndex))
        {
            m_Stage = WorldStreamingSmokeStage.AwaitStartupWorld;
        }
    }

    private bool TryBeginAfterStartupWorldReady(uint frameIndex)
    {
        WorldDescriptor? world = m_Streaming.ActiveWorld;
        EntityManager? entityManager = m_Scenes.ActiveScene?.EntityManager;
        if (world == null || entityManager == null)
        {
            return false;
        }

        m_World = world;
        m_EntityManager = entityManager;

        SelectValidationCells(m_World);
        ConfigureValidationBudgets();
        CapturePersistentWorldPositions();
        CaptureValidationCamera();
        m_Streaming.CellStateChanged += OnCellStateChanged;
        m_Origin.RebaseStarting += OnRebaseStarting;
        m_Origin.Rebased += OnRebased;

        ScheduleVisualCapture("before", checked(frameIndex + 1));
        m_Stage = WorldStreamingSmokeStage.AwaitBeforeCapture;
        return true;
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
        if (m_Stage == WorldStreamingSmokeStage.AwaitStartupWorld)
        {
            TryBeginAfterStartupWorldReady(frameIndex);
            return;
        }

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
            case WorldStreamingSmokeStage.AwaitDuringSettle:
                ObserveDuringSettle(frameIndex);
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
            case WorldStreamingSmokeStage.AwaitShadowNearCapture:
                if (VisualCaptureCompleted("shadow-near"))
                {
                    BeginShadowMidCapture(frameIndex);
                }
                break;
            case WorldStreamingSmokeStage.AwaitShadowMidCapture:
                if (VisualCaptureCompleted("shadow-mid"))
                {
                    BeginShadowFarCapture(frameIndex);
                }
                break;
            case WorldStreamingSmokeStage.AwaitShadowFarCapture:
                if (VisualCaptureCompleted("shadow-far"))
                {
                    PinStationaryAnimationClock();
                    BeginShadowFarStableCapture(frameIndex);
                }
                break;
            case WorldStreamingSmokeStage.AwaitShadowFarStableCapture:
                if (VisualCaptureCompleted("shadow-far-stable"))
                {
                    Time.Unpin();
                    BeginAfterCapture(frameIndex);
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
        Time.Unpin();
        m_FailureMessage ??= message;
        m_Context.VisualSummaryService?.Seal();
        m_ReadyForShutdown = true;
        m_Stage = WorldStreamingSmokeStage.ReadyForShutdown;
    }

    public void AfterShutdown()
    {
        Time.Unpin();
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
        if (failure.EstimatedCpuBytes <= largestNormalEstimate)
        {
            throw new InvalidOperationException(
                "World-streaming smoke requires one cell whose CPU estimate is larger than every other cell's.");
        }

        // The injected admission failure is only observable from a pose whose plan selects the
        // oversized cell: a source that never reaches it would leave the scenario waiting for a
        // state its own streaming source cannot produce. Keep the canonical lowest-X ordering among
        // the cells that can reach it, so a single lane keeps its published arrangement and a dense
        // planar grid picks a pose beside the oversized cell instead of the far edge of the world.
        WorldCellDescriptor[] normals = ordered[..^1];
        int radius = world.Partition.LoadRadius;
        foreach (WorldCellDescriptor primary in normals
            .Where(cell => ChebyshevDistance(cell.Key.Coordinate, failure.Key.Coordinate) <= radius)
            .OrderBy(cell => Math.Abs(cell.Key.Coordinate.X))
            .ThenBy(cell => cell.Id))
        {
            HashSet<WorldCellId> plan = ExpectedDesiredCells(world, primary.Key.Coordinate);
            if (!plan.Contains(primary.Id) || !plan.Contains(failure.Id))
            {
                continue;
            }

            foreach (WorldCellDescriptor cancellation in normals
                .Where(cell => cell.Id != primary.Id && plan.Contains(cell.Id))
                .OrderBy(cell => Math.Abs(cell.Key.Coordinate.X))
                .ThenBy(cell => cell.Id))
            {
                if (!TryFindCancellationSource(
                        primary.Key.Coordinate,
                        cancellation.Key.Coordinate,
                        out WorldPosition cancellationSource))
                {
                    continue;
                }

                m_PrimaryCell = primary;
                m_CancellationCell = cancellation;
                m_FailureCell = failure;
                m_AdmissionSource = GetCellCenter(world.Partition, primary.Key.Coordinate);
                m_CancellationSource = cancellationSource;
                int farX = world.Cells.Max(cell => cell.Key.Coordinate.X) +
                    world.Partition.LoadRadius +
                    world.Partition.UnloadHysteresis +
                    3;
                m_FarSource = GetCellCenter(
                    world.Partition,
                    new WorldCellCoordinate(
                        farX,
                        primary.Key.Coordinate.Y,
                        primary.Key.Coordinate.Z));
                return;
            }
        }

        throw new InvalidOperationException(
            "World-streaming smoke could not find a primary cell whose load radius selects both the " +
            "oversized cell and a cancellable neighbour.");
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

    private void CaptureValidationCamera()
    {
        var cameraPool = m_EntityManager!.GetPool<CameraComponent>();
        var transformPool = m_EntityManager.GetPool<TransformComponent>();
        ReadOnlySpan<Entity> cameraEntities = cameraPool.GetRawEntityArray();
        for (int index = 0; index < cameraPool.Count; index++)
        {
            Entity entity = cameraEntities[index];
            if (!m_EntityManager.IsAlive(entity) || !transformPool.Has(entity))
            {
                continue;
            }

            if (m_HasValidationCamera)
            {
                throw new InvalidOperationException(
                    "World-streaming visual validation requires exactly one persistent camera.");
            }

            ref TransformComponent transform = ref transformPool.GetRef(entity);
            if (!IsFinite(transform.Position) || !IsFinite(transform.Rotation))
            {
                throw new InvalidOperationException(
                    "World-streaming visual validation camera transform is not finite.");
            }

            m_ValidationCamera = entity;
            m_ValidationCameraWorldPosition = m_Origin.ToWorld(transform.Position);
            m_ValidationCameraRotation = transform.Rotation;
            // The authored viewer pose is the streaming source of every observation that has to see
            // the world the way a player does: the soak cycles and the shadow captures stream the
            // cells under the authored camera, so the terrain the plan selected and the persistent
            // scene's static meshes stand in front of one camera instead of in different cells. The
            // admission and cancellation probes drive the source away from this pose on purpose,
            // because a settled player pose cannot produce an admission failure or a cancellation;
            // the scenario returns here - and waits for this pose's own plan to settle - before it
            // captures the 'during' checkpoint, so every rendered frame belongs to the pose that
            // produced it.
            m_ViewSource = m_ValidationCameraWorldPosition;
            m_HasValidationCamera = true;
        }

        if (!m_HasValidationCamera)
        {
            throw new InvalidOperationException(
                "World-streaming visual validation requires one persistent camera.");
        }
    }

    private void SetValidationCameraRetreat(float retreat)
    {
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, m_ValidationCameraRotation);
        float forwardLengthSquared = forward.LengthSquared();
        if (!float.IsFinite(retreat) || retreat < 0.0f ||
            !float.IsFinite(forwardLengthSquared) || forwardLengthSquared <= 0.000001f)
        {
            throw new InvalidOperationException(
                "World-streaming visual validation camera direction is invalid.");
        }

        forward /= MathF.Sqrt(forwardLengthSquared);
        ApplyValidationCameraPose(new WorldPosition(
            m_ValidationCameraWorldPosition.X - forward.X * retreat,
            m_ValidationCameraWorldPosition.Y - forward.Y * retreat,
            m_ValidationCameraWorldPosition.Z - forward.Z * retreat));
    }

    private void ApplyValidationCameraPose(WorldPosition worldPosition)
    {
        if (!m_HasValidationCamera ||
            m_EntityManager == null ||
            !m_EntityManager.IsAlive(m_ValidationCamera) ||
            !m_EntityManager.HasComponent<TransformComponent>(m_ValidationCamera))
        {
            throw new InvalidOperationException(
                "World-streaming visual validation camera is no longer available.");
        }

        if (!m_Origin.TryToOriginRelative(worldPosition, out Vector3 originRelativePosition))
        {
            throw new InvalidOperationException(
                "World-streaming visual validation camera could not be represented relative to the current origin.");
        }

        ref TransformComponent transform = ref m_EntityManager.GetComponent<TransformComponent>(
            m_ValidationCamera);
        transform.Position = originRelativePosition;
        transform.Rotation = m_ValidationCameraRotation;
        m_PersistentWorldPositions[m_ValidationCamera] = worldPosition;
    }

    private void BeginInitialRequest()
    {
        // The first request is the admission probe. Its pose is the one whose plan selects both the
        // oversized cell that has to fail admission and the queued neighbour that is cancelled
        // next; the camera stays on the authored startup pose that the 'before' capture observed.
        m_Streaming.SetStreamingSource(m_AdmissionSource);
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
        // The checkpoint expectation is derived from the world descriptor rather than authored
        // per fixture: the source sits on the cancellation pose, so the cells that must still be
        // active are exactly the ones the streaming policy keeps selected there. A dense planar
        // cell grid keeps more than the primary cell, and a hand-written list would report that
        // correct arrangement as an unexpected active-cell set.
        WorldCellId[] expected = ExpectedActiveCells(m_CancellationSource);
        if (!m_ObservedStates.Contains(WorldCellStreamingState.Cancelled) ||
            !ActiveCellIds().SequenceEqual(expected))
        {
            return;
        }

        CaptureCheckpoint("cancelled", frameIndex, expected);
        // The probe pose is a corner of the region rather than where the camera stands, so a capture
        // taken here would frame cells the authored viewer never sees. The source returns to the
        // authored pose instead, and the 'during' checkpoint and its capture below wait for that
        // pose's plan - the cells the streaming policy selects there, including the probe cells
        // still inside the unload hysteresis - to settle first.
        m_Streaming.SetStreamingSource(m_ViewSource);
        m_Stage = WorldStreamingSmokeStage.AwaitDuringSettle;
    }

    private void ObserveDuringSettle(uint frameIndex)
    {
        // Derived from the world descriptor and the live policy, so the identity of the retained
        // probe cells follows the hysteresis rather than a fixture-authored list.
        WorldCellId[] expected = ExpectedActiveCells(m_ViewSource);
        if (!ActiveCellIds().SequenceEqual(expected)) return;

        CaptureCheckpoint("during", frameIndex, expected);
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
        // The soak cycles and the shadow captures that follow them stream around the authored
        // viewer pose, so every capture observes the terrain the plan selected under the camera
        // that renders it, exactly as a runtime streams around its player.
        m_Streaming.SetStreamingSource(m_ViewSource);
        m_Stage = WorldStreamingSmokeStage.AwaitSoakLoad;
    }

    private void ObserveSoakLoad(uint frameIndex)
    {
        WorldCellId[] expected = ExpectedActiveCells(m_ViewSource);
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

        BeginShadowNearCapture(frameIndex);
    }

    private void BeginFinalDrain()
    {
        m_Streaming.SetStreamingSource(m_FarSource);
        m_Stage = WorldStreamingSmokeStage.AwaitFinalDrain;
    }

    private void BeginShadowNearCapture(uint frameIndex)
    {
        if (m_Context.VisualSummaryService == null)
        {
            BeginFinalDrain();
            return;
        }

        SetValidationCameraRetreat(0.0f);
        if (ScheduleVisualCapture("shadow-near", checked(frameIndex + 1)))
        {
            m_Stage = WorldStreamingSmokeStage.AwaitShadowNearCapture;
        }
    }

    private void BeginShadowMidCapture(uint frameIndex)
    {
        SetValidationCameraRetreat(MidShadowCameraRetreat);
        if (ScheduleVisualCapture("shadow-mid", checked(frameIndex + 1)))
        {
            m_Stage = WorldStreamingSmokeStage.AwaitShadowMidCapture;
        }
    }

    private void BeginShadowFarCapture(uint frameIndex)
    {
        SetValidationCameraRetreat(FarShadowCameraRetreat);
        if (ScheduleVisualCapture("shadow-far", checked(frameIndex + 1)))
        {
            m_Stage = WorldStreamingSmokeStage.AwaitShadowFarCapture;
        }
    }

    private void BeginShadowFarStableCapture(uint frameIndex)
    {
        if (ScheduleVisualCapture("shadow-far-stable", checked(frameIndex + 1)))
        {
            m_Stage = WorldStreamingSmokeStage.AwaitShadowFarStableCapture;
        }
    }

    /// <summary>
    /// The stationary-stability pair renders the same camera on two consecutive frames and
    /// requires byte-identical color and depth output. Time-driven content such as vegetation
    /// wind legitimately advances between frames, so the validation clock is pinned to the time
    /// the reference frame rendered with. Any remaining difference is then real
    /// non-determinism rather than expected animation progress.
    /// </summary>
    private static void PinStationaryAnimationClock()
    {
        Time.Pin(Time.elapsedTime);
    }

    private void BeginAfterCapture(uint frameIndex)
    {
        SetValidationCameraRetreat(0.0f);
        if (ScheduleVisualCapture("after", checked(frameIndex + 1)))
        {
            m_Stage = WorldStreamingSmokeStage.AwaitAfterCapture;
        }
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

    /// <summary>
    /// Streaming source that keeps the retained cell selected while pushing the cancelled cell
    /// out of the load radius, so the scenario can observe a cancellation and still capture a
    /// checkpoint on a settled active set.
    /// </summary>
    /// <remarks>
    /// The search walks the whole horizontal load neighbourhood instead of one axis. A canonical
    /// single-lane fixture only ever needed the second cell along X, but a planar open-world grid
    /// has neighbours on both axes, and an axis-only search cannot cancel a cell that sits beside
    /// the retained one. Candidates are ranked by how few cells they leave active and then by
    /// distance, so the cancellation pose stays as close to the retained cell as the contract
    /// allows and the resulting expectation stays deterministic.
    /// </remarks>
    private bool TryFindCancellationSource(
        WorldCellCoordinate retained,
        WorldCellCoordinate cancelled,
        out WorldPosition source)
    {
        WorldPartitionSettings partition = m_World!.Partition;
        int radius = partition.LoadRadius;
        WorldCellCoordinate? bestCandidate = null;
        int bestActiveCount = int.MaxValue;
        int bestDistance = int.MaxValue;
        for (int x = retained.X - radius; x <= retained.X + radius; x++)
        {
            for (int z = retained.Z - radius; z <= retained.Z + radius; z++)
            {
                var candidate = new WorldCellCoordinate(x, retained.Y, z);
                if (ChebyshevDistance(candidate, retained) > radius ||
                    ChebyshevDistance(candidate, cancelled) <= radius)
                {
                    continue;
                }

                int activeCount = ExpectedActiveCells(GetCellCenter(partition, candidate)).Length;
                int distance = Math.Abs(x - retained.X) + Math.Abs(z - retained.Z);
                if (activeCount < bestActiveCount ||
                    (activeCount == bestActiveCount && distance < bestDistance))
                {
                    bestCandidate = candidate;
                    bestActiveCount = activeCount;
                    bestDistance = distance;
                }
            }
        }

        if (bestCandidate == null)
        {
            source = default;
            return false;
        }

        source = GetCellCenter(partition, bestCandidate.Value);
        return true;
    }

    /// <summary>
    /// Active set the streaming policy must reach for a source position, derived from the world
    /// descriptor and the configured budgets.
    /// </summary>
    /// <remarks>
    /// The expectation mirrors <c>RuntimeWorldStreamingService.PlanDesiredCells</c> and the read
    /// admission that follows it: camera candidates are the declared cells inside
    /// <c>LoadRadius</c>, cells that are already active stay selected inside
    /// <c>LoadRadius + UnloadHysteresis</c>, the dependency closure of a candidate has to fit
    /// <c>MaxActiveCells</c>, and a cell whose staging reservation exceeds the decoded staging
    /// budget fails instead of activating. Deriving the set means the fixture follows any authored
    /// cell arrangement - including the dense planar grids a streaming open world uses - instead of
    /// pinning the one arrangement of the canonical world.
    /// </remarks>
    private WorldCellId[] ExpectedActiveCells(WorldPosition source)
    {
        WorldDescriptor world = m_World!;
        WorldCellCoordinate sourceCoordinate =
            WorldPartitionCoordinates.GetCoordinate(world.Partition, source);
        return ExpectedDesiredCells(world, sourceCoordinate)
            .Where(id => !FailsAdmission(world, id))
            .Order()
            .ToArray();
    }

    private HashSet<WorldCellId> ExpectedDesiredCells(
        WorldDescriptor world,
        WorldCellCoordinate sourceCoordinate)
    {
        int radius = world.Partition.LoadRadius;
        int hysteresis = world.Partition.UnloadHysteresis;
        var selected = new HashSet<WorldCellId>();
        foreach (WorldCellStreamingSnapshot cell in m_Streaming.GetCells())
        {
            if (cell.State is not (WorldCellStreamingState.Active or
                WorldCellStreamingState.QueuedToUnload or
                WorldCellStreamingState.Unloading))
            {
                continue;
            }

            WorldCellDescriptor? descriptor = world.Cells
                .FirstOrDefault(candidate => candidate.Id == cell.CellId);
            if (descriptor == null) continue;
            if (ChebyshevDistance(sourceCoordinate, descriptor.Key.Coordinate) <=
                radius + hysteresis)
            {
                selected.Add(cell.CellId);
            }
        }

        WorldCellDescriptor[] candidates = world.Cells
            .Where(cell => ChebyshevDistance(sourceCoordinate, cell.Key.Coordinate) <= radius)
            .OrderBy(cell => ChebyshevDistance(sourceCoordinate, cell.Key.Coordinate))
            .ThenBy(cell => LayerPriority(world, cell.Key.Layer))
            .ThenBy(cell => cell.Id)
            .ToArray();
        foreach (WorldCellDescriptor candidate in candidates)
        {
            HashSet<WorldCellId> closure = DependencyClosure(world, candidate);
            int additional = closure.Count(id => !selected.Contains(id));
            if (selected.Count + additional > world.Partition.MaxActiveCells)
            {
                continue;
            }

            selected.UnionWith(closure);
        }

        return selected;
    }

    private static HashSet<WorldCellId> DependencyClosure(
        WorldDescriptor world,
        WorldCellDescriptor root)
    {
        var closure = new HashSet<WorldCellId>();
        var pending = new Stack<WorldCellId>();
        pending.Push(root.Id);
        while (pending.Count > 0)
        {
            WorldCellId id = pending.Pop();
            if (!closure.Add(id)) continue;
            WorldCellDescriptor descriptor = world.Cells.Single(cell => cell.Id == id);
            for (int index = 0; index < descriptor.Dependencies.Count; index++)
            {
                pending.Push(descriptor.Dependencies[index]);
            }
        }

        return closure;
    }

    private static int LayerPriority(WorldDescriptor world, string layer)
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

    private bool FailsAdmission(WorldDescriptor world, WorldCellId id)
    {
        WorldCellDescriptor descriptor = world.Cells.Single(cell => cell.Id == id);
        long inFlightBytes = descriptor.ScenePayloadBytes > 0
            ? descriptor.ScenePayloadBytes
            : Math.Max(1, Math.Min(descriptor.EstimatedCpuBytes, 16L * 1024 * 1024));
        long stagingBytes = Math.Max(inFlightBytes, descriptor.EstimatedCpuBytes);
        return inFlightBytes > m_Streaming.Budgets.MaxBytesInFlight ||
            stagingBytes > m_Streaming.Budgets.MaxDecodedStagingBytes;
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

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static bool IsFinite(Quaternion value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) &&
        float.IsFinite(value.W);

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
    AwaitStartupWorld,
    AwaitBeforeCapture,
    AwaitInitialPlan,
    AwaitCancellationAndPrimary,
    AwaitDuringSettle,
    AwaitDuringCapture,
    AwaitFirstUnload,
    AwaitSoakLoad,
    AwaitSoakUnload,
    AwaitShadowNearCapture,
    AwaitShadowMidCapture,
    AwaitShadowFarCapture,
    AwaitShadowFarStableCapture,
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
