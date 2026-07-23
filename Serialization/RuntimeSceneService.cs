using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.ECS;

namespace ArisenEngine.Resources.Serialization;

public readonly record struct RuntimeSceneInstanceId(long Value)
{
    public static RuntimeSceneInstanceId Invalid => default;

    public bool IsValid => Value > 0;

    public override string ToString() => IsValid ? Value.ToString() : "Invalid";
}

public enum RuntimeSceneInstanceKind
{
    Persistent,
    Additive
}

public enum RuntimeSceneInstanceState
{
    QueuedForActivation,
    Active,
    QueuedForUnload,
    Unloaded,
    Failed
}

public readonly record struct RuntimeSceneComponentCounts(
    int CameraCount,
    int MeshRendererCount,
    int DirectionalLightCount,
    int PointLightCount,
    int SpotLightCount,
    int EnvironmentCount)
{
    public static RuntimeSceneComponentCounts From(in SceneLoadResult result) => new(
        result.CameraCount,
        result.MeshRendererCount,
        result.DirectionalLightCount,
        result.PointLightCount,
        result.SpotLightCount,
        result.EnvironmentCount);
}

public sealed record RuntimeSceneInstanceSnapshot(
    RuntimeSceneInstanceId InstanceId,
    AssetRef<SceneSourceAsset> Scene,
    RuntimeSceneInstanceKind Kind,
    RuntimeSceneInstanceState State,
    string Name,
    string SourcePath,
    long SourceRevision,
    int EntityCount,
    IReadOnlyList<CookedSceneDependency> Dependencies,
    string Diagnostic,
    RuntimeSceneComponentCounts ComponentCounts = default);

public sealed record RuntimeSceneDiagnostic(
    long Sequence,
    RuntimeSceneInstanceId InstanceId,
    AssetRef<SceneSourceAsset> Scene,
    RuntimeSceneInstanceState State,
    string Message);

public sealed record RuntimeSceneState(
    AssetRef<SceneSourceAsset> Scene,
    string Name,
    string SourcePath,
    EntityManager EntityManager,
    SceneAuthoringEntityMap AuthoringEntities,
    long SourceRevision = 0,
    RuntimeSceneInstanceId InstanceId = default);

public sealed record RuntimeSceneLoadReport(
    AssetRef<SceneSourceAsset> Scene,
    long SourceRevision,
    SceneLoadResult Result,
    RuntimeSceneInstanceId InstanceId = default,
    RuntimeSceneInstanceKind Kind = RuntimeSceneInstanceKind.Persistent);

public interface IRuntimeSceneService
{
    RuntimeSceneState? ActiveScene { get; }

    event Action<RuntimeSceneState>? ActiveSceneChanged;

    event Action<RuntimeSceneLoadReport>? SceneLoadCompleted;

    event Action<RuntimeSceneInstanceSnapshot>? SceneInstanceStateChanged;

    SceneLoadResult LoadScene(AssetRef<SceneSourceAsset> scene);

    void RequestSceneLoad(AssetRef<SceneSourceAsset> scene);

    void RequestSceneLoad(SceneSourceSnapshot snapshot);

    RuntimeSceneInstanceId RequestAdditiveSceneLoad(AssetRef<SceneSourceAsset> scene);

    bool RequestSceneUnload(RuntimeSceneInstanceId instanceId);

    bool TryGetSceneInstance(
        RuntimeSceneInstanceId instanceId,
        out RuntimeSceneInstanceSnapshot snapshot);

    IReadOnlyList<RuntimeSceneInstanceSnapshot> GetSceneInstances();

    IReadOnlyList<RuntimeSceneDiagnostic> GetDiagnostics();

    bool TryResolveEntity(
        RuntimeSceneInstanceId instanceId,
        Guid authoringGuid,
        out Entity entity);

    bool TryGetEntityOwner(Entity entity, out RuntimeSceneInstanceId instanceId);
}

public sealed class RuntimeSceneService : IRuntimeSceneService
{
    private const int MaxTerminalSnapshots = 64;
    private const int MaxDiagnostics = 128;

    private enum PendingSceneOperationKind
    {
        Replace,
        Additive,
        Unload
    }

    private enum SceneReplacementMode
    {
        None,
        PersistentOnly,
        All
    }

