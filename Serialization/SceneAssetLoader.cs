using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.ECS;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ArisenEngine.Resources.Serialization;

public readonly record struct SceneLoadResult(
    bool Success,
    int EntityCount,
    int CameraCount,
    int MeshRendererCount,
    int DirectionalLightCount,
    int PointLightCount,
    int SpotLightCount,
    int EnvironmentCount,
    string Diagnostic,
    string SceneName = "",
    string SourcePath = "",
    SceneAuthoringEntityMap? AuthoringEntities = null);

public readonly record struct SceneInspectionResult(
    bool Success,
    string SceneName,
    string SourcePath,
    int EntityCount,
    int CameraCount,
    int MeshRendererCount,
    int DirectionalLightCount,
    int PointLightCount,
    int SpotLightCount,
    int EnvironmentCount,
    IReadOnlyList<SceneEntityInspection> Entities,
    IReadOnlyList<string> Diagnostics)
{
    public string Diagnostic => Diagnostics == null || Diagnostics.Count == 0
        ? string.Empty
        : string.Join(Environment.NewLine, Diagnostics);
}

public readonly record struct SceneAssetEditResult(
    bool Success,
    string Diagnostic,
    string UpdatedSource = "");

public sealed record SceneSourceSnapshot(
    AssetRef<SceneSourceAsset> Scene,
    string SourcePath,
    string SourceText,
    long Revision)
{
    public bool IsValid =>
        Scene.IsValid &&
        !string.IsNullOrWhiteSpace(SourcePath) &&
        !string.IsNullOrWhiteSpace(SourceText) &&
        Revision >= 0;
}

public sealed record SceneEntityInspection(
    Guid AuthoringGuid,
    Guid ParentGuid,
    Guid ParentSceneGuid,
    string Name,
    SceneTransformInspection Transform,
    SceneCameraInspection? Camera,
    SceneMeshRendererInspection? MeshRenderer,
    SceneDirectionalLightInspection? DirectionalLight,
    ScenePointLightInspection? PointLight,
    SceneSpotLightInspection? SpotLight,
    SceneEnvironmentInspection? Environment);

public sealed record SceneTransformInspection(
    Vector3 Position,
    Quaternion Rotation,
    Vector3 Scale);

public sealed record SceneCameraInspection(
    float VerticalFov,
    float NearPlane,
    float FarPlane,
    bool IsPerspective);

public sealed record SceneMeshRendererInspection(
    SceneAssetReferenceInspection Mesh,
    SceneAssetReferenceInspection Material,
    int FirstSubmeshIndex,
    int SubmeshCount,
    Vector3 BoundsCenter,
    Vector3 BoundsExtents,
    bool Visible);

public sealed record SceneDirectionalLightInspection(
    Vector3 Direction,
    Vector3 Color,
    float Intensity,
    float AmbientIntensity,
    bool Enabled);

public sealed record ScenePointLightInspection(
    Vector3 Color,
    float Intensity,
    float Range,
    bool Enabled);

public sealed record SceneSpotLightInspection(
    Vector3 Color,
    float Intensity,
    float Range,
    float InnerConeAngleDegrees,
    float OuterConeAngleDegrees,
    bool Enabled);

public sealed record SceneEnvironmentInspection(
    Vector3 SkyColor,
    Vector3 HorizonColor,
    Vector3 GroundColor,
    Vector3 AmbientColor,
    float SkyIntensity,
    float AmbientIntensity,
    float Exposure,
    bool Enabled,
    SceneAssetReferenceInspection EnvironmentTexture);

public sealed record SceneAssetReferenceInspection(
    Guid Guid,
    string PackageId,
    string ExpectedAssetType,
    string ActualAssetType,
    string SourcePath,
    bool IsResolved,
    string Diagnostic)
{
    public bool HasValue => Guid != Guid.Empty;
}

public sealed class SceneAuthoringEntityMap
{
    private readonly EntityManager m_EntityManager;
    private readonly Guid[] m_AuthoringGuids;
    private readonly Entity[] m_RuntimeEntities;

    internal SceneAuthoringEntityMap(
        EntityManager entityManager,
        Guid[] authoringGuids,
        Entity[] runtimeEntities)
    {
        m_EntityManager = entityManager;
        m_AuthoringGuids = authoringGuids;
        m_RuntimeEntities = runtimeEntities;
    }

    public int Count => m_AuthoringGuids.Length;

    internal Entity[] RuntimeEntities => m_RuntimeEntities;

    public bool TryGetEntity(Guid authoringGuid, out Entity entity)
    {
        int index = Array.BinarySearch(m_AuthoringGuids, authoringGuid);
        if (index >= 0 && m_EntityManager.IsAlive(m_RuntimeEntities[index]))
        {
            entity = m_RuntimeEntities[index];
            return true;
        }

        entity = Entity.Null;
        return false;
    }
}

public static class SceneAssetLoader
{
    private const string SceneAssetType = "Scene";
    private const string MeshAssetType = "Mesh";
    private const string MaterialAssetType = "Material";
    private const string EnvironmentTextureAssetType = "EnvironmentTexture";

    public static SceneLoadResult LoadScene(
        IAssetDatabase assetDatabase,
        AssetRef<SceneSourceAsset> sceneRef,
        EntityManager entityManager)
    {
        if (assetDatabase == null)
        {
            throw new ArgumentNullException(nameof(assetDatabase));
        }

        if (entityManager == null)
        {
            throw new ArgumentNullException(nameof(entityManager));
        }

        if (!TryLoadSceneStaging(assetDatabase, sceneRef, out var staging, out var diagnostic))
        {
            return Fail(diagnostic);
        }

        return InstantiateStagedScene(staging, entityManager, "source");
    }

    public static SceneLoadResult LoadScene(
        IAssetDatabase assetDatabase,
        SceneSourceSnapshot snapshot,
        EntityManager entityManager)
    {
        if (assetDatabase == null)
        {
            throw new ArgumentNullException(nameof(assetDatabase));
        }

        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        if (entityManager == null)
        {
            throw new ArgumentNullException(nameof(entityManager));
        }

        if (!TryLoadSceneStaging(assetDatabase, snapshot, out var staging, out var diagnostic))
        {
            return Fail(diagnostic);
        }

        return InstantiateStagedScene(staging, entityManager, "source");
    }

    internal static bool TryLoadSceneStaging(
        IAssetDatabase assetDatabase,
        AssetRef<SceneSourceAsset> sceneRef,
        out SceneStagingData staging,
        out string diagnostic)
    {
        staging = null!;
        if (!sceneRef.IsValid)
        {
            diagnostic = "[SceneAssetLoader] Scene asset ref is empty.";
            return false;
        }

        if (!assetDatabase.TryGetAsset(sceneRef, out var sceneAsset))
        {
            diagnostic =
                $"[SceneAssetLoader] Scene asset '{sceneRef.Guid:D}' is not indexed as '{SceneAssetType}'.";
            return false;
        }

        if (!File.Exists(sceneAsset.SourcePath))
        {
            diagnostic =
                $"[SceneAssetLoader] Scene asset '{sceneRef.Guid:D}' source file is missing: {sceneAsset.SourcePath}";
            return false;
        }

        try
        {
            return TryLoadSceneSourceStaging(
                assetDatabase,
                sceneRef.Guid,
                sceneAsset.SourcePath,
                File.ReadAllText(sceneAsset.SourcePath),
                out staging,
                out diagnostic);
        }
        catch (Exception ex)
        {
            diagnostic =
                $"[SceneAssetLoader] Failed to read scene '{sceneAsset.SourcePath}': {ex.Message}";
            return false;
        }
    }

    internal static bool TryLoadSceneStaging(
        IAssetDatabase assetDatabase,
        SceneSourceSnapshot snapshot,
        out SceneStagingData staging,
        out string diagnostic)
    {
        staging = null!;
        if (!snapshot.IsValid)
        {
            diagnostic = "[SceneAssetLoader] Scene source snapshot is incomplete.";
            return false;
        }

        if (!assetDatabase.TryGetAsset(snapshot.Scene, out var sceneAsset))
        {
            diagnostic =
                $"[SceneAssetLoader] Scene asset '{snapshot.Scene.Guid:D}' is not indexed as '{SceneAssetType}'.";
            return false;
        }

        if (!IsSamePath(sceneAsset.SourcePath, snapshot.SourcePath))
        {
            diagnostic =
                $"[SceneAssetLoader] Scene snapshot source '{snapshot.SourcePath}' does not match indexed source '{sceneAsset.SourcePath}'.";
            return false;
        }

        return TryLoadSceneSourceStaging(
            assetDatabase,
            snapshot.Scene.Guid,
            sceneAsset.SourcePath,
            snapshot.SourceText,
            out staging,
            out diagnostic);
    }

