using ArisenKernel.Packages;
using ArisenKernel.Services;
using ArisenKernel.Diagnostics;
using ArisenKernel.Lifecycle;
using ArisenEngine.Core.Assets;
using ArisenEngine.ECS.Lifecycle;
using ArisenEngine.Resources.Serialization;

namespace ArisenEngine.Resources;

public class ResourcesPackage : IPackageEntry
{
    private IRuntimeSceneService? m_RuntimeSceneService;

    public void OnLoad(IServiceRegistry registry)
    {
        var config = EngineKernel.Instance.Config;
        var workspaceRoot = config?.ProjectRoot ?? Directory.GetCurrentDirectory();
        var packages = (config?.PackageUrls ?? new List<string>())
            .Where(Directory.Exists)
            .Select(path => (PackageId: Path.GetFileName(path), PackageRoot: Path.GetFullPath(path)))
            .ToArray();

        var database = AssetDatabase.Instance;
        database.InitializeWorkspace(workspaceRoot, packages);
        registry.RegisterService<IAssetDatabase>(database);

        var sceneSubsystem = EngineKernel.Instance.GetSubsystem<SceneSubsystem>()
            ?? throw new InvalidOperationException("Runtime scene service requires SceneSubsystem to be selected.");
        m_RuntimeSceneService = new RuntimeSceneService(database, sceneSubsystem.ActivateEntityManager);
        registry.RegisterService<IRuntimeSceneService>(m_RuntimeSceneService);

        KernelLog.InfoFormat(
            "[ResourcesPackage] Loaded: indexed {0} asset(s), cooked root '{1}'.",
            database.Assets.Count,
            database.CookedRoot);
    }

    public void OnUnload(IServiceRegistry registry)
    {
        AssetDatabase.Instance.ReleaseAllLoadedCookedAssets();
        m_RuntimeSceneService = null;
    }
}