    private sealed record PendingSceneOperation(
        long Sequence,
        PendingSceneOperationKind OperationKind,
        RuntimeSceneInstanceId InstanceId,
        AssetRef<SceneSourceAsset> Scene,
        SceneSourceSnapshot? Snapshot,
        SceneReplacementMode ReplacementMode = SceneReplacementMode.None,
        SceneStagingData? PreparedStaging = null,
        string PreparedSourceKind = "");

    private sealed class RuntimeSceneInstance
    {
        public required RuntimeSceneInstanceId InstanceId { get; init; }
        public required AssetRef<SceneSourceAsset> Scene { get; init; }
        public required RuntimeSceneInstanceKind Kind { get; init; }
        public RuntimeSceneInstanceState State { get; set; }
        public string Name { get; set; } = string.Empty;
        public string SourcePath { get; set; } = string.Empty;
        public long SourceRevision { get; init; }
        public Entity[] OwnedEntities { get; set; } = Array.Empty<Entity>();
        public SceneAuthoringEntityMap? AuthoringEntities { get; set; }
        public CookedSceneDependency[] Dependencies { get; set; } = Array.Empty<CookedSceneDependency>();
        public RuntimeSceneComponentCounts ComponentCounts { get; set; }
        public string Diagnostic { get; set; } = string.Empty;

        public RuntimeSceneInstanceSnapshot Snapshot()
        {
            return new RuntimeSceneInstanceSnapshot(
                InstanceId,
                Scene,
                Kind,
                State,
                Name,
                SourcePath,
                SourceRevision,
                OwnedEntities.Length,
                Array.AsReadOnly(Dependencies),
                Diagnostic,
                ComponentCounts);
        }
    }

    private readonly object m_Gate = new();
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly Func<EntityManager> m_EntityManagerProvider;
    private readonly List<PendingSceneOperation> m_PendingOperations = new();
    private readonly Dictionary<long, RuntimeSceneInstance> m_Instances = new();
    private readonly Dictionary<Entity, RuntimeSceneInstanceId> m_EntityOwners = new();
    private readonly Queue<RuntimeSceneInstanceSnapshot> m_TerminalSnapshots = new();
    private readonly Queue<RuntimeSceneDiagnostic> m_Diagnostics = new();
    private EntityManager? m_EntityManager;
    private RuntimeSceneState? m_ActiveScene;
    private long m_NextInstanceId;
    private long m_NextOperationSequence;
    private long m_NextDiagnosticSequence;

    public RuntimeSceneState? ActiveScene => Volatile.Read(ref m_ActiveScene);

    public event Action<RuntimeSceneState>? ActiveSceneChanged;

    public event Action<RuntimeSceneLoadReport>? SceneLoadCompleted;

    public event Action<RuntimeSceneInstanceSnapshot>? SceneInstanceStateChanged;

    public RuntimeSceneService(
        IAssetDatabase assetDatabase,
        EntityManager entityManager)
        : this(assetDatabase, () => entityManager)
    {
        ArgumentNullException.ThrowIfNull(entityManager);
    }