    private static bool TryLoadSceneSourceStaging(
        IAssetDatabase assetDatabase,
        Guid sceneGuid,
        string sourcePath,
        string sourceText,
        out SceneStagingData staging,
        out string diagnostic)
    {
        using var _ = Profiler.Zone("SceneAssetLoader.LoadSceneSource");
        return TryBuildSceneStaging(
            assetDatabase,
            sceneGuid,
            sourcePath,
            sourceText,
            out staging,
            out diagnostic);
    }

    internal static bool TryBuildSceneStaging(
        IAssetDatabase assetDatabase,
        Guid sceneGuid,
        string sourcePath,
        string sourceText,
        out SceneStagingData staging,
        out string diagnostic)
    {
        staging = null!;
        if (sceneGuid == Guid.Empty)
        {
            diagnostic = "[SceneAssetLoader] Scene source has no stable asset GUID.";
            return false;
        }

        if (!TryReadSceneDocument(sourcePath, sourceText, out var document, out diagnostic))
        {
            return false;
        }

        if (document == null || document.Entities == null || document.Entities.Count == 0)
        {
            diagnostic = $"[SceneAssetLoader] Scene '{sourcePath}' has no entities.";
            return false;
        }

        if (document.Version != SceneComponentSchemas.CurrentSceneVersion)
        {
            diagnostic =
                $"[SceneAssetLoader] Scene '{sourcePath}' schema version '{document.Version}' is not supported.";
            return false;
        }

        var componentSchemas = new SceneComponentSchemaInfo[document.ComponentSchemas.Count];
        for (int i = 0; i < document.ComponentSchemas.Count; i++)
        {
            var schema = document.ComponentSchemas[i];
            componentSchemas[i] = new SceneComponentSchemaInfo(
                schema.TypeId,
                schema.Name,
                schema.Version,
                schema.Required);
        }
        Array.Sort(componentSchemas, static (left, right) => left.TypeId.CompareTo(right.TypeId));

        var entities = new SceneStagingEntity[document.Entities.Count];
        for (int i = 0; i < document.Entities.Count; i++)
        {
            var sourceEntity = document.Entities[i];
            string entityName = string.IsNullOrWhiteSpace(sourceEntity.Name)
                ? $"Entity[{i}]"
                : sourceEntity.Name.Trim();

            var meshRenderer = sourceEntity.MeshRenderer;
            if (meshRenderer != null)
            {
                var validation = ValidateMeshRenderer(assetDatabase, meshRenderer, sourcePath, entityName);
                if (!validation.Success)
                {
                    diagnostic = validation.Diagnostic;
                    return false;
                }
            }

            Guid parentGuid = sourceEntity.Parent?.EntityGuid ?? Guid.Empty;
            Guid parentSceneGuid = sourceEntity.Parent?.SceneGuid ?? Guid.Empty;
            if (sourceEntity.Parent != null && parentGuid == Guid.Empty)
            {
                diagnostic =
                    $"[SceneAssetLoader] Scene '{sourcePath}' entity '{sourceEntity.Guid:D}' has a Parent without EntityGuid.";
                return false;
            }

            if (parentSceneGuid != Guid.Empty && parentSceneGuid != sceneGuid)
            {
                diagnostic =
                    $"[SceneAssetLoader] Scene '{sourcePath}' entity '{sourceEntity.Guid:D}' references parent '{parentGuid:D}' in external scene '{parentSceneGuid:D}'. Cross-scene entity references require the future world-reference policy.";
                return false;
            }

            if (!TryBuildExtensionComponents(
                    assetDatabase,
                    sceneGuid,
                    sourcePath,
                    sourceEntity,
                    out SceneStagedExtensionComponent[] extensionComponents,
                    out diagnostic))
            {
                return false;
            }

            var stagedEntity = new SceneStagingEntity(
                sourceEntity.Guid,
                parentGuid,
                string.IsNullOrWhiteSpace(sourceEntity.Name) ? string.Empty : sourceEntity.Name.Trim(),
                ToTransform(sourceEntity.Transform),
                sourceEntity.Camera == null ? null : ToCamera(sourceEntity.Camera),
                meshRenderer == null ? null : ToMeshRenderer(meshRenderer),
                NormalizePackageId(meshRenderer?.Mesh?.PackageId),
                NormalizePackageId(meshRenderer?.Material?.PackageId),
                sourceEntity.DirectionalLight == null
                    ? null
                    : ToDirectionalLight(sourceEntity.DirectionalLight),
                sourceEntity.PointLight == null
                    ? null
                    : ToPointLight(sourceEntity.PointLight),
                sourceEntity.SpotLight == null
                    ? null
                    : ToSpotLight(sourceEntity.SpotLight),
                sourceEntity.Environment == null
                    ? null
                    : ToSceneEnvironment(sourceEntity.Environment),
                NormalizePackageId(sourceEntity.Environment?.EnvironmentTexture?.PackageId),
                extensionComponents);
            if (!SceneStagingValidation.TryValidate(
                    stagedEntity,
                    i,
                    sourcePath,
                    out diagnostic))
            {
                return false;
            }

            entities[i] = stagedEntity;
        }

        Array.Sort(
            entities,
            static (left, right) => left.AuthoringGuid.CompareTo(right.AuthoringGuid));

        string sceneName = string.IsNullOrWhiteSpace(document.Name)
            ? Path.GetFileNameWithoutExtension(sourcePath)
            : document.Name.Trim();
        staging = new SceneStagingData(
            sceneGuid,
            document.Version,
            sceneName,
            sourcePath,
            componentSchemas,
            entities);
        if (!TryValidateStagedHierarchy(staging, out diagnostic))
        {
            staging = null!;
            return false;
        }


        if (!SceneStagingValidation.TryCollectExclusiveOwnerships(
                staging,
                out _,
                out diagnostic))
        {
            staging = null!;
            return false;
        }

        diagnostic = string.Empty;
        return true;
    }

