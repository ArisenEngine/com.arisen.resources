using System;
using System.Threading;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.ECS;

namespace ArisenEngine.Resources.Serialization;

public sealed record RuntimeSceneState(
    AssetRef<SceneSourceAsset> Scene,
    string Name,
    string SourcePath,
    EntityManager EntityManager,
    long SourceRevision = 0);

public sealed record RuntimeSceneLoadReport(
    AssetRef<SceneSourceAsset> Scene,
    long SourceRevision,
    SceneLoadResult Result);

public interface IRuntimeSceneService
{
    RuntimeSceneState? ActiveScene { get; }

    event Action<RuntimeSceneState>? ActiveSceneChanged;

    event Action<RuntimeSceneLoadReport>? SceneLoadCompleted;

    SceneLoadResult LoadScene(AssetRef<SceneSourceAsset> scene);

    void RequestSceneLoad(AssetRef<SceneSourceAsset> scene);

    void RequestSceneLoad(SceneSourceSnapshot snapshot);
}

public sealed class RuntimeSceneService : IRuntimeSceneService
{
    private sealed record PendingSceneLoadRequest(
        AssetRef<SceneSourceAsset> Scene,
        SceneSourceSnapshot? Snapshot);

    private readonly IAssetDatabase m_AssetDatabase;
    private readonly Action<EntityManager> m_ActivateEntityManager;
    private RuntimeSceneState? m_ActiveScene;
    private PendingSceneLoadRequest? m_PendingSceneLoad;

    public RuntimeSceneState? ActiveScene => Volatile.Read(ref m_ActiveScene);

    public event Action<RuntimeSceneState>? ActiveSceneChanged;

    public event Action<RuntimeSceneLoadReport>? SceneLoadCompleted;

    public RuntimeSceneService(
        IAssetDatabase assetDatabase,
        Action<EntityManager> activateEntityManager)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_ActivateEntityManager = activateEntityManager ?? throw new ArgumentNullException(nameof(activateEntityManager));
    }

    public SceneLoadResult LoadScene(AssetRef<SceneSourceAsset> scene)
    {
        return LoadSceneCore(scene, null);
    }

    private SceneLoadResult LoadSceneCore(
        AssetRef<SceneSourceAsset> scene,
        SceneSourceSnapshot? snapshot)
    {
        using var _ = Profiler.Zone("RuntimeSceneService.LoadScene");
        var candidate = new EntityManager();
        var result = snapshot == null
            ? SceneAssetLoader.LoadScene(m_AssetDatabase, scene, candidate)
            : SceneAssetLoader.LoadScene(m_AssetDatabase, snapshot, candidate);
        long sourceRevision = snapshot?.Revision ?? 0;
        if (!result.Success)
        {
            PublishLoadCompleted(new RuntimeSceneLoadReport(scene, sourceRevision, result));
            return result;
        }

        m_ActivateEntityManager(candidate);

        var state = new RuntimeSceneState(
            scene,
            result.SceneName,
            result.SourcePath,
            candidate,
            sourceRevision);
        Volatile.Write(ref m_ActiveScene, state);
        PublishActiveSceneChanged(state);
        PublishLoadCompleted(new RuntimeSceneLoadReport(scene, sourceRevision, result));
        return result;
    }

    public void RequestSceneLoad(AssetRef<SceneSourceAsset> scene)
    {
        if (!scene.IsValid)
        {
            throw new ArgumentException("Queued scene load requires a valid scene asset reference.", nameof(scene));
        }

        Interlocked.Exchange(ref m_PendingSceneLoad, new PendingSceneLoadRequest(scene, null));
    }

    public void RequestSceneLoad(SceneSourceSnapshot snapshot)
    {
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        if (!snapshot.IsValid)
        {
            throw new ArgumentException(
                "Queued scene source snapshot must contain a valid scene, source path, text, and revision.",
                nameof(snapshot));
        }

        Interlocked.Exchange(
            ref m_PendingSceneLoad,
            new PendingSceneLoadRequest(snapshot.Scene, snapshot));
    }

    internal SceneLoadResult? ProcessPendingSceneLoadAtFrameBoundary()
    {
        var request = Interlocked.Exchange(ref m_PendingSceneLoad, null);
        if (request == null)
        {
            return null;
        }

        SceneLoadResult result;
        try
        {
            result = LoadSceneCore(request.Scene, request.Snapshot);
        }
        catch (Exception ex)
        {
            result = new SceneLoadResult(
                false,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                $"[RuntimeSceneService] Queued scene load failed: {ex.Message}");
            PublishLoadCompleted(new RuntimeSceneLoadReport(
                request.Scene,
                request.Snapshot?.Revision ?? 0,
                result));
        }

        return result;
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
}
