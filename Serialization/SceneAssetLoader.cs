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
    string SourcePath = "");

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

        if (!sceneRef.IsValid)
        {
            return Fail($"[SceneAssetLoader] Scene asset ref is empty.");
        }

        if (!assetDatabase.TryGetAsset(sceneRef, out var sceneAsset))
        {
            return Fail($"[SceneAssetLoader] Scene asset '{sceneRef.Guid:D}' is not indexed as '{SceneAssetType}'.");
        }

        if (!File.Exists(sceneAsset.SourcePath))
        {
            return Fail($"[SceneAssetLoader] Scene asset '{sceneRef.Guid:D}' source file is missing: {sceneAsset.SourcePath}");
        }

        try
        {
            return LoadSceneSource(
                assetDatabase,
                sceneAsset.SourcePath,
                File.ReadAllText(sceneAsset.SourcePath),
                entityManager);
        }
        catch (Exception ex)
        {
            return Fail($"[SceneAssetLoader] Failed to read scene '{sceneAsset.SourcePath}': {ex.Message}");
        }
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

        if (!snapshot.IsValid)
        {
            return Fail("[SceneAssetLoader] Scene source snapshot is incomplete.");
        }

        if (!assetDatabase.TryGetAsset(snapshot.Scene, out var sceneAsset))
        {
            return Fail($"[SceneAssetLoader] Scene asset '{snapshot.Scene.Guid:D}' is not indexed as '{SceneAssetType}'.");
        }

        if (!IsSamePath(sceneAsset.SourcePath, snapshot.SourcePath))
        {
            return Fail(
                $"[SceneAssetLoader] Scene snapshot source '{snapshot.SourcePath}' does not match indexed source '{sceneAsset.SourcePath}'.");
        }

        return LoadSceneSource(
            assetDatabase,
            sceneAsset.SourcePath,
            snapshot.SourceText,
            entityManager);
    }

    private static SceneLoadResult LoadSceneSource(
        IAssetDatabase assetDatabase,
        string sourcePath,
        string sourceText,
        EntityManager entityManager)
    {
        using var _ = Profiler.Zone("SceneAssetLoader.LoadSceneSource");
        if (!TryReadSceneDocument(sourcePath, sourceText, out var document, out var parseDiagnostic))
        {
            return Fail(parseDiagnostic);
        }

        if (document?.Entities == null || document.Entities.Count == 0)
        {
            return Fail($"[SceneAssetLoader] Scene '{sourcePath}' has no entities.");
        }

        int cameraCount = 0;
        int meshRendererCount = 0;
        int directionalLightCount = 0;
        int pointLightCount = 0;
        int spotLightCount = 0;
        int environmentCount = 0;
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
                    return validation;
                }
            }

            var entity = entityManager.CreateEntity();
            if (!string.IsNullOrWhiteSpace(sourceEntity.Name))
            {
                entityManager.AddComponent(entity, new NameComponent { Name = sourceEntity.Name.Trim() });
            }

            entityManager.AddComponent(entity, ToTransform(sourceEntity.Transform));

            if (sourceEntity.Camera != null)
            {
                entityManager.AddComponent(entity, ToCamera(sourceEntity.Camera));
                cameraCount++;
            }

            if (sourceEntity.DirectionalLight != null)
            {
                entityManager.AddComponent(entity, ToDirectionalLight(sourceEntity.DirectionalLight));
                directionalLightCount++;
            }

            if (sourceEntity.PointLight != null)
            {
                entityManager.AddComponent(entity, ToPointLight(sourceEntity.PointLight));
                pointLightCount++;
            }

            if (sourceEntity.SpotLight != null)
            {
                entityManager.AddComponent(entity, ToSpotLight(sourceEntity.SpotLight));
                spotLightCount++;
            }

            if (sourceEntity.Environment != null)
            {
                entityManager.AddComponent(entity, ToSceneEnvironment(sourceEntity.Environment));
                environmentCount++;
            }

            if (meshRenderer != null)
            {
                entityManager.AddComponent(entity, ToMeshRenderer(meshRenderer));
                meshRendererCount++;
            }
        }

        Profiler.PlotValue("SceneLoad.EntityCount", document.Entities.Count);
        Profiler.PlotValue("SceneLoad.CameraCount", cameraCount);
        Profiler.PlotValue("SceneLoad.MeshRendererCount", meshRendererCount);
        Profiler.PlotValue("SceneLoad.DirectionalLightCount", directionalLightCount);
        Profiler.PlotValue("SceneLoad.PointLightCount", pointLightCount);
        Profiler.PlotValue("SceneLoad.SpotLightCount", spotLightCount);
        Profiler.PlotValue("SceneLoad.EnvironmentCount", environmentCount);
        return new SceneLoadResult(
            true,
            document.Entities.Count,
            cameraCount,
            meshRendererCount,
            directionalLightCount,
            pointLightCount,
            spotLightCount,
            environmentCount,
            $"[SceneAssetLoader] Loaded scene '{sourcePath}' with {document.Entities.Count} entities, {cameraCount} cameras, {meshRendererCount} mesh renderers, {directionalLightCount} directional lights, {pointLightCount} point lights, {spotLightCount} spot lights, and {environmentCount} environments.",
            string.IsNullOrWhiteSpace(document.Name)
                ? Path.GetFileNameWithoutExtension(sourcePath)
                : document.Name.Trim(),
            sourcePath);
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

        return InspectSceneSource(assetDatabase, sceneAsset.SourcePath, snapshot.SourceText);
    }

    private static SceneInspectionResult InspectSceneSource(
        IAssetDatabase assetDatabase,
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

        var diagnostics = new List<string>();
        var entities = new List<SceneEntityInspection>(document.Entities.Count);
        int cameraCount = 0;
        int meshRendererCount = 0;
        int directionalLightCount = 0;
        int pointLightCount = 0;
        int spotLightCount = 0;
        int environmentCount = 0;

        for (int i = 0; i < document.Entities.Count; i++)
        {
            var sourceEntity = document.Entities[i];
            string entityName = string.IsNullOrWhiteSpace(sourceEntity.Name)
                ? $"Entity[{i}]"
                : sourceEntity.Name.Trim();

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
        int entityIndex,
        SceneTransformInspection transform)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return FailEdit("[SceneAssetLoader] Scene source path is empty.");
        }

        if (entityIndex < 0)
        {
            return FailEdit($"[SceneAssetLoader] Scene entity index '{entityIndex}' is invalid.");
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
            var result = UpdateEntityTransformSource(sourcePath, sourceText, entityIndex, transform);
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
        int entityIndex,
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

        if (entityIndex < 0)
        {
            return FailEdit($"[SceneAssetLoader] Scene entity index '{entityIndex}' is invalid.");
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

            if (!TryGetSequence(root, "Entities", out var entities))
            {
                return FailEdit($"[SceneAssetLoader] Scene '{sourcePath}' has no Entities sequence.");
            }

            if (entityIndex >= entities.Children.Count)
            {
                return FailEdit(
                    $"[SceneAssetLoader] Scene '{sourcePath}' entity index '{entityIndex}' is outside the entity count '{entities.Children.Count}'.");
            }

            if (entities.Children[entityIndex] is not YamlMappingNode entity)
            {
                return FailEdit($"[SceneAssetLoader] Scene '{sourcePath}' entity index '{entityIndex}' must be a YAML mapping.");
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
                $"[SceneAssetLoader] Updated transform for scene '{sourcePath}' entity index '{entityIndex}'.",
                updated.ToString());
        }
        catch (Exception ex)
        {
            return FailEdit($"[SceneAssetLoader] Failed to edit scene '{sourcePath}': {ex.Message}");
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

    private static bool TryReadSceneDocument(
        string sourcePath,
        string sourceText,
        out SceneSourceDocument? document,
        out string diagnostic)
    {
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(PascalCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            document = deserializer.Deserialize<SceneSourceDocument>(sourceText);
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

    private sealed class SceneSourceDocument
    {
        public string Name { get; set; } = string.Empty;
        public List<SceneEntitySource> Entities { get; set; } = new();
    }

    private sealed class SceneEntitySource
    {
        public string Name { get; set; } = string.Empty;
        public SceneTransformSource? Transform { get; set; }
        public SceneCameraSource? Camera { get; set; }
        public SceneDirectionalLightSource? DirectionalLight { get; set; }
        public ScenePointLightSource? PointLight { get; set; }
        public SceneSpotLightSource? SpotLight { get; set; }
        public SceneEnvironmentSource? Environment { get; set; }
        public SceneMeshRendererSource? MeshRenderer { get; set; }
    }

    private sealed class SceneTransformSource
    {
        public SceneVector3Source? Position { get; set; }
        public SceneQuaternionSource? Rotation { get; set; }
        public SceneVector3Source? Scale { get; set; }
    }

    private sealed class SceneCameraSource
    {
        public float VerticalFov { get; set; } = 60.0f;
        public float NearPlane { get; set; } = 0.1f;
        public float FarPlane { get; set; } = 1000.0f;
        public bool IsPerspective { get; set; } = true;
    }

    private sealed class SceneMeshRendererSource
    {
        public SceneAssetReferenceSource? Mesh { get; set; }
        public SceneAssetReferenceSource? Material { get; set; }
        public int FirstSubmeshIndex { get; set; }
        public int SubmeshCount { get; set; } = -1;
        public SceneVector3Source? BoundsCenter { get; set; }
        public SceneVector3Source? BoundsExtents { get; set; }
        public bool Visible { get; set; } = true;
    }

    private sealed class SceneDirectionalLightSource
    {
        public SceneVector3Source? Direction { get; set; }
        public SceneVector3Source? Color { get; set; }
        public float Intensity { get; set; } = 1.0f;
        public float AmbientIntensity { get; set; } = 0.18f;
        public bool Enabled { get; set; } = true;
    }

    private sealed class ScenePointLightSource
    {
        public SceneVector3Source? Color { get; set; }
        public float Intensity { get; set; } = 1.0f;
        public float Range { get; set; } = 4.0f;
        public bool Enabled { get; set; } = true;
    }

    private sealed class SceneSpotLightSource
    {
        public SceneVector3Source? Color { get; set; }
        public float Intensity { get; set; } = 1.0f;
        public float Range { get; set; } = 4.0f;
        public float InnerConeAngleDegrees { get; set; } = 18.0f;
        public float OuterConeAngleDegrees { get; set; } = 28.0f;
        public bool Enabled { get; set; } = true;
    }

    private sealed class SceneEnvironmentSource
    {
        public SceneAssetReferenceSource? EnvironmentTexture { get; set; }
        public SceneVector3Source? SkyColor { get; set; }
        public SceneVector3Source? HorizonColor { get; set; }
        public SceneVector3Source? GroundColor { get; set; }
        public SceneVector3Source? AmbientColor { get; set; }
        public float SkyIntensity { get; set; } = 0.85f;
        public float AmbientIntensity { get; set; } = 0.32f;
        public float Exposure { get; set; } = SceneEnvironmentComponent.DefaultExposure;
        public bool Enabled { get; set; } = true;
    }

    private sealed class SceneAssetReferenceSource
    {
        public Guid Guid { get; set; }
        public string PackageId { get; set; } = string.Empty;
    }

    private sealed class SceneVector3Source
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }

    private sealed class SceneQuaternionSource
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float W { get; set; } = 1.0f;
    }
}