    internal static SceneLoadResult InstantiateStagedScene(
        SceneStagingData staging,
        EntityManager entityManager,
        string sourceKind)
    {
        var runtimeEntities = new Entity[staging.Entities.Length];
        var authoringGuids = new Guid[staging.Entities.Length];
        var runtimeLookup = new Dictionary<Guid, Entity>(staging.Entities.Length);
        int createdEntityCount = 0;
        try
        {
            for (int i = 0; i < staging.Entities.Length; i++)
            {
                runtimeEntities[i] = entityManager.CreateEntity();
                createdEntityCount++;
                authoringGuids[i] = staging.Entities[i].AuthoringGuid;
                runtimeLookup.Add(authoringGuids[i], runtimeEntities[i]);
            }

            int cameraCount = 0;
            int meshRendererCount = 0;
            int directionalLightCount = 0;
            int pointLightCount = 0;
            int spotLightCount = 0;
            int environmentCount = 0;
            for (int i = 0; i < staging.Entities.Length; i++)
            {
                ref readonly SceneStagingEntity stagedEntity = ref staging.Entities[i];
                Entity entity = runtimeEntities[i];
                if (!string.IsNullOrEmpty(stagedEntity.Name))
                {
                    entityManager.AddComponent(entity, new NameComponent { Name = stagedEntity.Name });
                }

                entityManager.AddComponent(entity, stagedEntity.Transform);
                if (stagedEntity.Camera is { } camera)
                {
                    entityManager.AddComponent(entity, camera);
                    cameraCount++;
                }

                if (stagedEntity.DirectionalLight is { } directionalLight)
                {
                    entityManager.AddComponent(entity, directionalLight);
                    directionalLightCount++;
                }

                if (stagedEntity.PointLight is { } pointLight)
                {
                    entityManager.AddComponent(entity, pointLight);
                    pointLightCount++;
                }

                if (stagedEntity.SpotLight is { } spotLight)
                {
                    entityManager.AddComponent(entity, spotLight);
                    spotLightCount++;
                }

                if (stagedEntity.Environment is { } environment)
                {
                    entityManager.AddComponent(entity, environment);
                    environmentCount++;
                }

                if (stagedEntity.MeshRenderer is { } meshRenderer)
                {
                    entityManager.AddComponent(entity, meshRenderer);
                    meshRendererCount++;
                }

                SceneStagedExtensionComponent[] extensionComponents =
                    stagedEntity.ExtensionComponents ?? Array.Empty<SceneStagedExtensionComponent>();
                for (int componentIndex = 0;
                     componentIndex < extensionComponents.Length;
                     componentIndex++)
                {
                    ref readonly SceneStagedExtensionComponent extension =
                        ref extensionComponents[componentIndex];
                    extension.Codec.AddToEntity(entityManager, entity, extension.Value);
                }
            }

            var firstChildren = new Dictionary<Guid, Entity>();
            var previousChildren = new Dictionary<Guid, Entity>();
            var childCounts = new Dictionary<Guid, int>();
            for (int i = 0; i < staging.Entities.Length; i++)
            {
                ref readonly SceneStagingEntity stagedEntity = ref staging.Entities[i];
                if (stagedEntity.ParentGuid == Guid.Empty)
                {
                    continue;
                }

                Entity child = runtimeEntities[i];
                Entity parent = runtimeLookup[stagedEntity.ParentGuid];
                entityManager.AddComponent(child, new ParentComponent { Parent = parent });

                Entity previous = Entity.Null;
                if (previousChildren.TryGetValue(stagedEntity.ParentGuid, out var previousChild))
                {
                    previous = previousChild;
                    ref var previousSibling = ref entityManager.GetComponent<SiblingComponent>(previousChild);
                    previousSibling.NextSibling = child;
                }
                else
                {
                    firstChildren.Add(stagedEntity.ParentGuid, child);
                }

                entityManager.AddComponent(
                    child,
                    new SiblingComponent
                    {
                        PrevSibling = previous,
                        NextSibling = Entity.Null
                    });
                previousChildren[stagedEntity.ParentGuid] = child;
                childCounts.TryGetValue(stagedEntity.ParentGuid, out int childCount);
                childCounts[stagedEntity.ParentGuid] = childCount + 1;
            }

            foreach (var childList in firstChildren)
            {
                entityManager.AddComponent(
                    runtimeLookup[childList.Key],
                    new ChildComponent
                    {
                        FirstChild = childList.Value,
                        ChildCount = childCounts[childList.Key]
                    });
            }

            var authoringEntityMap = new SceneAuthoringEntityMap(
                entityManager,
                authoringGuids,
                runtimeEntities);

            Profiler.PlotValue("SceneLoad.EntityCount", staging.Entities.Length);
            Profiler.PlotValue("SceneLoad.CameraCount", cameraCount);
            Profiler.PlotValue("SceneLoad.MeshRendererCount", meshRendererCount);
            Profiler.PlotValue("SceneLoad.DirectionalLightCount", directionalLightCount);
            Profiler.PlotValue("SceneLoad.PointLightCount", pointLightCount);
            Profiler.PlotValue("SceneLoad.SpotLightCount", spotLightCount);
            Profiler.PlotValue("SceneLoad.EnvironmentCount", environmentCount);
            return new SceneLoadResult(
                true,
                staging.Entities.Length,
                cameraCount,
                meshRendererCount,
                directionalLightCount,
                pointLightCount,
                spotLightCount,
                environmentCount,
                $"[SceneAssetLoader] Loaded {sourceKind} scene '{staging.DiagnosticPath}' with {staging.Entities.Length} entities, {cameraCount} cameras, {meshRendererCount} mesh renderers, {directionalLightCount} directional lights, {pointLightCount} point lights, {spotLightCount} spot lights, and {environmentCount} environments.",
                staging.SceneName,
                staging.DiagnosticPath,
                authoringEntityMap);
        }
        catch
        {
            for (int i = createdEntityCount - 1; i >= 0; i--)
            {
                entityManager.TryDestroyEntity(runtimeEntities[i]);
            }
            throw;
        }
    }

    internal static bool TryValidateStagedHierarchy(
        SceneStagingData staging,
        out string diagnostic)
    {
        var indices = new Dictionary<Guid, int>(staging.Entities.Length);
        for (int i = 0; i < staging.Entities.Length; i++)
        {
            Guid authoringGuid = staging.Entities[i].AuthoringGuid;
            if (authoringGuid == Guid.Empty)
            {
                diagnostic =
                    $"[SceneAssetLoader] Scene '{staging.DiagnosticPath}' entity {i} has no authoring GUID.";
                return false;
            }

            if (!indices.TryAdd(authoringGuid, i))
            {
                diagnostic =
                    $"[SceneAssetLoader] Scene '{staging.DiagnosticPath}' contains duplicate entity GUID '{authoringGuid:D}'.";
                return false;
            }
        }

        for (int i = 0; i < staging.Entities.Length; i++)
        {
            Guid parentGuid = staging.Entities[i].ParentGuid;
            if (parentGuid == Guid.Empty)
            {
                continue;
            }

            if (parentGuid == staging.Entities[i].AuthoringGuid)
            {
                diagnostic =
                    $"[SceneAssetLoader] Scene '{staging.DiagnosticPath}' entity '{parentGuid:D}' cannot parent itself.";
                return false;
            }

            if (!indices.ContainsKey(parentGuid))
            {
                diagnostic =
                    $"[SceneAssetLoader] Scene '{staging.DiagnosticPath}' entity '{staging.Entities[i].AuthoringGuid:D}' references missing local parent '{parentGuid:D}'.";
                return false;
            }
        }

        var states = new byte[staging.Entities.Length];
        var path = new List<int>();
        for (int start = 0; start < staging.Entities.Length; start++)
        {
            if (states[start] != 0)
            {
                continue;
            }

            path.Clear();
            int current = start;
            while (states[current] == 0)
            {
                states[current] = 1;
                path.Add(current);
                Guid parentGuid = staging.Entities[current].ParentGuid;
                if (parentGuid == Guid.Empty)
                {
                    current = -1;
                    break;
                }

                current = indices[parentGuid];
            }

            if (current >= 0 && states[current] == 1)
            {
                diagnostic =
                    $"[SceneAssetLoader] Scene '{staging.DiagnosticPath}' hierarchy contains a cycle at entity '{staging.Entities[current].AuthoringGuid:D}'.";
                return false;
            }

            for (int i = 0; i < path.Count; i++)
            {
                states[path[i]] = 2;
            }
        }

        diagnostic = string.Empty;
        return true;
    }

    public static SceneInspectionResult InspectScene(
        IAssetDatabase assetDatabase,
        AssetRef<SceneSourceAsset> sceneRef)
    {
        if (assetDatabase == null)
        {
            throw new ArgumentNullException(nameof(assetDatabase));
        }

        if (!sceneRef.IsValid)
        {
            return FailInspection("[SceneAssetLoader] Scene asset ref is empty.");
        }

        if (!assetDatabase.TryGetAsset(sceneRef, out var sceneAsset))
        {
            return FailInspection($"[SceneAssetLoader] Scene asset '{sceneRef.Guid:D}' is not indexed as '{SceneAssetType}'.");
        }

        if (!File.Exists(sceneAsset.SourcePath))
        {
            return FailInspection(
                $"[SceneAssetLoader] Scene asset '{sceneRef.Guid:D}' source file is missing: {sceneAsset.SourcePath}",
                sceneAsset.SourcePath);
        }

        try
        {
            return InspectSceneSource(
                assetDatabase,
                sceneRef.Guid,
                sceneAsset.SourcePath,
                File.ReadAllText(sceneAsset.SourcePath));
        }
        catch (Exception ex)
        {
            return FailInspection(
                $"[SceneAssetLoader] Failed to read scene '{sceneAsset.SourcePath}': {ex.Message}",
                sceneAsset.SourcePath);
        }
    }

