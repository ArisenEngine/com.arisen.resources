using ArisenKernel.Packages;
using ArisenKernel.Services;
using ArisenKernel.Diagnostics;
using ArisenKernel.Lifecycle;
using ArisenEngine.Core.Assets;
using ArisenEngine.ECS.Lifecycle;
using ArisenEngine.Resources.Serialization;
using ArisenEngine.Threading;

namespace ArisenEngine.Resources;

public class ResourcesPackage : IPackageEntry
{
    private RuntimeSceneService? m_RuntimeSceneService;
    private RuntimeWorldStreamingService? m_RuntimeWorldStreamingService;
    private RuntimeAssetResidencyService? m_RuntimeAssetResidencyService;
    private WorldOriginService? m_WorldOriginService;

    public void OnLoad(IServiceRegistry registry)
    {
        var config = EngineKernel.Instance.Config;
        var workspaceRoot = config?.ProjectRoot ?? Directory.GetCurrentDirectory();
        var packages = (config?.PackageUrls ?? new List<string>())
            .Where(Directory.Exists)
            .Select(ReadPackageLocation)
            .ToArray();
        AssetSourceAccessMode sourceAccessMode;
#if ARISEN_ENGINE_EDITOR
        sourceAccessMode = AssetSourceAccessMode.EditorAuthoring;
#else
        sourceAccessMode = config?.ExecutionMode == EngineExecutionMode.RuntimeAssetCook
            ? AssetSourceAccessMode.RuntimeAssetCook
            : config?.EnableSourceAssetDiagnostics == true
                ? AssetSourceAccessMode.Diagnostic
                : AssetSourceAccessMode.Disabled;
#endif

        var database = AssetDatabase.Instance;
#if ARISEN_PROFILE_PRODUCTION
        if (config?.ExecutionMode == EngineExecutionMode.RuntimeAssetCook)
        {
            database.InitializeWorkspace(workspaceRoot, packages, sourceAccessMode);
        }
        else
        {
            database.InitializeRuntimeCatalog(AppContext.BaseDirectory, "Production");
        }
#else
        database.InitializeWorkspace(workspaceRoot, packages, sourceAccessMode);
#endif
        registry.RegisterService<IAssetDatabase>(database);
        IRuntimeAssetCookerRegistry cookerRegistry = registry.GetService<IRuntimeAssetCookerRegistry>();
        cookerRegistry.RegisterCooker(new SceneRuntimeAssetCooker(database));
        cookerRegistry.RegisterCooker(new WorldRuntimeAssetCooker(database));

        var sceneSubsystem = EngineKernel.Instance.GetSubsystem<SceneSubsystem>()
            ?? throw new InvalidOperationException("Runtime scene service requires SceneSubsystem to be selected.");
        m_RuntimeSceneService = new RuntimeSceneService(
            database,
            () => sceneSubsystem.ActiveEntityManager);
        registry.RegisterService<IRuntimeSceneService>(m_RuntimeSceneService);
        m_RuntimeAssetResidencyService = new RuntimeAssetResidencyService(database);
        registry.RegisterService<IRuntimeAssetResidencyService>(m_RuntimeAssetResidencyService);
        m_WorldOriginService = new WorldOriginService();
        registry.RegisterService<IWorldOriginService>(m_WorldOriginService);
        m_RuntimeWorldStreamingService = new RuntimeWorldStreamingService(
            database,
            m_RuntimeSceneService,
            registry.GetService<IBackgroundTaskScheduler>(),
            residencyService: m_RuntimeAssetResidencyService,
            originService: m_WorldOriginService);
        registry.RegisterService<IRuntimeWorldStreamingService>(m_RuntimeWorldStreamingService);
        registry.RegisterService<IRuntimeSmokeScenarioProvider>(
            new WorldStreamingSmokeScenarioProvider(
                m_RuntimeWorldStreamingService,
                m_RuntimeSceneService,
                m_RuntimeAssetResidencyService,
                m_WorldOriginService,
                database,
                registry.GetService<IBackgroundTaskScheduler>()));
        EngineKernel.Instance.OnFrameEnd += ProcessPendingSceneLoad;

        KernelLog.InfoFormat(
            "[ResourcesPackage] Loaded asset database in {0} mode with {1} source access: " +
            "{2} indexed source asset(s), cooked root '{3}'.",
            database.Mode,
            database.SourceAccessMode,
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
        m_RuntimeWorldStreamingService?.Shutdown(unloadActiveCells: false);
        m_RuntimeSceneService?.ClearForShutdown();
        m_RuntimeAssetResidencyService?.Dispose();
        AssetDatabase.Instance.ReleaseAllLoadedCookedAssets();
        m_RuntimeWorldStreamingService = null;
        m_RuntimeSceneService = null;
        m_RuntimeAssetResidencyService = null;
        m_WorldOriginService = null;
    }

    private void ProcessPendingSceneLoad()
    {
        m_RuntimeWorldStreamingService?.ProcessAtFrameBoundary();
        var result = m_RuntimeSceneService?.ProcessPendingSceneLoadAtFrameBoundary();
        if (result.HasValue && !result.Value.Success)
        {
            KernelLog.WarningFormat(
                "[ResourcesPackage] Queued scene activation was rejected: {0}",
                result.Value.Diagnostic);
        }
    }
}
