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
    private RuntimeSceneService? m_RuntimeSceneService;

    public void OnLoad(IServiceRegistry registry)
    {
        var config = EngineKernel.Instance.Config;
        var workspaceRoot = config?.ProjectRoot ?? Directory.GetCurrentDirectory();
        var packages = (config?.PackageUrls ?? new List<string>())
            .Where(Directory.Exists)
            .Select(ReadPackageLocation)
            .ToArray();

        var database = AssetDatabase.Instance;
        database.InitializeWorkspace(workspaceRoot, packages);
        registry.RegisterService<IAssetDatabase>(database);

        var sceneSubsystem = EngineKernel.Instance.GetSubsystem<SceneSubsystem>()
            ?? throw new InvalidOperationException("Runtime scene service requires SceneSubsystem to be selected.");
        m_RuntimeSceneService = new RuntimeSceneService(database, sceneSubsystem.ActivateEntityManager);
        registry.RegisterService<IRuntimeSceneService>(m_RuntimeSceneService);
        EngineKernel.Instance.OnFrameEnd += ProcessPendingSceneLoad;

        KernelLog.InfoFormat(
            "[ResourcesPackage] Loaded: indexed {0} asset(s), cooked root '{1}'.",
            database.Assets.Count,
            database.CookedRoot);
    }

    private static (string PackageId, string PackageRoot) ReadPackageLocation(string packagePath)
    {
        string packageRoot = Path.GetFullPath(packagePath);
        string manifestPath = Path.Combine(packageRoot, "package.json");
        using var manifest = ManifestJson.ParseDocumentFile(manifestPath);
        if (!manifest.RootElement.TryGetPropertyIC("id", out var idElement) ||
            idElement.ValueKind != System.Text.Json.JsonValueKind.String ||
            string.IsNullOrWhiteSpace(idElement.GetString()))
        {
            throw new InvalidOperationException(
                $"Asset indexing requires package manifest '{manifestPath}' to declare a non-empty id.");
        }

        return (idElement.GetString()!.Trim(), packageRoot);
    }

    public void OnUnload(IServiceRegistry registry)
    {
        EngineKernel.Instance.OnFrameEnd -= ProcessPendingSceneLoad;
        AssetDatabase.Instance.ReleaseAllLoadedCookedAssets();
        m_RuntimeSceneService = null;
    }

    private void ProcessPendingSceneLoad()
    {
        var result = m_RuntimeSceneService?.ProcessPendingSceneLoadAtFrameBoundary();
        if (result.HasValue && !result.Value.Success)
        {
            KernelLog.WarningFormat(
                "[ResourcesPackage] Queued scene activation was rejected: {0}",
                result.Value.Diagnostic);
        }
    }
}