    public static SceneInspectionResult InspectScene(
        IAssetDatabase assetDatabase,
        SceneSourceSnapshot snapshot)
    {
        if (assetDatabase == null)
        {
            throw new ArgumentNullException(nameof(assetDatabase));
        }

        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        if (!snapshot.IsValid)
        {
            return FailInspection("[SceneAssetLoader] Scene source snapshot is incomplete.");
        }

        if (!assetDatabase.TryGetAsset(snapshot.Scene, out var sceneAsset))
        {
            return FailInspection(
                $"[SceneAssetLoader] Scene asset '{snapshot.Scene.Guid:D}' is not indexed as '{SceneAssetType}'.",
                snapshot.SourcePath);
        }

        if (!IsSamePath(sceneAsset.SourcePath, snapshot.SourcePath))
        {
            return FailInspection(
                $"[SceneAssetLoader] Scene snapshot source '{snapshot.SourcePath}' does not match indexed source '{sceneAsset.SourcePath}'.",
                snapshot.SourcePath);
        }

        return InspectSceneSource(
            assetDatabase,
            snapshot.Scene.Guid,
            sceneAsset.SourcePath,
            snapshot.SourceText);
    }

    private static SceneInspectionResult InspectSceneSource(
        IAssetDatabase assetDatabase,
        Guid sceneGuid,
        string sourcePath,
        string sourceText)
    {
        if (!TryReadSceneDocument(sourcePath, sourceText, out var document, out var parseDiagnostic))
        {
            return FailInspection(parseDiagnostic, sourcePath);
        }

        if (document?.Entities == null || document.Entities.Count == 0)
        {
            return FailInspection(
                $"[SceneAssetLoader] Scene '{sourcePath}' has no entities.",
                sourcePath,
                document?.Name ?? string.Empty);
        }

        if (document.Version != SceneComponentSchemas.CurrentSceneVersion)
        {
            return FailInspection(
                $"[SceneAssetLoader] Scene '{sourcePath}' schema version '{document.Version}' is not supported.",
                sourcePath,
                document.Name ?? string.Empty);
        }

        var diagnostics = new List<string>();
        var entities = new List<SceneEntityInspection>(document.Entities.Count);
        int cameraCount = 0;
        int meshRendererCount = 0;
        int directionalLightCount = 0;
        int pointLightCount = 0;
        int spotLightCount = 0;
        int environmentCount = 0;
        var localEntityGuids = document.Entities
            .Select(entity => entity.Guid)
            .ToHashSet();

        for (int i = 0; i < document.Entities.Count; i++)
        {
            var sourceEntity = document.Entities[i];
            string entityName = string.IsNullOrWhiteSpace(sourceEntity.Name)
                ? $"Entity[{i}]"
                : sourceEntity.Name.Trim();

            if (sourceEntity.Parent is { } parent)
            {
                if (parent.EntityGuid == Guid.Empty)
                {
                    diagnostics.Add(
                        $"[SceneAssetLoader] Scene '{sourcePath}' entity '{sourceEntity.Guid:D}' has a Parent without EntityGuid.");
                }
                else if (parent.SceneGuid != Guid.Empty && parent.SceneGuid != sceneGuid)
                {
                    diagnostics.Add(
                        $"[SceneAssetLoader] Scene '{sourcePath}' entity '{sourceEntity.Guid:D}' references parent '{parent.EntityGuid:D}' in external scene '{parent.SceneGuid:D}'. Cross-scene entity references require the future world-reference policy.");
                }
                else if (!localEntityGuids.Contains(parent.EntityGuid))
                {
                    diagnostics.Add(
                        $"[SceneAssetLoader] Scene '{sourcePath}' entity '{sourceEntity.Guid:D}' references missing local parent '{parent.EntityGuid:D}'.");
                }
            }

            var camera = sourceEntity.Camera == null
                ? null
                : InspectCamera(sourceEntity.Camera);
            if (camera != null)
            {
                cameraCount++;
            }

            var meshRenderer = sourceEntity.MeshRenderer == null
                ? null
                : InspectMeshRenderer(assetDatabase, sourceEntity.MeshRenderer, sourcePath, entityName, diagnostics);
            if (meshRenderer != null)
            {
                meshRendererCount++;
            }

            var directionalLight = sourceEntity.DirectionalLight == null
                ? null
                : InspectDirectionalLight(sourceEntity.DirectionalLight);
            if (directionalLight != null)
            {
                directionalLightCount++;
            }

            var pointLight = sourceEntity.PointLight == null
                ? null
                : InspectPointLight(sourceEntity.PointLight);
            if (pointLight != null)
            {
                pointLightCount++;
            }

            var spotLight = sourceEntity.SpotLight == null
                ? null
                : InspectSpotLight(sourceEntity.SpotLight);
            if (spotLight != null)
            {
                spotLightCount++;
            }

            var environment = sourceEntity.Environment == null
                ? null
                : InspectEnvironment(
                    assetDatabase,
                    sourceEntity.Environment,
                    sourcePath,
                    entityName,
                    diagnostics);
            if (environment != null)
            {
                environmentCount++;
            }

            entities.Add(new SceneEntityInspection(
                sourceEntity.Guid,
                sourceEntity.Parent?.EntityGuid ?? Guid.Empty,
                sourceEntity.Parent?.SceneGuid ?? Guid.Empty,
                entityName,
                InspectTransform(sourceEntity.Transform),
                camera,
                meshRenderer,
                directionalLight,
                pointLight,
                spotLight,
                environment));
        }

        return new SceneInspectionResult(
            diagnostics.Count == 0,
            document.Name ?? string.Empty,
            sourcePath,
            document.Entities.Count,
            cameraCount,
            meshRendererCount,
            directionalLightCount,
            pointLightCount,
            spotLightCount,
            environmentCount,
            entities,
            diagnostics);
    }

