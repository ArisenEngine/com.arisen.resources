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
    private IAssetDatabase? m_AssetDatabase;
    private RuntimeSceneService? m_RuntimeSceneService;
    private RuntimeWorldStreamingService? m_RuntimeWorldStreamingService;
    private RuntimeAssetResidencyService? m_RuntimeAssetResidencyService;
    private WorldOriginService? m_WorldOriginService;
    private RuntimeSmokeScenarioRegistry? m_SmokeScenarioRegistry;
    private WorldStreamingSmokeScenarioProvider? m_WorldStreamingSmokeProvider;
    private SampleSwitchSmokeScenarioProvider? m_SampleSwitchSmokeProvider;
    private IRuntimeAssetCookerRegistry? m_RuntimeAssetCookerRegistry;
    private SceneRuntimeAssetCooker? m_SceneRuntimeAssetCooker;
    private WorldRuntimeAssetCooker? m_WorldRuntimeAssetCooker;
    private SampleCatalogueRuntimeAssetCooker? m_SampleCatalogueRuntimeAssetCooker;
    private SampleCatalogueService? m_SampleCatalogueService;
    private bool m_FrameEndSubscribed;

    public bool HasPendingOwnership =>
        m_AssetDatabase != null ||
        m_RuntimeSceneService != null ||
        m_RuntimeWorldStreamingService != null ||
        m_RuntimeAssetResidencyService != null ||
        m_WorldOriginService != null ||
        m_SmokeScenarioRegistry != null ||
        m_WorldStreamingSmokeProvider != null ||
        m_SampleSwitchSmokeProvider != null ||
        m_RuntimeAssetCookerRegistry != null ||
        m_SceneRuntimeAssetCooker != null ||
        m_WorldRuntimeAssetCooker != null ||
        m_SampleCatalogueRuntimeAssetCooker != null ||
        m_SampleCatalogueService != null ||
        m_FrameEndSubscribed;

    public void OnLoad(IServiceRegistry registry)
    {
        if (HasPendingOwnership)
        {
            throw new InvalidOperationException(
                "Resources package still owns state from a prior load attempt.");
        }

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
        m_AssetDatabase = database;
        try
        {
#if ARISEN_DEPLOYED
            if (config?.ExecutionMode == EngineExecutionMode.RuntimeAssetCook)
            {
                database.InitializeWorkspace(workspaceRoot, packages, sourceAccessMode);
            }
            else
            {
                database.InitializeRuntimeCatalog(
                    AppContext.BaseDirectory,
                    config?.Profile ?? throw new InvalidOperationException(
                        "[Resources] A deployed profile must resolve its workspace profile name " +
                        "before the runtime asset catalog can be mounted."));
            }
#else
            database.InitializeWorkspace(workspaceRoot, packages, sourceAccessMode);
#endif
            registry.RegisterService<IAssetDatabase>(database);
            registry.RegisterService<IAssetSourceIndex>(database);
            registry.RegisterService<ISceneComponentExtensionRegistry>(
                SceneComponentExtensionRegistry.Shared);
            IRuntimeAssetCookerRegistry cookerRegistry =
                registry.GetService<IRuntimeAssetCookerRegistry>();
            m_RuntimeAssetCookerRegistry = cookerRegistry;
            m_SceneRuntimeAssetCooker = new SceneRuntimeAssetCooker(database);
            m_WorldRuntimeAssetCooker = new WorldRuntimeAssetCooker(database);
            m_SampleCatalogueRuntimeAssetCooker = new SampleCatalogueRuntimeAssetCooker(database);
            cookerRegistry.RegisterCooker(m_SceneRuntimeAssetCooker);
            cookerRegistry.RegisterCooker(m_WorldRuntimeAssetCooker);
            cookerRegistry.RegisterCooker(m_SampleCatalogueRuntimeAssetCooker);

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
            m_SampleCatalogueService = new SampleCatalogueService(
                database,
                registry.GetService<ProjectSubsystem>());
            registry.RegisterService<ISampleCatalogueService>(m_SampleCatalogueService);
            m_RuntimeWorldStreamingService = new RuntimeWorldStreamingService(
                database,
                m_RuntimeSceneService,
                registry.GetService<IBackgroundTaskScheduler>(),
                residencyService: m_RuntimeAssetResidencyService,
                originService: m_WorldOriginService);
            registry.RegisterService<IRuntimeWorldStreamingService>(m_RuntimeWorldStreamingService);
            m_SmokeScenarioRegistry = new RuntimeSmokeScenarioRegistry();
            m_WorldStreamingSmokeProvider = new WorldStreamingSmokeScenarioProvider(
                m_RuntimeWorldStreamingService,
                m_RuntimeSceneService,
                m_RuntimeAssetResidencyService,
                m_WorldOriginService,
                database,
                registry.GetService<IBackgroundTaskScheduler>());
            m_SmokeScenarioRegistry.Register("world-streaming", m_WorldStreamingSmokeProvider);
            m_SampleSwitchSmokeProvider = new SampleSwitchSmokeScenarioProvider(
                m_SampleCatalogueService,
                m_RuntimeWorldStreamingService,
                m_RuntimeSceneService,
                m_RuntimeAssetResidencyService,
                registry.GetService<IBackgroundTaskScheduler>(),
                database);
            m_SmokeScenarioRegistry.Register(
                SampleSwitchSmokeScenarioProvider.ModeName,
                m_SampleSwitchSmokeProvider);
            registry.RegisterService<IRuntimeSmokeScenarioRegistry>(m_SmokeScenarioRegistry);
            registry.RegisterService<IRuntimeSmokeScenarioProvider>(m_SmokeScenarioRegistry);
            EngineKernel.Instance.OnFrameEnd += ProcessPendingSceneLoad;
            m_FrameEndSubscribed = true;

            KernelLog.InfoFormat(
                "[ResourcesPackage] Loaded asset database in {0} mode with {1} source access: " +
                "{2} indexed source asset(s), cooked root '{3}'.",
                database.Mode,
                database.SourceAccessMode,
                database.Assets.Count,
                database.CookedRoot);
        }
        catch (Exception loadError)
        {
            var rollbackFailures = new List<Exception>();
            UnloadOwnedState("load rollback", rollbackFailures);
            if (rollbackFailures.Count > 0)
            {
                rollbackFailures.Insert(0, loadError);
                throw new AggregateException(
                    "[ResourcesPackage] Package load failed and ownership rollback reported additional errors.",
                    rollbackFailures);
            }

            throw;
        }
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
        var failures = new List<Exception>();
        UnloadOwnedState("package unload", failures);
        if (failures.Count > 0)
        {
            throw new AggregateException(
                "[ResourcesPackage] One or more package teardown stages failed.",
                failures);
        }
    }

    private void UnloadOwnedState(
        string context,
        ICollection<Exception> failures)
    {
        if (m_FrameEndSubscribed)
        {
            AttemptUnloadStage(
                "frame-end callback removal",
                () => EngineKernel.Instance.OnFrameEnd -= ProcessPendingSceneLoad,
                () => m_FrameEndSubscribed = false,
                failures);
        }

        if (m_FrameEndSubscribed)
        {
            return;
        }

        if (m_SmokeScenarioRegistry != null ||
            m_WorldStreamingSmokeProvider != null ||
            m_SampleSwitchSmokeProvider != null)
        {
            AttemptUnloadStage(
                "runtime smoke provider unregister",
                () =>
                {
                    if (m_SmokeScenarioRegistry != null && m_WorldStreamingSmokeProvider != null)
                    {
                        m_SmokeScenarioRegistry.Unregister(
                            "world-streaming",
                            m_WorldStreamingSmokeProvider);
                    }

                    if (m_SmokeScenarioRegistry != null && m_SampleSwitchSmokeProvider != null)
                    {
                        m_SmokeScenarioRegistry.Unregister(
                            SampleSwitchSmokeScenarioProvider.ModeName,
                            m_SampleSwitchSmokeProvider);
                    }
                },
                () =>
                {
                    m_WorldStreamingSmokeProvider = null;
                    m_SampleSwitchSmokeProvider = null;
                    m_SmokeScenarioRegistry = null;
                },
                failures);
        }

        if (m_SmokeScenarioRegistry != null ||
            m_WorldStreamingSmokeProvider != null ||
            m_SampleSwitchSmokeProvider != null)
        {
            return;
        }

        if (m_RuntimeWorldStreamingService != null)
        {
            AttemptUnloadStage(
                "runtime world streaming shutdown",
                () => m_RuntimeWorldStreamingService.Shutdown(unloadActiveCells: false),
                () => m_RuntimeWorldStreamingService = null,
                failures);
        }

        if (m_RuntimeWorldStreamingService != null)
        {
            return;
        }

        if (m_RuntimeSceneService != null)
        {
            AttemptUnloadStage(
                "runtime scene shutdown",
                m_RuntimeSceneService.ClearForShutdown,
                () => m_RuntimeSceneService = null,
                failures);
        }

        if (m_RuntimeAssetResidencyService != null)
        {
            AttemptUnloadStage(
                "runtime asset residency disposal",
                m_RuntimeAssetResidencyService.Dispose,
                () => m_RuntimeAssetResidencyService = null,
                failures);
        }

        UnregisterRuntimeAssetCookers(context, failures);

        bool assetDatabaseDependenciesReleased =
            m_RuntimeSceneService == null &&
            m_RuntimeAssetResidencyService == null &&
            m_RuntimeAssetCookerRegistry == null &&
            m_SceneRuntimeAssetCooker == null &&
            m_WorldRuntimeAssetCooker == null &&
            m_SampleCatalogueRuntimeAssetCooker == null;
        if (assetDatabaseDependenciesReleased && m_AssetDatabase != null)
        {
            AttemptUnloadStage(
                "asset database cooked-handle release",
                m_AssetDatabase.ReleaseAllLoadedCookedAssets,
                () => m_AssetDatabase = null,
                failures);
        }

        m_WorldOriginService = null;
        m_SampleCatalogueService = null;
    }

    private void UnregisterRuntimeAssetCookers(
        string context,
        ICollection<Exception> failures)
    {
        IRuntimeAssetCookerRegistry? cookerRegistry = m_RuntimeAssetCookerRegistry;
        if (cookerRegistry == null)
        {
            return;
        }

        if (m_WorldRuntimeAssetCooker != null)
        {
            WorldRuntimeAssetCooker cooker = m_WorldRuntimeAssetCooker;
            AttemptUnloadStage(
                $"{context} world runtime asset cooker unregister",
                () => cookerRegistry.UnregisterCooker(cooker),
                () => m_WorldRuntimeAssetCooker = null,
                failures);
        }

        if (m_SceneRuntimeAssetCooker != null)
        {
            SceneRuntimeAssetCooker cooker = m_SceneRuntimeAssetCooker;
            AttemptUnloadStage(
                $"{context} scene runtime asset cooker unregister",
                () => cookerRegistry.UnregisterCooker(cooker),
                () => m_SceneRuntimeAssetCooker = null,
                failures);
        }

        if (m_SampleCatalogueRuntimeAssetCooker != null)
        {
            SampleCatalogueRuntimeAssetCooker cooker = m_SampleCatalogueRuntimeAssetCooker;
            AttemptUnloadStage(
                $"{context} sample catalogue runtime asset cooker unregister",
                () => cookerRegistry.UnregisterCooker(cooker),
                () => m_SampleCatalogueRuntimeAssetCooker = null,
                failures);
        }

        if (m_WorldRuntimeAssetCooker == null &&
            m_SceneRuntimeAssetCooker == null &&
            m_SampleCatalogueRuntimeAssetCooker == null)
        {
            m_RuntimeAssetCookerRegistry = null;
        }
    }

    private static void AttemptUnloadStage(
        string stage,
        Action teardown,
        Action clearOwnership,
        ICollection<Exception> failures)
    {
        try
        {
            teardown();
            clearOwnership();
        }
        catch (Exception ex)
        {
            failures.Add(new InvalidOperationException(
                $"[ResourcesPackage] Failed to complete {stage}.",
                ex));
        }
    }

    private void ProcessPendingSceneLoad()
    {
        var result = m_RuntimeWorldStreamingService?.ProcessAtFrameBoundary();
        if (result.HasValue && !result.Value.Success)
        {
            KernelLog.WarningFormat(
                "[ResourcesPackage] Queued scene activation was rejected: {0}",
                result.Value.Diagnostic);
        }
    }
}
