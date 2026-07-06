using ArisenKernel.Packages;
using ArisenKernel.Services;
using ArisenKernel.Diagnostics;
using ArisenKernel.Lifecycle;
using ArisenEngine.Core.Assets;

namespace ArisenEngine.Resources;

public class ResourcesPackage : IPackageEntry
{
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

        KernelLog.InfoFormat(
            "[ResourcesPackage] Loaded: indexed {0} asset(s), cooked root '{1}'.",
            database.Assets.Count,
            database.CookedRoot);
    }

    public void OnUnload(IServiceRegistry registry)
    {
        AssetDatabase.Instance.ReleaseAllLoadedCookedAssets();
    }
}