    public static SceneAssetEditResult UpdateEntityTransform(
        string sourcePath,
        Guid entityGuid,
        SceneTransformInspection transform)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return FailEdit("[SceneAssetLoader] Scene source path is empty.");
        }

        if (entityGuid == Guid.Empty)
        {
            return FailEdit("[SceneAssetLoader] Scene entity GUID is empty.");
        }

        if (!File.Exists(sourcePath))
        {
            return FailEdit($"[SceneAssetLoader] Scene source file is missing: {sourcePath}");
        }

        try
        {
            byte[] sourceFile = File.ReadAllBytes(sourcePath);
            bool hasUtf8Bom = sourceFile.AsSpan().StartsWith(Encoding.UTF8.Preamble);
            var sourceBytes = hasUtf8Bom
                ? sourceFile.AsSpan(Encoding.UTF8.Preamble.Length)
                : sourceFile.AsSpan();
            string sourceText = Encoding.UTF8.GetString(sourceBytes);
            var result = UpdateEntityTransformSource(sourcePath, sourceText, entityGuid, transform);
            if (!result.Success)
            {
                return result;
            }

            WriteUtf8Atomically(sourcePath, result.UpdatedSource, hasUtf8Bom);
            return result;
        }
        catch (Exception ex)
        {
            return FailEdit($"[SceneAssetLoader] Failed to update scene '{sourcePath}': {ex.Message}");
        }
    }

    public static SceneAssetEditResult UpdateEntityTransformSource(
        string sourcePath,
        string sourceText,
        Guid entityGuid,
        SceneTransformInspection transform)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return FailEdit("[SceneAssetLoader] Scene source path is empty.");
        }

        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return FailEdit($"[SceneAssetLoader] Scene '{sourcePath}' source text is empty.");
        }

        if (entityGuid == Guid.Empty)
        {
            return FailEdit("[SceneAssetLoader] Scene entity GUID is empty.");
        }

        try
        {
            var stream = new YamlStream();
            using (var reader = new StringReader(sourceText))
            {
                stream.Load(reader);
            }

            if (stream.Documents.Count == 0 ||
                stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                return FailEdit($"[SceneAssetLoader] Scene '{sourcePath}' root must be a YAML mapping.");
            }

            if (!SceneComponentSchemas.TryPrepareDocument(root, sourcePath, out var schemaDiagnostic))
            {
                return FailEdit(schemaDiagnostic);
            }

            if (!TryGetSequence(root, "Entities", out var entities))
            {
                return FailEdit($"[SceneAssetLoader] Scene '{sourcePath}' has no Entities sequence.");
            }

            YamlMappingNode? entity = null;
            for (int i = 0; i < entities.Children.Count; i++)
            {
                if (entities.Children[i] is YamlMappingNode candidate &&
                    TryReadGuid(candidate, "Guid", out var candidateGuid) &&
                    candidateGuid == entityGuid)
                {
                    entity = candidate;
                    break;
                }
            }

            if (entity == null)
            {
                return FailEdit(
                    $"[SceneAssetLoader] Scene '{sourcePath}' does not contain entity GUID '{entityGuid:D}'.");
            }

            var transformNode = GetOrCreateMapping(entity, "Transform");
            SetChild(transformNode, "Position", CreateVector3Node(transform.Position));
            SetChild(transformNode, "Rotation", CreateQuaternionNode(transform.Rotation));
            SetChild(transformNode, "Scale", CreateVector3Node(transform.Scale));

            var updated = new StringBuilder(sourceText.Length + 128);
            using (var writer = new StringWriter(updated, CultureInfo.InvariantCulture)
            {
                NewLine = DetectNewline(sourceText)
            })
            {
                stream.Save(writer, assignAnchors: false);
            }

            return new SceneAssetEditResult(
                true,
                $"[SceneAssetLoader] Updated transform for scene '{sourcePath}' entity '{entityGuid:D}'.",
                updated.ToString());
        }
        catch (Exception ex)
        {
            return FailEdit($"[SceneAssetLoader] Failed to edit scene '{sourcePath}': {ex.Message}");
        }
    }

    public static SceneAssetEditResult MigrateLegacySceneSource(
        Guid sceneGuid,
        string sourcePath,
        string sourceText)
    {
        if (sceneGuid == Guid.Empty)
        {
            return FailEdit("[SceneAssetLoader] Legacy scene migration requires a stable scene GUID.");
        }

        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(sourceText))
        {
            return FailEdit("[SceneAssetLoader] Legacy scene migration requires a source path and source text.");
        }

        try
        {
            var stream = new YamlStream();
            using (var reader = new StringReader(sourceText))
            {
                stream.Load(reader);
            }

            if (stream.Documents.Count == 0 ||
                stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                return FailEdit($"[SceneAssetLoader] Scene '{sourcePath}' root must be a YAML mapping.");
            }

            int legacyVersion = 1;
            if (TryGetChild(root, "Version", out var versionNode) &&
                (versionNode is not YamlScalarNode versionScalar ||
                 !int.TryParse(
                     versionScalar.Value,
                     NumberStyles.Integer,
                     CultureInfo.InvariantCulture,
                     out legacyVersion)))
            {
                return FailEdit($"[SceneAssetLoader] Scene '{sourcePath}' has an invalid Version field.");
            }

            if (legacyVersion != 1)
            {
                return FailEdit(
                    $"[SceneAssetLoader] Scene '{sourcePath}' migration accepts legacy schema version 1, found '{legacyVersion}'.");
            }

            if (!TryGetSequence(root, "Entities", out var entities) || entities.Children.Count == 0)
            {
                return FailEdit($"[SceneAssetLoader] Scene '{sourcePath}' has no entities to migrate.");
            }

            var usedComponentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Transform"
            };
            var assignedGuids = new HashSet<Guid>();
            for (int i = 0; i < entities.Children.Count; i++)
            {
                if (entities.Children[i] is not YamlMappingNode entity)
                {
                    return FailEdit(
                        $"[SceneAssetLoader] Scene '{sourcePath}' legacy entity {i} must be a mapping.");
                }

                Guid entityGuid;
                if (TryReadGuid(entity, "Guid", out var existingGuid) && existingGuid != Guid.Empty)
                {
                    entityGuid = existingGuid;
                }
                else
                {
                    entityGuid = SceneAuthoringIdentity.CreateEntityGuid();
                    SetChild(entity, "Guid", entityGuid.ToString("D"));
                }

                if (!assignedGuids.Add(entityGuid))
                {
                    return FailEdit(
                        $"[SceneAssetLoader] Scene '{sourcePath}' legacy entities contain duplicate GUID '{entityGuid:D}'.");
                }

                foreach (var field in entity.Children)
                {
                    if (field.Key is not YamlScalarNode key || string.IsNullOrWhiteSpace(key.Value) ||
                        string.Equals(key.Value, "Guid", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(key.Value, "Name", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(key.Value, "Parent", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!SceneComponentSchemas.TryGetByName(key.Value, out var codec))
                    {
                        return FailEdit(
                            $"[SceneAssetLoader] Scene '{sourcePath}' legacy entity {i} contains unsupported component '{key.Value}'.");
                    }

                    usedComponentNames.Add(codec.Info.Name);
                }
            }

            SetChild(root, "Version", SceneComponentSchemas.CurrentSceneVersion.ToString(CultureInfo.InvariantCulture));
            SetChild(root, "ComponentSchemas", SceneComponentSchemas.CreateCurrentDeclarations(usedComponentNames));

            var updated = new StringBuilder(sourceText.Length + (entities.Children.Count * 64) + 512);
            using (var writer = new StringWriter(updated, CultureInfo.InvariantCulture)
            {
                NewLine = DetectNewline(sourceText)
            })
            {
                stream.Save(writer, assignAnchors: false);
            }

            return new SceneAssetEditResult(
                true,
                $"[SceneAssetLoader] Migrated legacy scene '{sourcePath}' to schema version {SceneComponentSchemas.CurrentSceneVersion} with {assignedGuids.Count} persistent entity GUIDs.",
                updated.ToString());
        }
        catch (Exception ex)
        {
            return FailEdit($"[SceneAssetLoader] Failed to migrate legacy scene '{sourcePath}': {ex.Message}");
        }
    }

    public static SceneAssetEditResult MigrateLegacySceneFile(Guid sceneGuid, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return FailEdit($"[SceneAssetLoader] Legacy scene source is missing: {sourcePath}");
        }

        try
        {
            byte[] sourceFile = File.ReadAllBytes(sourcePath);
            bool hasUtf8Bom = sourceFile.AsSpan().StartsWith(Encoding.UTF8.Preamble);
            ReadOnlySpan<byte> sourceBytes = hasUtf8Bom
                ? sourceFile.AsSpan(Encoding.UTF8.Preamble.Length)
                : sourceFile.AsSpan();
            string sourceText = new UTF8Encoding(false, true).GetString(sourceBytes);
            var migration = MigrateLegacySceneSource(sceneGuid, sourcePath, sourceText);
            if (!migration.Success)
            {
                return migration;
            }

            WriteUtf8Atomically(sourcePath, migration.UpdatedSource, hasUtf8Bom);
            return migration;
        }
        catch (Exception ex)
        {
            return FailEdit($"[SceneAssetLoader] Failed to migrate legacy scene '{sourcePath}': {ex.Message}");
        }
    }

    private static SceneLoadResult ValidateMeshRenderer(
        IAssetDatabase assetDatabase,
        SceneMeshRendererSource meshRenderer,
        string scenePath,
        string entityName)
    {
        if (meshRenderer.Mesh == null || meshRenderer.Mesh.Guid == Guid.Empty)
        {
            return Fail($"[SceneAssetLoader] Scene '{scenePath}' entity '{entityName}' has a MeshRenderer without a Mesh.Guid.");
        }

        var meshRef = new AssetRef<MeshSourceAsset>(meshRenderer.Mesh.Guid, MeshAssetType, meshRenderer.Mesh.PackageId ?? string.Empty);
        if (!assetDatabase.TryGetAsset(meshRef, out var meshAsset))
        {
            return Fail($"[SceneAssetLoader] Scene '{scenePath}' entity '{entityName}' references missing mesh '{meshRenderer.Mesh.Guid:D}'.");
        }

        if (!string.Equals(meshAsset.AssetType, MeshAssetType, StringComparison.OrdinalIgnoreCase))
        {
            return Fail($"[SceneAssetLoader] Scene '{scenePath}' entity '{entityName}' references asset '{meshRenderer.Mesh.Guid:D}' with type '{meshAsset.AssetType}', expected '{MeshAssetType}'.");
        }

        if (meshRenderer.Material == null || meshRenderer.Material.Guid == Guid.Empty)
        {
            return new SceneLoadResult(true, 0, 0, 0, 0, 0, 0, 0, string.Empty);
        }

        var materialRef = new AssetRef<MaterialSourceAsset>(meshRenderer.Material.Guid, MaterialAssetType, meshRenderer.Material.PackageId ?? string.Empty);
        if (!assetDatabase.TryGetAsset(materialRef, out var materialAsset))
        {
            return Fail($"[SceneAssetLoader] Scene '{scenePath}' entity '{entityName}' references missing material '{meshRenderer.Material.Guid:D}'.");
        }

        if (!string.Equals(materialAsset.AssetType, MaterialAssetType, StringComparison.OrdinalIgnoreCase))
        {
            return Fail($"[SceneAssetLoader] Scene '{scenePath}' entity '{entityName}' references asset '{meshRenderer.Material.Guid:D}' with type '{materialAsset.AssetType}', expected '{MaterialAssetType}'.");
        }

        return new SceneLoadResult(true, 0, 0, 0, 0, 0, 0, 0, string.Empty);
    }

    private static bool TryBuildExtensionComponents(
        IAssetDatabase assetDatabase,
        Guid sceneGuid,
        string sourcePath,
        SceneEntitySource sourceEntity,
        out SceneStagedExtensionComponent[] components,
        out string diagnostic)
    {
        if (sourceEntity.ExtensionComponents.Count == 0)
        {
            components = Array.Empty<SceneStagedExtensionComponent>();
            diagnostic = string.Empty;
            return true;
        }

        var staged = new List<SceneStagedExtensionComponent>(
            sourceEntity.ExtensionComponents.Count);
        var context = new SceneComponentReadContext(
            assetDatabase,
            sceneGuid,
            sourceEntity.Guid,
            sourcePath);
        foreach ((uint typeId, YamlMappingNode source) in
                 sourceEntity.ExtensionComponents.OrderBy(pair => pair.Key))
        {
            if (!SceneComponentSchemas.TryGetByTypeId(typeId, out var codec) ||
                codec.Extension == null)
            {
                components = Array.Empty<SceneStagedExtensionComponent>();
                diagnostic =
                    $"[SceneAssetLoader] Scene '{sourcePath}' entity " +
                    $"'{sourceEntity.Guid:D}' extension TypeId '{typeId}' is not registered.";
                return false;
            }

            try
            {
                if (!codec.Extension.TryReadSource(
                        context,
                        source,
                        out object component,
                        out diagnostic))
                {
                    components = Array.Empty<SceneStagedExtensionComponent>();
                    return false;
                }

                if (component == null)
                {
                    components = Array.Empty<SceneStagedExtensionComponent>();
                    diagnostic =
                        $"[SceneAssetLoader] Scene '{sourcePath}' entity " +
                        $"'{sourceEntity.Guid:D}' extension '{codec.Info.Name}' returned null staging data.";
                    return false;
                }

                staged.Add(new SceneStagedExtensionComponent(codec.Extension, component));
            }
            catch (Exception ex)
            {
                components = Array.Empty<SceneStagedExtensionComponent>();
                diagnostic =
                    $"[SceneAssetLoader] Scene '{sourcePath}' entity " +
                    $"'{sourceEntity.Guid:D}' extension '{codec.Info.Name}' failed: {ex.Message}";
                return false;
            }
        }

        components = staged.ToArray();
        diagnostic = string.Empty;
        return true;
    }

    private static bool TryReadSceneDocument(
        string sourcePath,
        string sourceText,
        out SceneSourceDocument? document,
        out string diagnostic)
    {
        try
        {
            var stream = new YamlStream();
            using (var reader = new StringReader(sourceText))
            {
                stream.Load(reader);
            }

            if (stream.Documents.Count == 0 ||
                stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                document = null;
                diagnostic = $"[SceneAssetLoader] Scene '{sourcePath}' root must be a YAML mapping.";
                return false;
            }

            if (!SceneComponentSchemas.TryPrepareDocument(root, sourcePath, out diagnostic))
            {
                document = null;
                return false;
            }

            var preparedSource = new StringBuilder(sourceText.Length + 128);
            using (var writer = new StringWriter(preparedSource, CultureInfo.InvariantCulture)
            {
                NewLine = DetectNewline(sourceText)
            })
            {
                stream.Save(writer, assignAnchors: false);
            }

            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(PascalCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            document = deserializer.Deserialize<SceneSourceDocument>(preparedSource.ToString());
            if (document == null)
            {
                diagnostic = $"[SceneAssetLoader] Scene '{sourcePath}' deserialized to an empty document.";
                return false;
            }

            if (!SceneComponentSchemas.TryGetChild(root, "Entities", out var entitiesNode) ||
                entitiesNode is not YamlSequenceNode sourceEntities ||
                sourceEntities.Children.Count != document.Entities.Count)
            {
                diagnostic =
                    $"[SceneAssetLoader] Scene '{sourcePath}' prepared entity sequence does not match its document.";
                document = null;
                return false;
            }

            for (int entityIndex = 0; entityIndex < document.Entities.Count; entityIndex++)
            {
                var entity = document.Entities[entityIndex];
                if (sourceEntities.Children[entityIndex] is not YamlMappingNode sourceEntityNode)
                {
                    diagnostic =
                        $"[SceneAssetLoader] Scene '{sourcePath}' entity {entityIndex} must be a mapping.";
                    document = null;
                    return false;
                }

                for (int schemaIndex = 0; schemaIndex < document.ComponentSchemas.Count; schemaIndex++)
                {
                    if (SceneComponentSchemas.TryGetByTypeId(
                            document.ComponentSchemas[schemaIndex].TypeId,
                            out var codec))
                    {
                        if (codec.Extension != null)
                        {
                            if (SceneComponentSchemas.TryGetChild(
                                    sourceEntityNode,
                                    codec.Info.Name,
                                    out var extensionNode))
                            {
                                if (extensionNode is not YamlMappingNode extensionMapping)
                                {
                                    diagnostic =
                                        $"[SceneAssetLoader] Scene '{sourcePath}' entity " +
                                        $"'{entity.Guid:D}' component '{codec.Info.Name}' must be a mapping.";
                                    document = null;
                                    return false;
                                }

                                entity.ExtensionComponents.Add(codec.Info.TypeId, extensionMapping);
                            }
                            continue;
                        }

                        object? component = codec.Read(entity);
                        codec.Write(entity, component);
                    }
                }
            }

            diagnostic = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            document = null;
            diagnostic = $"[SceneAssetLoader] Failed to parse scene '{sourcePath}': {ex.Message}";
            return false;
        }
    }

    private static bool IsSamePath(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizePackageId(string? packageId)
    {
        return string.IsNullOrWhiteSpace(packageId)
            ? string.Empty
            : packageId.Trim().ToLowerInvariant();
    }

    private static string DetectNewline(string sourceText)
    {
        int lineFeed = sourceText.IndexOf('\n');
        return lineFeed > 0 && sourceText[lineFeed - 1] == '\r'
            ? "\r\n"
            : "\n";
    }

    private static void WriteUtf8Atomically(string path, string sourceText, bool includeBom)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Scene source path has no parent directory.");
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        byte[] sourceBytes = Encoding.UTF8.GetBytes(sourceText);
        byte[] output;
        if (includeBom)
        {
            byte[] preamble = Encoding.UTF8.GetPreamble();
            output = new byte[preamble.Length + sourceBytes.Length];
            preamble.CopyTo(output, 0);
            sourceBytes.CopyTo(output, preamble.Length);
        }
        else
        {
            output = sourceBytes;
        }

        try
        {
            File.WriteAllBytes(temporaryPath, output);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static SceneTransformInspection InspectTransform(SceneTransformSource? source)
    {
        return new SceneTransformInspection(
            ToVector3(source?.Position, Vector3.Zero),
            ToQuaternion(source?.Rotation, Quaternion.Identity),
            ToVector3(source?.Scale, Vector3.One));
    }

    private static SceneCameraInspection InspectCamera(SceneCameraSource source)
    {
        return new SceneCameraInspection(
            source.VerticalFov > 0.0f ? source.VerticalFov : CameraComponent.Default.VerticalFov,
            source.NearPlane > 0.0f ? source.NearPlane : CameraComponent.Default.NearPlane,
            source.FarPlane > 0.0f ? source.FarPlane : CameraComponent.Default.FarPlane,
            source.IsPerspective);
    }

    private static SceneMeshRendererInspection InspectMeshRenderer(
        IAssetDatabase assetDatabase,
        SceneMeshRendererSource source,
        string scenePath,
        string entityName,
        List<string> diagnostics)
    {
        var mesh = InspectRequiredAssetRef<MeshSourceAsset>(
            assetDatabase,
            source.Mesh,
            MeshAssetType,
            scenePath,
            entityName,
            "MeshRenderer",
            "Mesh");
        if (!mesh.IsResolved)
        {
            diagnostics.Add(mesh.Diagnostic);
        }

        var material = InspectOptionalAssetRef<MaterialSourceAsset>(
            assetDatabase,
            source.Material,
            MaterialAssetType,
            scenePath,
            entityName,
            "MeshRenderer",
            "Material");
        if (material.HasValue && !material.IsResolved)
        {
            diagnostics.Add(material.Diagnostic);
        }

        return new SceneMeshRendererInspection(
            mesh,
            material,
            source.FirstSubmeshIndex,
            source.SubmeshCount == 0 ? -1 : source.SubmeshCount,
            ToVector3(source.BoundsCenter, Vector3.Zero),
            ToVector3(source.BoundsExtents, Vector3.Zero),
            source.Visible);
    }

    private static SceneDirectionalLightInspection InspectDirectionalLight(SceneDirectionalLightSource source)
    {
        var fallback = DirectionalLightComponent.Default;
        return new SceneDirectionalLightInspection(
            ToVector3(source.Direction, fallback.Direction),
            ToVector3(source.Color, fallback.Color),
            source.Intensity >= 0.0f ? source.Intensity : fallback.Intensity,
            source.AmbientIntensity >= 0.0f ? source.AmbientIntensity : fallback.AmbientIntensity,
            source.Enabled);
    }

    private static ScenePointLightInspection InspectPointLight(ScenePointLightSource source)
    {
        var fallback = PointLightComponent.Default;
        return new ScenePointLightInspection(
            ToVector3(source.Color, fallback.Color),
            source.Intensity >= 0.0f ? source.Intensity : fallback.Intensity,
            source.Range > 0.0f ? source.Range : fallback.Range,
            source.Enabled);
    }

    private static SceneSpotLightInspection InspectSpotLight(SceneSpotLightSource source)
    {
        var fallback = SpotLightComponent.Default;
        return new SceneSpotLightInspection(
            ToVector3(source.Color, fallback.Color),
            source.Intensity >= 0.0f ? source.Intensity : fallback.Intensity,
            source.Range > 0.0f ? source.Range : fallback.Range,
            source.InnerConeAngleDegrees >= 0.0f
                ? source.InnerConeAngleDegrees
                : fallback.InnerConeAngleDegrees,
            source.OuterConeAngleDegrees > 0.0f
                ? source.OuterConeAngleDegrees
                : fallback.OuterConeAngleDegrees,
            source.Enabled);
    }

    private static SceneEnvironmentInspection InspectEnvironment(
        IAssetDatabase assetDatabase,
        SceneEnvironmentSource source,
        string scenePath,
        string entityName,
        List<string> diagnostics)
    {
        var fallback = SceneEnvironmentComponent.Default;
        var environmentTexture = InspectOptionalAssetRef<EnvironmentTextureSourceAsset>(
            assetDatabase,
            source.EnvironmentTexture,
            EnvironmentTextureAssetType,
            scenePath,
            entityName,
            "Environment",
            "EnvironmentTexture");
        if (environmentTexture.HasValue && !environmentTexture.IsResolved)
        {
            diagnostics.Add(environmentTexture.Diagnostic);
        }

        return new SceneEnvironmentInspection(
            ToVector3(source.SkyColor, fallback.SkyColor),
            ToVector3(source.HorizonColor, fallback.HorizonColor),
            ToVector3(source.GroundColor, fallback.GroundColor),
            ToVector3(source.AmbientColor, fallback.AmbientColor),
            source.SkyIntensity >= 0.0f ? source.SkyIntensity : fallback.SkyIntensity,
            source.AmbientIntensity >= 0.0f ? source.AmbientIntensity : fallback.AmbientIntensity,
            ResolveExposure(source.Exposure, fallback.Exposure),
            source.Enabled,
            environmentTexture);
    }

    private static SceneAssetReferenceInspection InspectRequiredAssetRef<TAsset>(
        IAssetDatabase assetDatabase,
        SceneAssetReferenceSource? source,
        string expectedAssetType,
        string scenePath,
        string entityName,
        string componentName,
        string fieldName)
    {
        if (source == null || source.Guid == Guid.Empty)
        {
            var diagnostic = $"[SceneAssetLoader] Scene '{scenePath}' entity '{entityName}' has a {componentName} without a {fieldName}.Guid.";
            return new SceneAssetReferenceInspection(
                Guid.Empty,
                string.Empty,
                expectedAssetType,
                string.Empty,
                string.Empty,
                false,
                diagnostic);
        }

        return InspectAssetRef<TAsset>(
            assetDatabase,
            source,
            expectedAssetType,
            scenePath,
            entityName,
            required: true);
    }

    private static SceneAssetReferenceInspection InspectOptionalAssetRef<TAsset>(
        IAssetDatabase assetDatabase,
        SceneAssetReferenceSource? source,
        string expectedAssetType,
        string scenePath,
        string entityName,
        string componentName,
        string fieldName)
    {
        if (source == null || source.Guid == Guid.Empty)
        {
            return new SceneAssetReferenceInspection(
                Guid.Empty,
                string.Empty,
                expectedAssetType,
                string.Empty,
                string.Empty,
                true,
                $"[SceneAssetLoader] Scene '{scenePath}' entity '{entityName}' has no {componentName}.{fieldName} override.");
        }

        return InspectAssetRef<TAsset>(
            assetDatabase,
            source,
            expectedAssetType,
            scenePath,
            entityName,
            required: false);
    }

    private static SceneAssetReferenceInspection InspectAssetRef<TAsset>(
        IAssetDatabase assetDatabase,
        SceneAssetReferenceSource source,
        string expectedAssetType,
        string scenePath,
        string entityName,
        bool required)
    {
        var assetRef = new AssetRef<TAsset>(
            source.Guid,
            expectedAssetType,
            source.PackageId ?? string.Empty);
        if (!assetDatabase.TryGetAsset(assetRef, out var asset))
        {
            var diagnostic = required
                ? $"[SceneAssetLoader] Scene '{scenePath}' entity '{entityName}' references missing {expectedAssetType.ToLowerInvariant()} '{source.Guid:D}'."
                : $"[SceneAssetLoader] Scene '{scenePath}' entity '{entityName}' references missing optional {expectedAssetType.ToLowerInvariant()} '{source.Guid:D}'.";
            return new SceneAssetReferenceInspection(
                source.Guid,
                source.PackageId ?? string.Empty,
                expectedAssetType,
                string.Empty,
                string.Empty,
                false,
                diagnostic);
        }

        if (!string.Equals(asset.AssetType, expectedAssetType, StringComparison.OrdinalIgnoreCase))
        {
            var diagnostic = $"[SceneAssetLoader] Scene '{scenePath}' entity '{entityName}' references asset '{source.Guid:D}' with type '{asset.AssetType}', expected '{expectedAssetType}'.";
            return new SceneAssetReferenceInspection(
                source.Guid,
                source.PackageId ?? string.Empty,
                expectedAssetType,
                asset.AssetType,
                asset.SourcePath,
                false,
                diagnostic);
        }

        return new SceneAssetReferenceInspection(
            source.Guid,
            source.PackageId ?? string.Empty,
            expectedAssetType,
            asset.AssetType,
            asset.SourcePath,
            true,
            string.Empty);
    }

    private static TransformComponent ToTransform(SceneTransformSource? source)
    {
        if (source == null)
        {
            return TransformComponent.Identity;
        }

        return new TransformComponent
        {
            Position = ToVector3(source.Position, Vector3.Zero),
            Rotation = ToQuaternion(source.Rotation, Quaternion.Identity),
            Scale = ToVector3(source.Scale, Vector3.One)
        };
    }

    private static CameraComponent ToCamera(SceneCameraSource source)
    {
        return new CameraComponent
        {
            VerticalFov = source.VerticalFov > 0.0f ? source.VerticalFov : CameraComponent.Default.VerticalFov,
            NearPlane = source.NearPlane > 0.0f ? source.NearPlane : CameraComponent.Default.NearPlane,
            FarPlane = source.FarPlane > 0.0f ? source.FarPlane : CameraComponent.Default.FarPlane,
            IsPerspective = source.IsPerspective ? (byte)1 : (byte)0
        };
    }

    private static MeshRendererComponent ToMeshRenderer(SceneMeshRendererSource source)
    {
        var component = MeshRendererComponent.Create(
            source.Mesh?.Guid ?? Guid.Empty,
            source.Material?.Guid ?? Guid.Empty);
        component.FirstSubmeshIndex = source.FirstSubmeshIndex;
        component.SubmeshCount = source.SubmeshCount == 0 ? -1 : source.SubmeshCount;
        component.BoundsCenter = ToVector3(source.BoundsCenter, Vector3.Zero);
        component.BoundsExtents = ToVector3(source.BoundsExtents, Vector3.Zero);
        component.Visible = source.Visible ? (byte)1 : (byte)0;
        return component;
    }

    private static DirectionalLightComponent ToDirectionalLight(SceneDirectionalLightSource source)
    {
        var component = DirectionalLightComponent.Default;
        component.Direction = ToVector3(source.Direction, component.Direction);
        component.Color = ToVector3(source.Color, component.Color);
        component.Intensity = source.Intensity >= 0.0f ? source.Intensity : component.Intensity;
        component.AmbientIntensity = source.AmbientIntensity >= 0.0f ? source.AmbientIntensity : component.AmbientIntensity;
        component.Enabled = source.Enabled ? (byte)1 : (byte)0;
        return component;
    }

    private static PointLightComponent ToPointLight(ScenePointLightSource source)
    {
        var component = PointLightComponent.Default;
        component.Color = ToVector3(source.Color, component.Color);
        component.Intensity = source.Intensity >= 0.0f ? source.Intensity : component.Intensity;
        component.Range = source.Range > 0.0f ? source.Range : component.Range;
        component.Enabled = source.Enabled ? (byte)1 : (byte)0;
        return component;
    }

    private static SpotLightComponent ToSpotLight(SceneSpotLightSource source)
    {
        var component = SpotLightComponent.Default;
        component.Color = ToVector3(source.Color, component.Color);
        component.Intensity = source.Intensity >= 0.0f ? source.Intensity : component.Intensity;
        component.Range = source.Range > 0.0f ? source.Range : component.Range;
        component.InnerConeAngleDegrees = source.InnerConeAngleDegrees >= 0.0f
            ? source.InnerConeAngleDegrees
            : component.InnerConeAngleDegrees;
        component.OuterConeAngleDegrees = source.OuterConeAngleDegrees > 0.0f
            ? source.OuterConeAngleDegrees
            : component.OuterConeAngleDegrees;
        component.Enabled = source.Enabled ? (byte)1 : (byte)0;
        return component;
    }

    private static SceneEnvironmentComponent ToSceneEnvironment(SceneEnvironmentSource source)
    {
        var component = SceneEnvironmentComponent.Default;
        component.EnvironmentTextureGuid = source.EnvironmentTexture?.Guid ?? Guid.Empty;
        component.SkyColor = ToVector3(source.SkyColor, component.SkyColor);
        component.HorizonColor = ToVector3(source.HorizonColor, component.HorizonColor);
        component.GroundColor = ToVector3(source.GroundColor, component.GroundColor);
        component.AmbientColor = ToVector3(source.AmbientColor, component.AmbientColor);
        component.SkyIntensity = source.SkyIntensity >= 0.0f
            ? source.SkyIntensity
            : component.SkyIntensity;
        component.AmbientIntensity = source.AmbientIntensity >= 0.0f
            ? source.AmbientIntensity
            : component.AmbientIntensity;
        component.Exposure = ResolveExposure(source.Exposure, component.Exposure);
        component.Enabled = source.Enabled ? (byte)1 : (byte)0;
        return component;
    }

    private static float ResolveExposure(float exposure, float fallback)
    {
        return float.IsFinite(exposure) && exposure >= 0.0f
            ? SceneEnvironmentComponent.NormalizeExposure(exposure)
            : fallback;
    }

    private static Vector3 ToVector3(SceneVector3Source? source, Vector3 fallback)
    {
        return source == null ? fallback : new Vector3(source.X, source.Y, source.Z);
    }

    private static Quaternion ToQuaternion(SceneQuaternionSource? source, Quaternion fallback)
    {
        return source == null ? fallback : new Quaternion(source.X, source.Y, source.Z, source.W);
    }

    private static SceneLoadResult Fail(string diagnostic)
    {
        return new SceneLoadResult(false, 0, 0, 0, 0, 0, 0, 0, diagnostic);
    }

    private static SceneInspectionResult FailInspection(
        string diagnostic,
        string sourcePath = "",
        string sceneName = "")
    {
        return new SceneInspectionResult(
            false,
            sceneName,
            sourcePath,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            Array.Empty<SceneEntityInspection>(),
            new[] { diagnostic });
    }

    private static SceneAssetEditResult FailEdit(string diagnostic)
    {
        return new SceneAssetEditResult(false, diagnostic);
    }

    private static bool TryGetSequence(
        YamlMappingNode mapping,
        string key,
        out YamlSequenceNode sequence)
    {
        if (TryGetChild(mapping, key, out var node) &&
            node is YamlSequenceNode found)
        {
            sequence = found;
            return true;
        }

        sequence = null!;
        return false;
    }

    private static bool TryGetChild(
        YamlMappingNode mapping,
        string key,
        out YamlNode node)
    {
        foreach (var child in mapping.Children)
        {
            if (child.Key is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                node = child.Value;
                return true;
            }
        }

        node = null!;
        return false;
    }

    private static bool TryReadGuid(
        YamlMappingNode mapping,
        string key,
        out Guid guid)
    {
        if (TryGetChild(mapping, key, out var node) &&
            node is YamlScalarNode scalar &&
            Guid.TryParse(scalar.Value, out guid))
        {
            return true;
        }

        guid = Guid.Empty;
        return false;
    }

    private static YamlMappingNode GetOrCreateMapping(YamlMappingNode mapping, string key)
    {
        if (TryGetChild(mapping, key, out var existing))
        {
            if (existing is YamlMappingNode existingMapping)
            {
                return existingMapping;
            }

            SetChild(mapping, key, new YamlMappingNode());
            return (YamlMappingNode)GetChild(mapping, key);
        }

        var created = new YamlMappingNode();
        mapping.Add(key, created);
        return created;
    }

    private static YamlNode GetChild(YamlMappingNode mapping, string key)
    {
        return TryGetChild(mapping, key, out var node)
            ? node
            : throw new InvalidOperationException($"Missing YAML child '{key}'.");
    }

    private static void SetChild(YamlMappingNode mapping, string key, YamlNode value)
    {
        YamlNode? existingKey = null;
        foreach (var child in mapping.Children)
        {
            if (child.Key is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                existingKey = child.Key;
                break;
            }
        }

        if (existingKey != null)
        {
            mapping.Children[existingKey] = value;
            return;
        }

        mapping.Add(key, value);
    }

    private static YamlMappingNode CreateVector3Node(Vector3 value)
    {
        return new YamlMappingNode
        {
            { "X", FormatFloat(value.X) },
            { "Y", FormatFloat(value.Y) },
            { "Z", FormatFloat(value.Z) }
        };
    }

    private static YamlMappingNode CreateQuaternionNode(Quaternion value)
    {
        return new YamlMappingNode
        {
            { "X", FormatFloat(value.X) },
            { "Y", FormatFloat(value.Y) },
            { "Z", FormatFloat(value.Z) },
            { "W", FormatFloat(value.W) }
        };
    }

    private static string FormatFloat(float value)
    {
        return value.ToString("0.########", CultureInfo.InvariantCulture);
    }

}
