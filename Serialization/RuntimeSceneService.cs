using System;
using System.Threading;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.ECS;

namespace ArisenEngine.Resources.Serialization;

public sealed record RuntimeSceneState(
    AssetRef<SceneSourceAsset> Scene,
    string Name,
    string SourcePath,
    EntityManager EntityManager);

public interface IRuntimeSceneService
{
    RuntimeSceneState? ActiveScene { get; }

    event Action<RuntimeSceneState>? ActiveSceneChanged;

    SceneLoadResult LoadScene(AssetRef<SceneSourceAsset> scene);

    void RequestSceneLoad(AssetRef<SceneSourceAsset> scene);
}

public sealed class RuntimeSceneService : IRuntimeSceneService
{
    private sealed record PendingSceneLoadRequest(AssetRef<SceneSourceAsset> Scene);

    private readonly IAssetDatabase m_AssetDatabase;
    private readonly Action<EntityManager> m_ActivateEntityManager;
    private RuntimeSceneState? m_ActiveScene;
    private PendingSceneLoadRequest? m_PendingSceneLoad;

    public RuntimeSceneState? ActiveScene => Volatile.Read(ref m_ActiveScene);

    public event Action<RuntimeSceneState>? ActiveSceneChanged;

    public RuntimeSceneService(
        IAssetDatabase assetDatabase,
        Action<EntityManager> activateEntityManager)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_ActivateEntityManager = activateEntityManager ?? throw new ArgumentNullException(nameof(activateEntityManager));
    }

    public SceneLoadResult LoadScene(AssetRef<SceneSourceAsset> scene)
    {
        var candidate = new EntityManager();
        var result = SceneAssetLoader.LoadScene(m_AssetDatabase, scene, candidate);
        if (!result.Success)
        {
            return result;
        }

        m_ActivateEntityManager(candidate);

        var state = new RuntimeSceneState(
            scene,
            result.SceneName,
            result.SourcePath,
            candidate);
        Volatile.Write(ref m_ActiveScene, state);
        ActiveSceneChanged?.Invoke(state);
        return result;
    }

    public void RequestSceneLoad(AssetRef<SceneSourceAsset> scene)
    {
        if (!scene.IsValid)
        {
            throw new ArgumentException("Queued scene load requires a valid scene asset reference.", nameof(scene));
        }

        Interlocked.Exchange(ref m_PendingSceneLoad, new PendingSceneLoadRequest(scene));
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
            result = LoadScene(request.Scene);
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
        }

        return result;
    }
}
