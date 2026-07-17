using System;
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
}

public sealed class RuntimeSceneService : IRuntimeSceneService
{
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly Action<EntityManager> m_ActivateEntityManager;

    public RuntimeSceneState? ActiveScene { get; private set; }

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
        ActiveScene = state;
        ActiveSceneChanged?.Invoke(state);
        return result;
    }
}