    public RuntimeSceneService(
        IAssetDatabase assetDatabase,
        Func<EntityManager> entityManagerProvider)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_EntityManagerProvider = entityManagerProvider
            ?? throw new ArgumentNullException(nameof(entityManagerProvider));
    }

    public SceneLoadResult LoadScene(AssetRef<SceneSourceAsset> scene)
    {
        RuntimeSceneInstance instance;
        lock (m_Gate)
        {
            instance = CreateQueuedInstanceLocked(
                scene,
                RuntimeSceneInstanceKind.Persistent,
                sourceRevision: 0);
        }

        return ActivateInstance(
            instance,
            snapshot: null,
            preparedStaging: null,
            preparedSourceKind: string.Empty,
            SceneReplacementMode.All);
    }

    public void RequestSceneLoad(AssetRef<SceneSourceAsset> scene)
    {
        if (!scene.IsValid)
        {
            throw new ArgumentException("Queued scene load requires a valid scene asset reference.", nameof(scene));
        }

        QueueReplace(scene, snapshot: null);
    }

    public void RequestSceneLoad(SceneSourceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsValid)
        {
            throw new ArgumentException(
                "Queued scene source snapshot must contain a valid scene, source path, text, and revision.",
                nameof(snapshot));
        }

        if (!m_AssetDatabase.CanReadSourceAssets)
        {
            throw new InvalidOperationException(
                "[RuntimeSceneService] Source snapshots require Editor authoring access or the " +
                "explicit diagnostic source-asset option.");
        }

        QueueReplace(snapshot.Scene, snapshot);
    }

    public RuntimeSceneInstanceId RequestAdditiveSceneLoad(AssetRef<SceneSourceAsset> scene)
    {
        if (!scene.IsValid)
        {
            throw new ArgumentException(
                "Additive scene load requires a valid scene asset reference.",
                nameof(scene));
        }

        lock (m_Gate)
        {
            RuntimeSceneInstance instance = CreateQueuedInstanceLocked(
                scene,
                RuntimeSceneInstanceKind.Additive,
                sourceRevision: 0);
            m_PendingOperations.Add(new PendingSceneOperation(
                ++m_NextOperationSequence,
                PendingSceneOperationKind.Additive,
                instance.InstanceId,
                scene,
                Snapshot: null));
            return instance.InstanceId;
        }
    }

    public bool RequestSceneUnload(RuntimeSceneInstanceId instanceId)
    {
        if (!instanceId.IsValid)
        {
            return false;
        }

        lock (m_Gate)
        {
            if (!m_Instances.TryGetValue(instanceId.Value, out RuntimeSceneInstance? instance)
                || instance.State != RuntimeSceneInstanceState.Active)
            {
                return false;
            }

            instance.State = RuntimeSceneInstanceState.QueuedForUnload;
            m_PendingOperations.Add(new PendingSceneOperation(
                ++m_NextOperationSequence,
                PendingSceneOperationKind.Unload,
                instanceId,
                instance.Scene,
                Snapshot: null));
            return true;
        }
    }

    public bool TryGetSceneInstance(
        RuntimeSceneInstanceId instanceId,
        out RuntimeSceneInstanceSnapshot snapshot)
    {
        lock (m_Gate)
        {
            if (m_Instances.TryGetValue(instanceId.Value, out RuntimeSceneInstance? instance))
            {
                snapshot = instance.Snapshot();
                return true;
            }

            foreach (RuntimeSceneInstanceSnapshot terminal in m_TerminalSnapshots)
            {
                if (terminal.InstanceId == instanceId)
                {
                    snapshot = terminal;
                    return true;
                }
            }
        }

        snapshot = null!;
        return false;
    }

    public IReadOnlyList<RuntimeSceneInstanceSnapshot> GetSceneInstances()
    {
        lock (m_Gate)
        {
            return m_Instances.Values
                .OrderBy(instance => instance.InstanceId.Value)
                .Select(instance => instance.Snapshot())
                .ToArray();
        }
    }

    public IReadOnlyList<RuntimeSceneDiagnostic> GetDiagnostics()
    {
        lock (m_Gate)
        {
            return m_Diagnostics.ToArray();
        }
    }

    public bool TryResolveEntity(
        RuntimeSceneInstanceId instanceId,
        Guid authoringGuid,
        out Entity entity)
    {
        SceneAuthoringEntityMap? map;
        lock (m_Gate)
        {
            map = m_Instances.TryGetValue(instanceId.Value, out RuntimeSceneInstance? instance)
                ? instance.AuthoringEntities
                : null;
        }

        if (map != null && map.TryGetEntity(authoringGuid, out entity))
        {
            return true;
        }

        entity = Entity.Null;
        return false;
    }

    public bool TryGetEntityOwner(Entity entity, out RuntimeSceneInstanceId instanceId)
    {
        lock (m_Gate)
        {
            return m_EntityOwners.TryGetValue(entity, out instanceId);
        }
    }

    internal (RuntimeSceneInstanceId InstanceId, SceneLoadResult Result)
        ActivatePreparedAdditiveAtFrameBoundary(
            AssetRef<SceneSourceAsset> scene,
            SceneStagingData staging,
            string sourceKind)
    {
        ArgumentNullException.ThrowIfNull(staging);
        if (!scene.IsValid || staging.SceneGuid != scene.Guid)
        {
            throw new ArgumentException(
                "Prepared additive scene identity must match its validated staging payload.",
                nameof(scene));
        }

        RuntimeSceneInstance instance;
        lock (m_Gate)
        {
            instance = CreateQueuedInstanceLocked(
                scene,
                RuntimeSceneInstanceKind.Additive,
                sourceRevision: 0);
        }

        SceneLoadResult result = ActivateInstance(
            instance,
            snapshot: null,
            staging,
            sourceKind,
            SceneReplacementMode.None);
        return (instance.InstanceId, result);
    }

    internal bool UnloadSceneAtFrameBoundary(
        RuntimeSceneInstanceId instanceId,
        out string diagnostic)
    {
        RuntimeSceneInstance? instance;
        lock (m_Gate)
        {
            if (!m_Instances.TryGetValue(instanceId.Value, out instance) ||
                instance.State != RuntimeSceneInstanceState.Active)
            {
                diagnostic =
                    $"[RuntimeSceneService] Scene instance '{instanceId}' is not active and cannot be unloaded.";
                return false;
            }

            instance.State = RuntimeSceneInstanceState.QueuedForUnload;
        }

        UnloadInstance(instance);
        if (TryGetSceneInstance(instanceId, out RuntimeSceneInstanceSnapshot snapshot) &&
            snapshot.State == RuntimeSceneInstanceState.Unloaded)
        {
            diagnostic = snapshot.Diagnostic;
            return true;
        }

        diagnostic = snapshot?.Diagnostic
            ?? $"[RuntimeSceneService] Scene instance '{instanceId}' unload did not complete.";
        return false;
    }

    internal SceneLoadResult? ProcessPendingSceneLoadAtFrameBoundary()
    {
        PendingSceneOperation[] operations;
        lock (m_Gate)
        {
            if (m_PendingOperations.Count == 0)
            {
                return null;
            }

            operations = m_PendingOperations
                .OrderBy(operation => operation.Sequence)
                .ToArray();
            m_PendingOperations.Clear();
        }

        SceneLoadResult? lastLoadResult = null;
        for (int i = 0; i < operations.Length; i++)
        {
            PendingSceneOperation operation = operations[i];
            RuntimeSceneInstance? instance;
            lock (m_Gate)
            {
                m_Instances.TryGetValue(operation.InstanceId.Value, out instance);
            }

            if (instance == null)
            {
                continue;
            }

            switch (operation.OperationKind)
            {
                case PendingSceneOperationKind.Replace:
                    lastLoadResult = ActivateInstance(
                        instance,
                        operation.Snapshot,
                        operation.PreparedStaging,
                        operation.PreparedSourceKind,
                        operation.ReplacementMode);
                    break;
                case PendingSceneOperationKind.Additive:
                    lastLoadResult = ActivateInstance(
                        instance,
                        snapshot: null,
                        operation.PreparedStaging,
                        operation.PreparedSourceKind,
                        SceneReplacementMode.None);
                    break;
                case PendingSceneOperationKind.Unload:
                    UnloadInstance(instance);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        return lastLoadResult;
    }

    internal void ClearForShutdown()
    {
        lock (m_Gate)
        {
            m_PendingOperations.Clear();
            m_Instances.Clear();
            m_EntityOwners.Clear();
            m_TerminalSnapshots.Clear();
            m_Diagnostics.Clear();
            m_EntityManager = null;
            Volatile.Write(ref m_ActiveScene, null);
        }
    }

    private void QueueReplace(
        AssetRef<SceneSourceAsset> scene,
        SceneSourceSnapshot? snapshot)
    {
        lock (m_Gate)
        {
            SceneReplacementMode replacementMode =
                m_ActiveScene is { } activeScene && IsSameScene(activeScene.Scene, scene)
                    ? SceneReplacementMode.PersistentOnly
                    : SceneReplacementMode.All;

            for (int i = m_PendingOperations.Count - 1; i >= 0; i--)
            {
                PendingSceneOperation pending = m_PendingOperations[i];
                if (pending.OperationKind != PendingSceneOperationKind.Replace)
                {
                    continue;
                }

                m_PendingOperations.RemoveAt(i);
                if (m_Instances.Remove(pending.InstanceId.Value, out RuntimeSceneInstance? superseded))
                {
                    superseded.State = RuntimeSceneInstanceState.Failed;
                    superseded.Diagnostic =
                        "[RuntimeSceneService] Scene replacement request was superseded before activation.";
                    AddTerminalSnapshotLocked(superseded.Snapshot());
                    AddDiagnosticLocked(superseded);
                }
            }

            RuntimeSceneInstance instance = CreateQueuedInstanceLocked(
                scene,
                RuntimeSceneInstanceKind.Persistent,
                snapshot?.Revision ?? 0);
            m_PendingOperations.Add(new PendingSceneOperation(
                ++m_NextOperationSequence,
                PendingSceneOperationKind.Replace,
                instance.InstanceId,
                scene,
                snapshot,
                replacementMode));
        }
    }

    private RuntimeSceneInstance CreateQueuedInstanceLocked(
        AssetRef<SceneSourceAsset> scene,
        RuntimeSceneInstanceKind kind,
        long sourceRevision)
    {
        var instance = new RuntimeSceneInstance
        {
            InstanceId = new RuntimeSceneInstanceId(++m_NextInstanceId),
            Scene = scene,
            Kind = kind,
            State = RuntimeSceneInstanceState.QueuedForActivation,
            SourceRevision = sourceRevision,
            Diagnostic = "[RuntimeSceneService] Scene instance is queued for frame-boundary activation."
        };
        m_Instances.Add(instance.InstanceId.Value, instance);
        return instance;
    }

    private SceneLoadResult ActivateInstance(
        RuntimeSceneInstance instance,
        SceneSourceSnapshot? snapshot,
        SceneStagingData? preparedStaging,
        string preparedSourceKind,
        SceneReplacementMode replacementMode)
    {
        using var _ = Profiler.Zone("RuntimeSceneService.LoadScene");
        Entity[] activatedEntities = Array.Empty<Entity>();
        try
        {
            if (snapshot != null && !m_AssetDatabase.CanReadSourceAssets)
            {
                throw new InvalidOperationException(
                    "[RuntimeSceneService] Source snapshots require Editor authoring access or the " +
                    "explicit diagnostic source-asset option.");
            }

            SceneStagingData staging;
            string diagnostic;
            bool staged = preparedStaging != null;
            if (preparedStaging != null)
            {
                staging = preparedStaging;
                diagnostic = string.Empty;
            }
            else
            {
                staged = snapshot != null
                    ? SceneAssetLoader.TryLoadSceneStaging(
                        m_AssetDatabase,
                        snapshot,
                        out staging,
                        out diagnostic)
                    : m_AssetDatabase.CanReadSourceAssets
                        ? SceneAssetLoader.TryLoadSceneStaging(
                            m_AssetDatabase,
                            instance.Scene,
                            out staging,
                            out diagnostic)
                        : SceneAssetCooker.TryLoadCookedStaging(
                            m_AssetDatabase,
                            instance.Scene,
                            out staging,
                            out diagnostic);
            }
            if (!staged)
            {
                return FailActivation(instance, diagnostic);
            }

            RuntimeSceneInstance[] replacedInstances = replacementMode != SceneReplacementMode.None
                ? GetActiveInstancesForReplacement(instance.InstanceId, replacementMode)
                : Array.Empty<RuntimeSceneInstance>();
            if (replacedInstances.Length > 0
                && !TryValidateUnloadReferences(replacedInstances, out string unloadDiagnostic))
            {
                return FailActivation(instance, unloadDiagnostic);
            }

            EntityManager entityManager = ResolveEntityManager();
            SceneLoadResult result = SceneAssetLoader.InstantiateStagedScene(
                staging,
                entityManager,
                preparedStaging != null && !string.IsNullOrWhiteSpace(preparedSourceKind)
                    ? preparedSourceKind
                    : m_AssetDatabase.CanReadSourceAssets ? "source" : "cooked");
            SceneAuthoringEntityMap authoringEntities = result.AuthoringEntities
                ?? throw new InvalidOperationException(
                    "[RuntimeSceneService] A successful scene load returned no authoring-to-runtime identity map.");
            activatedEntities = authoringEntities.RuntimeEntities;

            RuntimeSceneInstanceSnapshot[] unloadedSnapshots = replacedInstances.Length == 0
                ? Array.Empty<RuntimeSceneInstanceSnapshot>()
                : DestroyInstances(
                    entityManager,
                    replacedInstances,
                    "[RuntimeSceneService] Scene instance was unloaded by controlled scene replacement.");

            instance.Name = result.SceneName;
            instance.SourcePath = result.SourcePath;
            instance.OwnedEntities = activatedEntities;
            instance.AuthoringEntities = authoringEntities;
            instance.Dependencies = SceneAssetCooker.GetDependencies(staging);
            instance.ComponentCounts = RuntimeSceneComponentCounts.From(result);
            instance.Diagnostic = result.Diagnostic;
            instance.State = RuntimeSceneInstanceState.Active;

            RuntimeSceneState? activeState = null;
            lock (m_Gate)
            {
                for (int i = 0; i < activatedEntities.Length; i++)
                {
                    if (m_EntityOwners.ContainsKey(activatedEntities[i]))
                    {
                        throw new InvalidOperationException(
                            $"[RuntimeSceneService] Entity {activatedEntities[i]} already has a scene owner.");
                    }
                }

                for (int i = 0; i < activatedEntities.Length; i++)
                {
                    m_EntityOwners.Add(activatedEntities[i], instance.InstanceId);
                }

                AddDiagnosticLocked(instance);
                if (instance.Kind == RuntimeSceneInstanceKind.Persistent)
                {
                    activeState = new RuntimeSceneState(
                        instance.Scene,
                        instance.Name,
                        instance.SourcePath,
                        entityManager,
                        authoringEntities,
                        instance.SourceRevision,
                        instance.InstanceId);
                    Volatile.Write(ref m_ActiveScene, activeState);
                }
            }

            for (int i = 0; i < unloadedSnapshots.Length; i++)
            {
                PublishSceneInstanceStateChanged(unloadedSnapshots[i]);
            }

            RuntimeSceneInstanceSnapshot activatedSnapshot = instance.Snapshot();
            PublishSceneInstanceStateChanged(activatedSnapshot);
            if (activeState != null)
            {
                PublishActiveSceneChanged(activeState);
            }

            PublishLoadCompleted(new RuntimeSceneLoadReport(
                instance.Scene,
                instance.SourceRevision,
                result,
                instance.InstanceId,
                instance.Kind));
            PlotInstanceCounts();
            return result;
        }
        catch (Exception ex)
        {
            EntityManager? entityManager = m_EntityManager;
            if (entityManager != null && activatedEntities.Length > 0)
            {
                for (int i = activatedEntities.Length - 1; i >= 0; i--)
                {
                    entityManager.TryDestroyEntity(activatedEntities[i]);
                }
            }

            return FailActivation(
                instance,
                $"[RuntimeSceneService] Scene activation failed: {ex.Message}");
        }
    }

    private void UnloadInstance(RuntimeSceneInstance instance)
    {
        if (!TryValidateUnloadReferences(new[] { instance }, out string diagnostic))
        {
            RuntimeSceneInstanceSnapshot snapshot;
            lock (m_Gate)
            {
                instance.State = RuntimeSceneInstanceState.Active;
                instance.Diagnostic = diagnostic;
                AddDiagnosticLocked(instance);
                snapshot = instance.Snapshot();
            }
            PublishSceneInstanceStateChanged(snapshot);
            return;
        }

        RuntimeSceneInstanceSnapshot[] snapshots = DestroyInstances(
            ResolveEntityManager(),
            new[] { instance },
            $"[RuntimeSceneService] Unloaded scene instance {instance.InstanceId} with {instance.OwnedEntities.Length} owned entities.");
        PublishSceneInstanceStateChanged(snapshots[0]);
        PlotInstanceCounts();
    }

    private RuntimeSceneInstance[] GetActiveInstancesForReplacement(
        RuntimeSceneInstanceId replacementId,
        SceneReplacementMode replacementMode)
    {
        lock (m_Gate)
        {
            return m_Instances.Values
                .Where(instance =>
                    instance.InstanceId != replacementId
                    && (replacementMode == SceneReplacementMode.All
                        || instance.Kind == RuntimeSceneInstanceKind.Persistent)
                    && (instance.State == RuntimeSceneInstanceState.Active
                        || instance.State == RuntimeSceneInstanceState.QueuedForUnload))
                .OrderBy(instance => instance.InstanceId.Value)
                .ToArray();
        }
    }

    private static bool IsSameScene(
        AssetRef<SceneSourceAsset> left,
        AssetRef<SceneSourceAsset> right)
    {
        return left.Guid == right.Guid &&
               string.Equals(left.PackageId, right.PackageId, StringComparison.OrdinalIgnoreCase);
    }

    private RuntimeSceneInstanceSnapshot[] DestroyInstances(
        EntityManager entityManager,
        RuntimeSceneInstance[] instances,
        string diagnostic)
    {
        int entityCount = 0;
        for (int i = 0; i < instances.Length; i++)
        {
            entityCount = checked(entityCount + instances[i].OwnedEntities.Length);
        }

        var entities = new Entity[entityCount];
        int destination = 0;
        for (int i = 0; i < instances.Length; i++)
        {
            instances[i].OwnedEntities.CopyTo(entities, destination);
            destination += instances[i].OwnedEntities.Length;
        }
        entityManager.DestroyEntities(entities);

        var snapshots = new RuntimeSceneInstanceSnapshot[instances.Length];
        lock (m_Gate)
        {
            for (int i = 0; i < entities.Length; i++)
            {
                m_EntityOwners.Remove(entities[i]);
            }

            RuntimeSceneState? activeScene = m_ActiveScene;
            for (int i = 0; i < instances.Length; i++)
            {
                RuntimeSceneInstance instance = instances[i];
                instance.State = RuntimeSceneInstanceState.Unloaded;
                instance.Diagnostic = diagnostic;
                snapshots[i] = instance.Snapshot();
                m_Instances.Remove(instance.InstanceId.Value);
                AddTerminalSnapshotLocked(snapshots[i]);
                AddDiagnosticLocked(instance);

                if (activeScene?.InstanceId == instance.InstanceId)
                {
                    Volatile.Write(ref m_ActiveScene, null);
                    activeScene = null;
                }
            }
        }

        return snapshots;
    }

    private bool TryValidateUnloadReferences(
        RuntimeSceneInstance[] instances,
        out string diagnostic)
    {
        var unloading = new HashSet<long>(instances.Select(instance => instance.InstanceId.Value));
        lock (m_Gate)
        {
            EntityManager entityManager = ResolveEntityManager();
            if (entityManager.HasPool<ParentComponent>())
            {
                ComponentPool<ParentComponent> pool = entityManager.GetPool<ParentComponent>();
                Entity[] owners = pool.GetRawEntityArray();
                ParentComponent[] components = pool.GetRawComponentArray();
                for (int i = 0; i < pool.Count; i++)
                {
                    if (!TryValidateReference(
                            owners[i],
                            components[i].Parent,
                            unloading,
                            nameof(ParentComponent.Parent),
                            out diagnostic))
                    {
                        return false;
                    }
                }
            }

            if (entityManager.HasPool<ChildComponent>())
            {
                ComponentPool<ChildComponent> pool = entityManager.GetPool<ChildComponent>();
                Entity[] owners = pool.GetRawEntityArray();
                ChildComponent[] components = pool.GetRawComponentArray();
                for (int i = 0; i < pool.Count; i++)
                {
                    if (!TryValidateReference(
                            owners[i],
                            components[i].FirstChild,
                            unloading,
                            nameof(ChildComponent.FirstChild),
                            out diagnostic))
                    {
                        return false;
                    }
                }
            }

            if (entityManager.HasPool<SiblingComponent>())
            {
                ComponentPool<SiblingComponent> pool = entityManager.GetPool<SiblingComponent>();
                Entity[] owners = pool.GetRawEntityArray();
                SiblingComponent[] components = pool.GetRawComponentArray();
                for (int i = 0; i < pool.Count; i++)
                {
                    if (!TryValidateReference(
                            owners[i],
                            components[i].PrevSibling,
                            unloading,
                            nameof(SiblingComponent.PrevSibling),
                            out diagnostic)
                        || !TryValidateReference(
                            owners[i],
                            components[i].NextSibling,
                            unloading,
                            nameof(SiblingComponent.NextSibling),
                            out diagnostic))
                    {
                        return false;
                    }
                }
            }
        }

        diagnostic = string.Empty;
        return true;
    }

    private bool TryValidateReference(
        Entity source,
        Entity target,
        HashSet<long> unloading,
        string fieldName,
        out string diagnostic)
    {
        if (target.IsNull)
        {
            diagnostic = string.Empty;
            return true;
        }

        bool sourceIsUnloading = m_EntityOwners.TryGetValue(source, out RuntimeSceneInstanceId sourceOwner)
            && unloading.Contains(sourceOwner.Value);
        bool targetIsUnloading = m_EntityOwners.TryGetValue(target, out RuntimeSceneInstanceId targetOwner)
            && unloading.Contains(targetOwner.Value);
        if (sourceIsUnloading == targetIsUnloading)
        {
            diagnostic = string.Empty;
            return true;
        }

        diagnostic =
            $"[RuntimeSceneService] Cannot unload scene ownership because hierarchy field '{fieldName}' " +
            $"crosses the unload boundary: {source} -> {target}. Cross-instance hierarchy references must be removed first.";
        return false;
    }

    private SceneLoadResult FailActivation(RuntimeSceneInstance instance, string diagnostic)
    {
        var result = new SceneLoadResult(false, 0, 0, 0, 0, 0, 0, 0, diagnostic);
        RuntimeSceneInstanceSnapshot snapshot;
        lock (m_Gate)
        {
            instance.State = RuntimeSceneInstanceState.Failed;
            instance.Diagnostic = diagnostic;
            snapshot = instance.Snapshot();
            m_Instances.Remove(instance.InstanceId.Value);
            AddTerminalSnapshotLocked(snapshot);
            AddDiagnosticLocked(instance);
        }

        PublishSceneInstanceStateChanged(snapshot);
        PublishLoadCompleted(new RuntimeSceneLoadReport(
            instance.Scene,
            instance.SourceRevision,
            result,
            instance.InstanceId,
            instance.Kind));
        PlotInstanceCounts();
        return result;
    }

    private EntityManager ResolveEntityManager()
    {
        EntityManager entityManager = m_EntityManagerProvider()
            ?? throw new InvalidOperationException(
                "[RuntimeSceneService] The active EntityManager is not initialized.");
        lock (m_Gate)
        {
            if (m_EntityManager == null)
            {
                m_EntityManager = entityManager;
            }
            else if (!ReferenceEquals(m_EntityManager, entityManager))
            {
                throw new InvalidOperationException(
                    "[RuntimeSceneService] SceneSubsystem replaced its EntityManager. " +
                    "Additive scene instances require one stable ECS world.");
            }
        }

        return entityManager;
    }

    private void AddTerminalSnapshotLocked(RuntimeSceneInstanceSnapshot snapshot)
    {
        m_TerminalSnapshots.Enqueue(snapshot);
        while (m_TerminalSnapshots.Count > MaxTerminalSnapshots)
        {
            m_TerminalSnapshots.Dequeue();
        }
    }

    private void AddDiagnosticLocked(RuntimeSceneInstance instance)
    {
        m_Diagnostics.Enqueue(new RuntimeSceneDiagnostic(
            ++m_NextDiagnosticSequence,
            instance.InstanceId,
            instance.Scene,
            instance.State,
            instance.Diagnostic));
        while (m_Diagnostics.Count > MaxDiagnostics)
        {
            m_Diagnostics.Dequeue();
        }
    }

    private void PlotInstanceCounts()
    {
        int activeCount;
        int ownedEntityCount;
        lock (m_Gate)
        {
            activeCount = m_Instances.Values.Count(instance =>
                instance.State == RuntimeSceneInstanceState.Active
                || instance.State == RuntimeSceneInstanceState.QueuedForUnload);
            ownedEntityCount = m_EntityOwners.Count;
        }

        Profiler.PlotValue("SceneInstances.Active", activeCount);
        Profiler.PlotValue("SceneInstances.OwnedEntities", ownedEntityCount);
    }

    private void PublishActiveSceneChanged(RuntimeSceneState state)
    {
        var handlers = ActiveSceneChanged;
        if (handlers == null)
        {
            return;
        }

        foreach (Action<RuntimeSceneState> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(state);
            }
            catch
            {
                // Tooling observers must not invalidate an already activated runtime world.
            }
        }
    }

    private void PublishLoadCompleted(RuntimeSceneLoadReport report)
    {
        var handlers = SceneLoadCompleted;
        if (handlers == null)
        {
            return;
        }

        foreach (Action<RuntimeSceneLoadReport> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(report);
            }
            catch
            {
                // Load reporting is diagnostic and cannot affect scene activation.
            }
        }
    }

    private void PublishSceneInstanceStateChanged(RuntimeSceneInstanceSnapshot snapshot)
    {
        var handlers = SceneInstanceStateChanged;
        if (handlers == null)
        {
            return;
        }

        foreach (Action<RuntimeSceneInstanceSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(snapshot);
            }
            catch
            {
                // Lifecycle observers are isolated from frame-boundary structural mutation.
            }
        }
    }
}
