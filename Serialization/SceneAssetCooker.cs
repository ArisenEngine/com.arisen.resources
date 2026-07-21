using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.ECS;

namespace ArisenEngine.Resources.Serialization;

public sealed record CookedSceneArtifact(
    Guid SceneGuid,
    string Variant,
    string Path,
    int EntityCount,
    int AssetReferenceCount,
    long SizeInBytes,
    IReadOnlyList<CookedSceneDependency> Dependencies);

public readonly record struct CookedSceneDependency(
    Guid Guid,
    string PackageId,
    string AssetType,
    bool Required);

internal sealed record SceneStagingData(
    Guid SceneGuid,
    int SourceSchemaVersion,
    string SceneName,
    string DiagnosticPath,
    SceneComponentSchemaInfo[] ComponentSchemas,
    SceneStagingEntity[] Entities);

internal readonly record struct SceneStagingEntity(
    Guid AuthoringGuid,
    Guid ParentGuid,
    string Name,
    TransformComponent Transform,
    CameraComponent? Camera,
    MeshRendererComponent? MeshRenderer,
    string MeshPackageId,
    string MaterialPackageId,
    DirectionalLightComponent? DirectionalLight,
    PointLightComponent? PointLight,
    SpotLightComponent? SpotLight,
    SceneEnvironmentComponent? Environment,
    string EnvironmentTexturePackageId);

internal static class SceneStagingValidation
{
    public static bool TryValidate(
        in SceneStagingEntity entity,
        int entityIndex,
        string diagnosticPath,
        out string diagnostic)
    {
        string entityName = string.IsNullOrWhiteSpace(entity.Name)
            ? $"Entity[{entityIndex}]"
            : entity.Name;

        if (entity.AuthoringGuid == Guid.Empty)
        {
            diagnostic = Invalid(diagnosticPath, entityName, "AuthoringGuid is empty");
            return false;
        }

        if (!IsFinite(entity.Transform.Position) ||
            !IsFinite(entity.Transform.Rotation) ||
            !IsFinite(entity.Transform.Scale))
        {
            diagnostic = Invalid(diagnosticPath, entityName, "Transform contains a non-finite value");
            return false;
        }

        if (entity.Camera is { } camera &&
            (!float.IsFinite(camera.VerticalFov) || camera.VerticalFov <= 0.0f ||
             !float.IsFinite(camera.NearPlane) || camera.NearPlane <= 0.0f ||
             !float.IsFinite(camera.FarPlane) || camera.FarPlane <= 0.0f ||
             camera.IsPerspective > 1))
        {
            diagnostic = Invalid(diagnosticPath, entityName, "Camera contains invalid projection data");
            return false;
        }

        if (entity.MeshRenderer is { } mesh &&
            (mesh.MeshGuid == Guid.Empty ||
             mesh.FirstSubmeshIndex < 0 ||
             (mesh.SubmeshCount != -1 && mesh.SubmeshCount <= 0) ||
             !IsFinite(mesh.BoundsCenter) ||
             !IsFinite(mesh.BoundsExtents) ||
             mesh.Visible > 1))
        {
            diagnostic = Invalid(diagnosticPath, entityName, "MeshRenderer contains invalid range, bounds, visibility, or mesh identity data");
            return false;
        }

        if (entity.DirectionalLight is { } directional &&
            (!IsFinite(directional.Direction) ||
             !IsFinite(directional.Color) ||
             !IsNonNegativeFinite(directional.Intensity) ||
             !IsNonNegativeFinite(directional.AmbientIntensity) ||
             directional.Enabled > 1))
        {
            diagnostic = Invalid(diagnosticPath, entityName, "DirectionalLight contains invalid data");
            return false;
        }

        if (entity.PointLight is { } point &&
            (!IsFinite(point.Color) ||
             !IsNonNegativeFinite(point.Intensity) ||
             !IsPositiveFinite(point.Range) ||
             point.Enabled > 1))
        {
            diagnostic = Invalid(diagnosticPath, entityName, "PointLight contains invalid data");
            return false;
        }

        if (entity.SpotLight is { } spot &&
            (!IsFinite(spot.Color) ||
             !IsNonNegativeFinite(spot.Intensity) ||
             !IsPositiveFinite(spot.Range) ||
             !IsNonNegativeFinite(spot.InnerConeAngleDegrees) ||
             !IsPositiveFinite(spot.OuterConeAngleDegrees) ||
             spot.Enabled > 1))
        {
            diagnostic = Invalid(diagnosticPath, entityName, "SpotLight contains invalid data");
            return false;
        }

        if (entity.Environment is { } environment &&
            (!IsFinite(environment.SkyColor) ||
             !IsFinite(environment.HorizonColor) ||
             !IsFinite(environment.GroundColor) ||
             !IsFinite(environment.AmbientColor) ||
             !IsNonNegativeFinite(environment.SkyIntensity) ||
             !IsNonNegativeFinite(environment.AmbientIntensity) ||
             !float.IsFinite(environment.Exposure) ||
             environment.Exposure < SceneEnvironmentComponent.MinimumExposure ||
             environment.Exposure > SceneEnvironmentComponent.MaximumExposure ||
             environment.Enabled > 1))
        {
            diagnostic = Invalid(diagnosticPath, entityName, "Environment contains invalid data");
            return false;
        }

        diagnostic = string.Empty;
        return true;
    }

    private static bool IsFinite(System.Numerics.Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    private static bool IsFinite(System.Numerics.Quaternion value)
    {
        return float.IsFinite(value.X) &&
               float.IsFinite(value.Y) &&
               float.IsFinite(value.Z) &&
               float.IsFinite(value.W);
    }

    private static bool IsNonNegativeFinite(float value)
    {
        return float.IsFinite(value) && value >= 0.0f;
    }

    private static bool IsPositiveFinite(float value)
    {
        return float.IsFinite(value) && value > 0.0f;
    }

    private static string Invalid(string path, string entityName, string reason)
    {
        return $"[SceneStaging] Scene '{path}' entity '{entityName}' is invalid: {reason}.";
    }
}

[Flags]
internal enum CookedSceneSectionFlags : uint
{
    None = 0,
    Required = 1
}

internal enum CookedSceneSectionType : uint
{
    Metadata = 1,
    Strings = 2,
    Entities = 3,
    Hierarchy = 4,
    AssetReferences = 5,
    Transforms = 6,
    Cameras = 7,
    MeshRenderers = 8,
    DirectionalLights = 9,
    PointLights = 10,
    SpotLights = 11,
    Environments = 12,
    ComponentSchemas = 13
}

[Flags]
internal enum CookedSceneComponentSchemaFlags : uint
{
    None = 0,
    Required = 1
}

[Flags]
internal enum CookedSceneComponentMask : uint
{
    None = 0,
    Name = 1 << 0,
    Transform = 1 << 1,
    Camera = 1 << 2,
    MeshRenderer = 1 << 3,
    DirectionalLight = 1 << 4,
    PointLight = 1 << 5,
    SpotLight = 1 << 6,
    Environment = 1 << 7
}

internal enum CookedSceneAssetReferenceKind : uint
{
    Mesh = 1,
    Material = 2,
    EnvironmentTexture = 3
}

internal readonly record struct CookedSceneSectionDescriptor(
    CookedSceneSectionType Type,
    CookedSceneSectionFlags Flags,
    ulong Offset,
    ulong Size,
    uint Count,
    uint Stride);

internal readonly record struct SceneAssetReferenceKey(
    CookedSceneAssetReferenceKind Kind,
    Guid Guid,
    string PackageId,
    bool Required);

internal readonly record struct CookedSceneSectionPayload(
    CookedSceneSectionType Type,
    CookedSceneSectionFlags Flags,
    uint Count,
    uint Stride,
    byte[] Bytes);

internal readonly record struct CookedSceneSchemaReadResult(
    SceneComponentSchemaInfo[] Schemas,
    CookedSceneComponentMask DeclaredMask,
    CookedSceneComponentMask IgnoredMask)
{
    public bool IsSupported(uint typeId)
    {
        for (int i = 0; i < Schemas.Length; i++)
        {
            if (Schemas[i].TypeId == typeId)
            {
                return true;
            }
        }

        return false;
    }
}

public static class SceneAssetCooker
{
    public const string RuntimeVariant = "runtime.scene.v1";
    public const string CookedExtension = ".ariscene";
    public const int CookedFormatVersion = 2;

    internal const int HeaderSize = 96;
    internal const int SectionDirectoryEntrySize = 32;
    internal const int HashOffset = 48;
    internal const int HashSize = 32;

    private const int CurrentContainerVersion = 2;
    private const int CurrentSourceSchemaVersion = SceneComponentSchemas.CurrentSceneVersion;
    private const string SceneAssetType = "Scene";
    private const uint EndianMarker = 0x01020304;
    private const uint NoIndex = uint.MaxValue;
    private const uint SupportedSectionFlags = (uint)CookedSceneSectionFlags.Required;
    private const int MaxSectionCount = 64;
    private const int MaxEntityCount = 1_000_000;
    private const int MaxAssetReferenceCount = 3_000_000;
    private const int MaxStringCount = 4_000_000;
    private const int MaxStringByteLength = 1_048_576;
    private const int MaxCookedSceneBytes = 512 * 1024 * 1024;

    private const uint MetadataStride = 8;
    private const uint EntityStride = 32;
    private const uint HierarchyStride = 8;
    private const uint ComponentSchemaStride = 16;
    private const uint AssetReferenceStride = 28;
    private const uint TransformStride = 44;
    private const uint CameraStride = 20;
    private const uint MeshRendererStride = 48;
    private const uint DirectionalLightStride = 40;
    private const uint PointLightStride = 28;
    private const uint SpotLightStride = 36;
    private const uint EnvironmentStride = 72;

    private static readonly byte[] s_Magic = Encoding.ASCII.GetBytes("ARISCENE");
    private static readonly UTF8Encoding s_StrictUtf8 = new(false, true);

    public static CookedSceneArtifact Cook(
        IAssetDatabase assetDatabase,
        AssetRef<SceneSourceAsset> sceneRef)
    {
        if (assetDatabase == null)
        {
            throw new ArgumentNullException(nameof(assetDatabase));
        }

        if (!sceneRef.IsValid)
        {
            throw new ArgumentException("Scene cooking requires a valid scene asset reference.", nameof(sceneRef));
        }

        if (!assetDatabase.TryGetAsset(sceneRef, out var sourceAsset))
        {
            throw new InvalidOperationException(
                $"[SceneAssetCooker] Scene asset '{sceneRef.Guid:D}' is not indexed as 'Scene'.");
        }

        if (!string.Equals(sourceAsset.AssetType, SceneAssetType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[SceneAssetCooker] Asset '{sceneRef.Guid:D}' has type '{sourceAsset.AssetType}', expected '{SceneAssetType}'.");
        }

        if (!File.Exists(sourceAsset.SourcePath))
        {
            throw new FileNotFoundException(
                $"[SceneAssetCooker] Scene source '{sourceAsset.SourcePath}' is missing.",
                sourceAsset.SourcePath);
        }

        using var _ = Profiler.Zone("SceneAssetCooker.Cook");
        string sourceText = File.ReadAllText(sourceAsset.SourcePath);
        if (!SceneAssetLoader.TryBuildSceneStaging(
                assetDatabase,
                sceneRef.Guid,
                sourceAsset.SourcePath,
                sourceText,
                out var staging,
                out var diagnostic))
        {
            throw new InvalidOperationException(diagnostic);
        }

        List<SceneAssetReferenceKey> stagedReferences = BuildAssetReferences(staging);
        for (int i = 0; i < stagedReferences.Count; i++)
        {
            try
            {
                ValidateAssetReference(assetDatabase, stagedReferences[i], i);
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidOperationException(
                    $"[SceneAssetCooker] Scene '{sourceAsset.SourcePath}' dependency validation failed: {ex.Message}",
                    ex);
            }
        }

        byte[] bytes = WritePayload(staging);
        string outputPath = assetDatabase.GetCookedArtifactPath(
            sceneRef.Guid,
            RuntimeVariant,
            CookedExtension);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, bytes);

        var output = new FileInfo(outputPath);
        assetDatabase.RegisterCookedArtifact(new CookedAssetRecord(
            sceneRef.Guid,
            sourceAsset.AssetType,
            RuntimeVariant,
            output.FullName,
            output.Length,
            output.LastWriteTimeUtc));

        int assetReferenceCount = stagedReferences.Count;
        Logger.Info(
            $"[SceneAssetCooker] Cooked scene {sceneRef.Guid:D} | Entities: {staging.Entities.Length} | Dependencies: {assetReferenceCount} | Bytes: {output.Length} | Output: {output.FullName}");
        return new CookedSceneArtifact(
            sceneRef.Guid,
            RuntimeVariant,
            output.FullName,
            staging.Entities.Length,
            assetReferenceCount,
            output.Length,
            GetDependencies(staging));
    }

    public static SceneLoadResult LoadCooked(
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

        if (!TryLoadCookedStaging(assetDatabase, sceneRef, out var staging, out var diagnostic))
        {
            return Failure(diagnostic);
        }

        return SceneAssetLoader.InstantiateStagedScene(staging, entityManager, "cooked");
    }

    internal static bool TryLoadCookedStaging(
        IAssetDatabase assetDatabase,
        AssetRef<SceneSourceAsset> sceneRef,
        out SceneStagingData staging,
        out string diagnostic)
    {
        staging = null!;
        if (!sceneRef.IsValid)
        {
            diagnostic = "[SceneAssetCooker] Cooked scene asset ref is empty.";
            return false;
        }

        if (!assetDatabase.TryGetAssetDescriptor(sceneRef.Guid, out AssetDescriptor sceneAsset))
        {
            diagnostic =
                $"[SceneAssetCooker] Cooked scene '{sceneRef.Guid:D}' has no cataloged asset identity.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(sceneRef.PackageId) &&
            !string.Equals(
                sceneAsset.PackageId,
                sceneRef.PackageId,
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostic =
                $"[SceneAssetCooker] Cooked scene '{sceneRef.Guid:D}' belongs to package " +
                $"'{sceneAsset.PackageId}', expected '{sceneRef.PackageId}'.";
            return false;
        }

        if (!assetDatabase.TryLoadCookedAsset(
                sceneRef.Guid,
                RuntimeVariant,
                SceneAssetType,
                out var handle))
        {
            diagnostic =
                $"[SceneAssetCooker] Cooked scene '{sceneRef.Guid:D}' variant '{RuntimeVariant}' is unavailable.";
            return false;
        }

        string artifactPath = assetDatabase.TryGetCookedArtifact(sceneRef.Guid, RuntimeVariant, out var artifact)
            ? artifact.Path
            : $"{sceneRef.Guid:D}:{RuntimeVariant}";

        try
        {
            using var _ = Profiler.Zone("SceneAssetCooker.LoadCooked");
            ReadOnlyMemory<byte> bytes = assetDatabase.GetCookedAssetBytes(handle);
            return TryReadPayload(
                assetDatabase,
                sceneRef.Guid,
                bytes.Span,
                artifactPath,
                out staging,
                out diagnostic);
        }
        finally
        {
            assetDatabase.Release(handle);
        }
    }

    internal static byte[] WritePayload(SceneStagingData staging)
    {
        if (staging.SceneGuid == Guid.Empty)
        {
            throw new InvalidOperationException("[SceneAssetCooker] Staged scene has no source GUID.");
        }

        if (staging.SourceSchemaVersion != CurrentSourceSchemaVersion)
        {
            throw new InvalidOperationException(
                $"[SceneAssetCooker] Source scene schema version '{staging.SourceSchemaVersion}' is not supported.");
        }

        if (staging.Entities.Length == 0 || staging.Entities.Length > MaxEntityCount)
        {
            throw new InvalidOperationException(
                $"[SceneAssetCooker] Staged scene entity count '{staging.Entities.Length}' is invalid.");
        }

        ValidateComponentSchemas(staging.ComponentSchemas);

        for (int i = 0; i < staging.Entities.Length; i++)
        {
            if (!SceneStagingValidation.TryValidate(
                    staging.Entities[i],
                    i,
                    staging.DiagnosticPath,
                    out var diagnostic))
            {
                throw new InvalidOperationException(diagnostic);
            }

            if (i > 0 &&
                staging.Entities[i - 1].AuthoringGuid.CompareTo(staging.Entities[i].AuthoringGuid) >= 0)
            {
                throw new InvalidOperationException(
                    "[SceneAssetCooker] Staged scene entities are not in canonical authoring-GUID order.");
            }
        }

        if (!SceneAssetLoader.TryValidateStagedHierarchy(staging, out var hierarchyDiagnostic))
        {
            throw new InvalidOperationException(hierarchyDiagnostic);
        }

        List<SceneAssetReferenceKey> assetReferences = BuildAssetReferences(staging);
        var assetReferenceIndices = new Dictionary<SceneAssetReferenceKey, uint>(assetReferences.Count);
        for (int i = 0; i < assetReferences.Count; i++)
        {
            assetReferenceIndices.Add(assetReferences[i], checked((uint)i));
        }

        List<string> strings = BuildStringTable(staging, assetReferences);
        var stringIndices = new Dictionary<string, uint>(strings.Count, StringComparer.Ordinal);
        for (int i = 0; i < strings.Count; i++)
        {
            stringIndices.Add(strings[i], checked((uint)i));
        }

        var sections = new[]
        {
            new CookedSceneSectionPayload(
                CookedSceneSectionType.Metadata,
                CookedSceneSectionFlags.Required,
                1,
                MetadataStride,
                BuildMetadataSection(staging, stringIndices)),
            new CookedSceneSectionPayload(
                CookedSceneSectionType.Strings,
                CookedSceneSectionFlags.Required,
                checked((uint)strings.Count),
                0,
                BuildStringsSection(strings)),
            new CookedSceneSectionPayload(
                CookedSceneSectionType.Entities,
                CookedSceneSectionFlags.Required,
                checked((uint)staging.Entities.Length),
                EntityStride,
                BuildEntitiesSection(staging, stringIndices)),
            new CookedSceneSectionPayload(
                CookedSceneSectionType.Hierarchy,
                CookedSceneSectionFlags.Required,
                checked((uint)staging.Entities.Count(entity => entity.ParentGuid != Guid.Empty)),
                HierarchyStride,
                BuildHierarchySection(staging)),
            new CookedSceneSectionPayload(
                CookedSceneSectionType.AssetReferences,
                CookedSceneSectionFlags.Required,
                checked((uint)assetReferences.Count),
                AssetReferenceStride,
                BuildAssetReferencesSection(assetReferences, stringIndices)),
            new CookedSceneSectionPayload(
                CookedSceneSectionType.Transforms,
                CookedSceneSectionFlags.Required,
                checked((uint)staging.Entities.Length),
                TransformStride,
                BuildTransformsSection(staging)),
            BuildCamerasSection(staging),
            BuildMeshRenderersSection(staging, assetReferenceIndices),
            BuildDirectionalLightsSection(staging),
            BuildPointLightsSection(staging),
            BuildSpotLightsSection(staging),
            BuildEnvironmentsSection(staging, assetReferenceIndices),
            new CookedSceneSectionPayload(
                CookedSceneSectionType.ComponentSchemas,
                CookedSceneSectionFlags.Required,
                checked((uint)staging.ComponentSchemas.Length),
                ComponentSchemaStride,
                BuildComponentSchemasSection(staging.ComponentSchemas))
        };

        int directorySize = checked(sections.Length * SectionDirectoryEntrySize);
        int nextOffset = Align8(checked(HeaderSize + directorySize));
        var descriptors = new CookedSceneSectionDescriptor[sections.Length];
        for (int i = 0; i < sections.Length; i++)
        {
            byte[] sectionBytes = sections[i].Bytes;
            descriptors[i] = new CookedSceneSectionDescriptor(
                sections[i].Type,
                sections[i].Flags,
                checked((ulong)nextOffset),
                checked((ulong)sectionBytes.Length),
                sections[i].Count,
                sections[i].Stride);
            nextOffset = Align8(checked(nextOffset + sectionBytes.Length));
        }

        if (nextOffset > MaxCookedSceneBytes)
        {
            throw new InvalidOperationException(
                $"[SceneAssetCooker] Cooked scene size '{nextOffset}' exceeds the {MaxCookedSceneBytes}-byte limit.");
        }

        byte[] output = new byte[nextOffset];
        Span<byte> outputSpan = output;
        s_Magic.CopyTo(outputSpan);
        BinaryPrimitives.WriteInt32LittleEndian(outputSpan.Slice(8, 4), CurrentContainerVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(outputSpan.Slice(12, 4), EndianMarker);
        WriteGuid(outputSpan.Slice(16, 16), staging.SceneGuid);
        BinaryPrimitives.WriteInt32LittleEndian(outputSpan.Slice(32, 4), staging.SourceSchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(outputSpan.Slice(36, 4), sections.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(outputSpan.Slice(40, 8), checked((ulong)output.Length));

        for (int i = 0; i < descriptors.Length; i++)
        {
            int directoryOffset = HeaderSize + (i * SectionDirectoryEntrySize);
            WriteDescriptor(outputSpan.Slice(directoryOffset, SectionDirectoryEntrySize), descriptors[i]);
            sections[i].Bytes.CopyTo(outputSpan.Slice(
                checked((int)descriptors[i].Offset),
                sections[i].Bytes.Length));
        }

        byte[] hash = SHA256.HashData(outputSpan.Slice(HeaderSize));
        hash.CopyTo(outputSpan.Slice(HashOffset, HashSize));
        return output;
    }

    internal static bool TryReadPayload(
        IAssetDatabase assetDatabase,
        Guid expectedSceneGuid,
        ReadOnlySpan<byte> bytes,
        string diagnosticPath,
        out SceneStagingData staging,
        out string diagnostic)
    {
        try
        {
            staging = ReadPayload(assetDatabase, expectedSceneGuid, bytes, diagnosticPath);
            diagnostic = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentException)
        {
            staging = null!;
            diagnostic = $"[SceneAssetCooker] Cooked scene '{diagnosticPath}' is invalid: {ex.Message}";
            return false;
        }
    }

    private static SceneStagingData ReadPayload(
        IAssetDatabase assetDatabase,
        Guid expectedSceneGuid,
        ReadOnlySpan<byte> bytes,
        string diagnosticPath)
    {
        if (bytes.Length < HeaderSize)
        {
            throw Invalid("header is truncated");
        }

        if (bytes.Length > MaxCookedSceneBytes)
        {
            throw Invalid($"file exceeds the {MaxCookedSceneBytes}-byte limit");
        }

        if (!bytes.Slice(0, s_Magic.Length).SequenceEqual(s_Magic))
        {
            throw Invalid("header magic is invalid");
        }

        int containerVersion = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(8, 4));
        if (containerVersion != CurrentContainerVersion)
        {
            throw Invalid($"container version '{containerVersion}' is not supported");
        }

        uint endianMarker = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(12, 4));
        if (endianMarker != EndianMarker)
        {
            throw Invalid($"byte-order marker '0x{endianMarker:X8}' is invalid");
        }

        Guid sceneGuid = ReadGuid(bytes.Slice(16, 16));
        if (sceneGuid == Guid.Empty || sceneGuid != expectedSceneGuid)
        {
            throw Invalid(
                $"source scene GUID '{sceneGuid:D}' does not match requested GUID '{expectedSceneGuid:D}'");
        }

        int sourceSchemaVersion = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(32, 4));
        if (sourceSchemaVersion != CurrentSourceSchemaVersion)
        {
            throw Invalid($"source schema version '{sourceSchemaVersion}' is not supported");
        }

        int sectionCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(36, 4));
        if (sectionCount <= 0 || sectionCount > MaxSectionCount)
        {
            throw Invalid($"section count '{sectionCount}' is invalid");
        }

        ulong declaredFileSize = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(40, 8));
        if (declaredFileSize != checked((ulong)bytes.Length))
        {
            throw Invalid(
                $"declared file size '{declaredFileSize}' does not match actual size '{bytes.Length}'");
        }

        EnsureZeroPadding(bytes.Slice(80, 16), "reserved header bytes");

        byte[] computedHash = SHA256.HashData(bytes.Slice(HeaderSize));
        if (!CryptographicOperations.FixedTimeEquals(
                bytes.Slice(HashOffset, HashSize),
                computedHash))
        {
            throw Invalid("content hash does not match the section directory and payload");
        }

        int directoryEnd = checked(HeaderSize + (sectionCount * SectionDirectoryEntrySize));
        if (directoryEnd > bytes.Length)
        {
            throw Invalid("section directory is truncated");
        }

        var knownSections = new Dictionary<CookedSceneSectionType, CookedSceneSectionDescriptor>();
        var seenSectionTypes = new HashSet<uint>();
        var occupiedRanges = new List<(ulong Start, ulong End, uint Type)>(sectionCount);
        for (int i = 0; i < sectionCount; i++)
        {
            int descriptorOffset = HeaderSize + (i * SectionDirectoryEntrySize);
            ReadOnlySpan<byte> descriptorBytes = bytes.Slice(descriptorOffset, SectionDirectoryEntrySize);
            uint rawType = BinaryPrimitives.ReadUInt32LittleEndian(descriptorBytes.Slice(0, 4));
            uint rawFlags = BinaryPrimitives.ReadUInt32LittleEndian(descriptorBytes.Slice(4, 4));
            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(descriptorBytes.Slice(8, 8));
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(descriptorBytes.Slice(16, 8));
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(descriptorBytes.Slice(24, 4));
            uint stride = BinaryPrimitives.ReadUInt32LittleEndian(descriptorBytes.Slice(28, 4));

            if (!seenSectionTypes.Add(rawType))
            {
                throw Invalid($"section type '{rawType}' is duplicated");
            }

            if ((rawFlags & ~SupportedSectionFlags) != 0)
            {
                throw Invalid($"section type '{rawType}' uses unsupported flags '0x{rawFlags:X8}'");
            }

            if ((offset & 7) != 0 || offset < checked((ulong)directoryEnd))
            {
                throw Invalid($"section type '{rawType}' has an invalid or unaligned offset '{offset}'");
            }

            ulong end = checked(offset + size);
            if (end > checked((ulong)bytes.Length))
            {
                throw Invalid($"section type '{rawType}' extends beyond the file");
            }

            if (stride != 0 && size != checked((ulong)count * stride))
            {
                throw Invalid($"section type '{rawType}' size does not match count times stride");
            }

            if (count > MaxStringCount)
            {
                throw Invalid($"section type '{rawType}' count '{count}' exceeds the safety limit");
            }

            bool isKnown = Enum.IsDefined(typeof(CookedSceneSectionType), rawType);
            if (!isKnown)
            {
                if ((rawFlags & (uint)CookedSceneSectionFlags.Required) != 0)
                {
                    throw Invalid($"unknown required section type '{rawType}' cannot be skipped");
                }
            }
            else
            {
                var type = (CookedSceneSectionType)rawType;
                var descriptor = new CookedSceneSectionDescriptor(
                    type,
                    (CookedSceneSectionFlags)rawFlags,
                    offset,
                    size,
                    count,
                    stride);
                if (!knownSections.TryAdd(type, descriptor))
                {
                    throw Invalid($"section '{type}' is duplicated");
                }
            }

            if (size > 0)
            {
                occupiedRanges.Add((offset, end, rawType));
            }
        }

        occupiedRanges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        for (int i = 1; i < occupiedRanges.Count; i++)
        {
            if (occupiedRanges[i].Start < occupiedRanges[i - 1].End)
            {
                throw Invalid(
                    $"sections '{occupiedRanges[i - 1].Type}' and '{occupiedRanges[i].Type}' overlap");
            }
        }

        ulong paddingCursor = checked((ulong)directoryEnd);
        foreach ((ulong start, ulong end, _) in occupiedRanges)
        {
            EnsureZeroPadding(
                bytes.Slice(
                    checked((int)paddingCursor),
                    checked((int)(start - paddingCursor))),
                "section alignment padding");
            paddingCursor = end;
        }

        EnsureZeroPadding(
            bytes.Slice(
                checked((int)paddingCursor),
                checked(bytes.Length - (int)paddingCursor)),
            "trailing alignment padding");

        CookedSceneSectionDescriptor metadataSection = RequireSection(
            knownSections,
            CookedSceneSectionType.Metadata,
            MetadataStride,
            expectedCount: 1);
        CookedSceneSectionDescriptor stringsSection = RequireVariableSection(
            knownSections,
            CookedSceneSectionType.Strings,
            MaxStringCount);
        CookedSceneSectionDescriptor entitiesSection = RequireSection(
            knownSections,
            CookedSceneSectionType.Entities,
            EntityStride,
            minimumCount: 1,
            maximumCount: MaxEntityCount);
        CookedSceneSectionDescriptor hierarchySection = RequireSection(
            knownSections,
            CookedSceneSectionType.Hierarchy,
            HierarchyStride,
            minimumCount: 0,
            maximumCount: checked((int)entitiesSection.Count));
        CookedSceneSectionDescriptor componentSchemasSection = RequireSection(
            knownSections,
            CookedSceneSectionType.ComponentSchemas,
            ComponentSchemaStride,
            minimumCount: 1,
            maximumCount: MaxSectionCount);
        CookedSceneSectionDescriptor assetReferencesSection = RequireSection(
            knownSections,
            CookedSceneSectionType.AssetReferences,
            AssetReferenceStride,
            minimumCount: 0,
            maximumCount: MaxAssetReferenceCount);
        CookedSceneSectionDescriptor transformsSection = RequireSection(
            knownSections,
            CookedSceneSectionType.Transforms,
            TransformStride,
            expectedCount: entitiesSection.Count);

        string[] strings = ReadStrings(bytes, stringsSection);
        string sceneName = ReadMetadata(bytes, metadataSection, strings, entitiesSection.Count);
        CookedSceneSchemaReadResult componentSchemas = ReadComponentSchemas(
            bytes,
            componentSchemasSection);
        SceneAssetReferenceKey[] assetReferences = ReadAssetReferences(
            assetDatabase,
            bytes,
            assetReferencesSection,
            strings);
        SceneEntityBuilder[] entities = ReadEntities(
            bytes,
            entitiesSection,
            strings,
            componentSchemas);
        ReadHierarchy(bytes, hierarchySection, entities);
        ReadTransforms(bytes, transformsSection, entities);
        ReadCameras(
            bytes,
            ResolveComponentSection(
                knownSections,
                componentSchemas,
                SceneComponentSchemas.CameraTypeId,
                CookedSceneSectionType.Cameras,
                CameraStride),
            entities);
        ReadMeshRenderers(
            bytes,
            ResolveComponentSection(
                knownSections,
                componentSchemas,
                SceneComponentSchemas.MeshRendererTypeId,
                CookedSceneSectionType.MeshRenderers,
                MeshRendererStride),
            entities,
            assetReferences);
        ReadDirectionalLights(
            bytes,
            ResolveComponentSection(
                knownSections,
                componentSchemas,
                SceneComponentSchemas.DirectionalLightTypeId,
                CookedSceneSectionType.DirectionalLights,
                DirectionalLightStride),
            entities);
        ReadPointLights(
            bytes,
            ResolveComponentSection(
                knownSections,
                componentSchemas,
                SceneComponentSchemas.PointLightTypeId,
                CookedSceneSectionType.PointLights,
                PointLightStride),
            entities);
        ReadSpotLights(
            bytes,
            ResolveComponentSection(
                knownSections,
                componentSchemas,
                SceneComponentSchemas.SpotLightTypeId,
                CookedSceneSectionType.SpotLights,
                SpotLightStride),
            entities);
        ReadEnvironments(
            bytes,
            ResolveComponentSection(
                knownSections,
                componentSchemas,
                SceneComponentSchemas.EnvironmentTypeId,
                CookedSceneSectionType.Environments,
                EnvironmentStride),
            entities,
            assetReferences);

        var stagedEntities = new SceneStagingEntity[entities.Length];
        for (int i = 0; i < entities.Length; i++)
        {
            stagedEntities[i] = entities[i].Build(i);
            if (!SceneStagingValidation.TryValidate(
                    stagedEntities[i],
                    i,
                    diagnosticPath,
                    out var validationDiagnostic))
            {
                throw Invalid(validationDiagnostic);
            }
        }

        var staged = new SceneStagingData(
            sceneGuid,
            sourceSchemaVersion,
            sceneName,
            diagnosticPath,
            componentSchemas.Schemas,
            stagedEntities);
        if (!SceneAssetLoader.TryValidateStagedHierarchy(staged, out var hierarchyDiagnostic))
        {
            throw Invalid(hierarchyDiagnostic);
        }

        return staged;
    }

    private static List<SceneAssetReferenceKey> BuildAssetReferences(SceneStagingData staging)
    {
        var unique = new HashSet<SceneAssetReferenceKey>();
        foreach (ref readonly SceneStagingEntity entity in staging.Entities.AsSpan())
        {
            if (entity.MeshRenderer is not { } mesh)
            {
                if (entity.Environment is { EnvironmentTextureGuid: var environmentGuid } &&
                    environmentGuid != Guid.Empty)
                {
                    unique.Add(new SceneAssetReferenceKey(
                        CookedSceneAssetReferenceKind.EnvironmentTexture,
                        environmentGuid,
                        entity.EnvironmentTexturePackageId,
                        Required: false));
                }

                continue;
            }

            unique.Add(new SceneAssetReferenceKey(
                CookedSceneAssetReferenceKind.Mesh,
                mesh.MeshGuid,
                entity.MeshPackageId,
                Required: true));
            if (mesh.MaterialGuid != Guid.Empty)
            {
                unique.Add(new SceneAssetReferenceKey(
                    CookedSceneAssetReferenceKind.Material,
                    mesh.MaterialGuid,
                    entity.MaterialPackageId,
                    Required: false));
            }

            if (entity.Environment is { EnvironmentTextureGuid: var environmentTextureGuid } &&
                environmentTextureGuid != Guid.Empty)
            {
                unique.Add(new SceneAssetReferenceKey(
                    CookedSceneAssetReferenceKind.EnvironmentTexture,
                    environmentTextureGuid,
                    entity.EnvironmentTexturePackageId,
                    Required: false));
            }
        }

        var result = unique.ToList();
        result.Sort(CompareAssetReferences);
        return result;
    }

    internal static CookedSceneDependency[] GetDependencies(SceneStagingData staging)
    {
        return BuildAssetReferences(staging)
            .Select(reference => new CookedSceneDependency(
                reference.Guid,
                reference.PackageId,
                GetAssetType(reference.Kind),
                reference.Required))
            .ToArray();
    }

    private static int CompareAssetReferences(SceneAssetReferenceKey left, SceneAssetReferenceKey right)
    {
        int comparison = left.Kind.CompareTo(right.Kind);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Guid.CompareTo(right.Guid);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = string.Compare(left.PackageId, right.PackageId, StringComparison.Ordinal);
        return comparison != 0 ? comparison : left.Required.CompareTo(right.Required);
    }

    private static List<string> BuildStringTable(
        SceneStagingData staging,
        IReadOnlyList<SceneAssetReferenceKey> assetReferences)
    {
        var strings = new SortedSet<string>(StringComparer.Ordinal);
        AddString(strings, staging.SceneName);
        foreach (ref readonly SceneStagingEntity entity in staging.Entities.AsSpan())
        {
            AddString(strings, entity.Name);
        }

        foreach (SceneAssetReferenceKey assetReference in assetReferences)
        {
            AddString(strings, assetReference.PackageId);
        }

        return strings.ToList();
    }

    private static void AddString(ISet<string> strings, string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            strings.Add(value);
        }
    }

    private static byte[] BuildMetadataSection(
        SceneStagingData staging,
        IReadOnlyDictionary<string, uint> stringIndices)
    {
        using var writer = new ScenePayloadWriter();
        writer.WriteUInt32(GetStringIndex(staging.SceneName, stringIndices));
        writer.WriteUInt32(checked((uint)staging.Entities.Length));
        return writer.ToArray();
    }

    private static byte[] BuildStringsSection(IReadOnlyList<string> strings)
    {
        using var writer = new ScenePayloadWriter();
        foreach (string value in strings)
        {
            byte[] utf8 = s_StrictUtf8.GetBytes(value);
            if (utf8.Length > MaxStringByteLength)
            {
                throw new InvalidOperationException(
                    $"[SceneAssetCooker] String length '{utf8.Length}' exceeds the {MaxStringByteLength}-byte limit.");
            }

            writer.WriteUInt32(checked((uint)utf8.Length));
            writer.WriteBytes(utf8);
        }

        return writer.ToArray();
    }

    private static byte[] BuildEntitiesSection(
        SceneStagingData staging,
        IReadOnlyDictionary<string, uint> stringIndices)
    {
        using var writer = new ScenePayloadWriter();
        for (int i = 0; i < staging.Entities.Length; i++)
        {
            ref readonly SceneStagingEntity entity = ref staging.Entities[i];
            writer.WriteUInt32(checked((uint)i));
            writer.WriteUInt32(GetStringIndex(entity.Name, stringIndices));
            writer.WriteUInt32((uint)GetComponentMask(entity));
            writer.WriteUInt32(0);
            writer.WriteGuid(entity.AuthoringGuid);
        }

        return writer.ToArray();
    }

    private static byte[] BuildHierarchySection(SceneStagingData staging)
    {
        var entityIndices = new Dictionary<Guid, uint>(staging.Entities.Length);
        for (int i = 0; i < staging.Entities.Length; i++)
        {
            entityIndices.Add(staging.Entities[i].AuthoringGuid, checked((uint)i));
        }

        using var writer = new ScenePayloadWriter();
        for (int i = 0; i < staging.Entities.Length; i++)
        {
            Guid parentGuid = staging.Entities[i].ParentGuid;
            if (parentGuid == Guid.Empty)
            {
                continue;
            }

            writer.WriteUInt32(checked((uint)i));
            writer.WriteUInt32(entityIndices[parentGuid]);
        }

        return writer.ToArray();
    }

    private static byte[] BuildComponentSchemasSection(
        IReadOnlyList<SceneComponentSchemaInfo> schemas)
    {
        using var writer = new ScenePayloadWriter();
        for (int i = 0; i < schemas.Count; i++)
        {
            SceneComponentSchemaInfo schema = schemas[i];
            writer.WriteUInt32(schema.TypeId);
            writer.WriteUInt32(checked((uint)schema.Version));
            writer.WriteUInt32(schema.Required
                ? (uint)CookedSceneComponentSchemaFlags.Required
                : (uint)CookedSceneComponentSchemaFlags.None);
            writer.WriteUInt32((uint)GetComponentSectionType(schema.TypeId));
        }

        return writer.ToArray();
    }

    private static byte[] BuildAssetReferencesSection(
        IReadOnlyList<SceneAssetReferenceKey> assetReferences,
        IReadOnlyDictionary<string, uint> stringIndices)
    {
        using var writer = new ScenePayloadWriter();
        foreach (SceneAssetReferenceKey assetReference in assetReferences)
        {
            writer.WriteUInt32((uint)assetReference.Kind);
            writer.WriteUInt32(assetReference.Required ? 1u : 0u);
            writer.WriteGuid(assetReference.Guid);
            writer.WriteUInt32(GetStringIndex(assetReference.PackageId, stringIndices));
        }

        return writer.ToArray();
    }

    private static byte[] BuildTransformsSection(SceneStagingData staging)
    {
        using var writer = new ScenePayloadWriter();
        for (int i = 0; i < staging.Entities.Length; i++)
        {
            TransformComponent transform = staging.Entities[i].Transform;
            writer.WriteUInt32(checked((uint)i));
            writer.WriteVector3(transform.Position);
            writer.WriteQuaternion(transform.Rotation);
            writer.WriteVector3(transform.Scale);
        }

        return writer.ToArray();
    }

    private static CookedSceneSectionPayload BuildCamerasSection(SceneStagingData staging)
    {
        using var writer = new ScenePayloadWriter();
        uint count = 0;
        for (int i = 0; i < staging.Entities.Length; i++)
        {
            if (staging.Entities[i].Camera is not { } camera)
            {
                continue;
            }

            writer.WriteUInt32(checked((uint)i));
            writer.WriteSingle(camera.VerticalFov);
            writer.WriteSingle(camera.NearPlane);
            writer.WriteSingle(camera.FarPlane);
            writer.WriteUInt32(camera.IsPerspective);
            count++;
        }

        return new CookedSceneSectionPayload(
            CookedSceneSectionType.Cameras,
            CookedSceneSectionFlags.None,
            count,
            CameraStride,
            writer.ToArray());
    }

    private static CookedSceneSectionPayload BuildMeshRenderersSection(
        SceneStagingData staging,
        IReadOnlyDictionary<SceneAssetReferenceKey, uint> assetReferenceIndices)
    {
        using var writer = new ScenePayloadWriter();
        uint count = 0;
        for (int i = 0; i < staging.Entities.Length; i++)
        {
            ref readonly SceneStagingEntity entity = ref staging.Entities[i];
            if (entity.MeshRenderer is not { } mesh)
            {
                continue;
            }

            var meshKey = new SceneAssetReferenceKey(
                CookedSceneAssetReferenceKind.Mesh,
                mesh.MeshGuid,
                entity.MeshPackageId,
                Required: true);
            uint materialIndex = NoIndex;
            if (mesh.MaterialGuid != Guid.Empty)
            {
                var materialKey = new SceneAssetReferenceKey(
                    CookedSceneAssetReferenceKind.Material,
                    mesh.MaterialGuid,
                    entity.MaterialPackageId,
                    Required: false);
                materialIndex = assetReferenceIndices[materialKey];
            }

            writer.WriteUInt32(checked((uint)i));
            writer.WriteUInt32(assetReferenceIndices[meshKey]);
            writer.WriteUInt32(materialIndex);
            writer.WriteInt32(mesh.FirstSubmeshIndex);
            writer.WriteInt32(mesh.SubmeshCount);
            writer.WriteVector3(mesh.BoundsCenter);
            writer.WriteVector3(mesh.BoundsExtents);
            writer.WriteUInt32(mesh.Visible);
            count++;
        }

        return new CookedSceneSectionPayload(
            CookedSceneSectionType.MeshRenderers,
            CookedSceneSectionFlags.None,
            count,
            MeshRendererStride,
            writer.ToArray());
    }

    private static CookedSceneSectionPayload BuildDirectionalLightsSection(SceneStagingData staging)
    {
        using var writer = new ScenePayloadWriter();
        uint count = 0;
        for (int i = 0; i < staging.Entities.Length; i++)
        {
            if (staging.Entities[i].DirectionalLight is not { } light)
            {
                continue;
            }

            writer.WriteUInt32(checked((uint)i));
            writer.WriteVector3(light.Direction);
            writer.WriteVector3(light.Color);
            writer.WriteSingle(light.Intensity);
            writer.WriteSingle(light.AmbientIntensity);
            writer.WriteUInt32(light.Enabled);
            count++;
        }

        return new CookedSceneSectionPayload(
            CookedSceneSectionType.DirectionalLights,
            CookedSceneSectionFlags.None,
            count,
            DirectionalLightStride,
            writer.ToArray());
    }

    private static CookedSceneSectionPayload BuildPointLightsSection(SceneStagingData staging)
    {
        using var writer = new ScenePayloadWriter();
        uint count = 0;
        for (int i = 0; i < staging.Entities.Length; i++)
        {
            if (staging.Entities[i].PointLight is not { } light)
            {
                continue;
            }

            writer.WriteUInt32(checked((uint)i));
            writer.WriteVector3(light.Color);
            writer.WriteSingle(light.Intensity);
            writer.WriteSingle(light.Range);
            writer.WriteUInt32(light.Enabled);
            count++;
        }

        return new CookedSceneSectionPayload(
            CookedSceneSectionType.PointLights,
            CookedSceneSectionFlags.None,
            count,
            PointLightStride,
            writer.ToArray());
    }

    private static CookedSceneSectionPayload BuildSpotLightsSection(SceneStagingData staging)
    {
        using var writer = new ScenePayloadWriter();
        uint count = 0;
        for (int i = 0; i < staging.Entities.Length; i++)
        {
            if (staging.Entities[i].SpotLight is not { } light)
            {
                continue;
            }

            writer.WriteUInt32(checked((uint)i));
            writer.WriteVector3(light.Color);
            writer.WriteSingle(light.Intensity);
            writer.WriteSingle(light.Range);
            writer.WriteSingle(light.InnerConeAngleDegrees);
            writer.WriteSingle(light.OuterConeAngleDegrees);
            writer.WriteUInt32(light.Enabled);
            count++;
        }

        return new CookedSceneSectionPayload(
            CookedSceneSectionType.SpotLights,
            CookedSceneSectionFlags.None,
            count,
            SpotLightStride,
            writer.ToArray());
    }

    private static CookedSceneSectionPayload BuildEnvironmentsSection(
        SceneStagingData staging,
        IReadOnlyDictionary<SceneAssetReferenceKey, uint> assetReferenceIndices)
    {
        using var writer = new ScenePayloadWriter();
        uint count = 0;
        for (int i = 0; i < staging.Entities.Length; i++)
        {
            ref readonly SceneStagingEntity entity = ref staging.Entities[i];
            if (entity.Environment is not { } environment)
            {
                continue;
            }

            uint environmentTextureIndex = NoIndex;
            if (environment.EnvironmentTextureGuid != Guid.Empty)
            {
                var key = new SceneAssetReferenceKey(
                    CookedSceneAssetReferenceKind.EnvironmentTexture,
                    environment.EnvironmentTextureGuid,
                    entity.EnvironmentTexturePackageId,
                    Required: false);
                environmentTextureIndex = assetReferenceIndices[key];
            }

            writer.WriteUInt32(checked((uint)i));
            writer.WriteUInt32(environmentTextureIndex);
            writer.WriteVector3(environment.SkyColor);
            writer.WriteVector3(environment.HorizonColor);
            writer.WriteVector3(environment.GroundColor);
            writer.WriteVector3(environment.AmbientColor);
            writer.WriteSingle(environment.SkyIntensity);
            writer.WriteSingle(environment.AmbientIntensity);
            writer.WriteSingle(environment.Exposure);
            writer.WriteUInt32(environment.Enabled);
            count++;
        }

        return new CookedSceneSectionPayload(
            CookedSceneSectionType.Environments,
            CookedSceneSectionFlags.None,
            count,
            EnvironmentStride,
            writer.ToArray());
    }

    private static string[] ReadStrings(
        ReadOnlySpan<byte> fileBytes,
        CookedSceneSectionDescriptor descriptor)
    {
        if (descriptor.Count > (uint)MaxStringCount)
        {
            throw Invalid($"string count '{descriptor.Count}' exceeds the safety limit");
        }

        var strings = new string[checked((int)descriptor.Count)];
        var reader = new ScenePayloadReader(GetSection(fileBytes, descriptor));
        string? previous = null;
        for (int i = 0; i < strings.Length; i++)
        {
            int byteLength = checked((int)reader.ReadUInt32());
            if (byteLength <= 0 || byteLength > MaxStringByteLength)
            {
                throw Invalid($"string byte length '{byteLength}' is invalid");
            }

            string value = s_StrictUtf8.GetString(reader.ReadBytes(byteLength));
            if (string.IsNullOrEmpty(value))
            {
                throw Invalid("string table contains an empty entry");
            }

            if (previous != null && string.Compare(previous, value, StringComparison.Ordinal) >= 0)
            {
                throw Invalid("string table is not strictly canonical and unique");
            }

            strings[i] = value;
            previous = value;
        }

        reader.EnsureEnd("string table");
        return strings;
    }

    private static string ReadMetadata(
        ReadOnlySpan<byte> fileBytes,
        CookedSceneSectionDescriptor descriptor,
        IReadOnlyList<string> strings,
        uint expectedEntityCount)
    {
        var reader = new ScenePayloadReader(GetSection(fileBytes, descriptor));
        uint sceneNameIndex = reader.ReadUInt32();
        uint entityCount = reader.ReadUInt32();
        reader.EnsureEnd("metadata");
        if (entityCount != expectedEntityCount)
        {
            throw Invalid(
                $"metadata entity count '{entityCount}' does not match entity section count '{expectedEntityCount}'");
        }

        return ReadRequiredString(strings, sceneNameIndex, "scene name");
    }

    private static SceneAssetReferenceKey[] ReadAssetReferences(
        IAssetDatabase assetDatabase,
        ReadOnlySpan<byte> fileBytes,
        CookedSceneSectionDescriptor descriptor,
        IReadOnlyList<string> strings)
    {
        if (descriptor.Count > (uint)MaxAssetReferenceCount)
        {
            throw Invalid($"asset-reference count '{descriptor.Count}' exceeds the safety limit");
        }

        var references = new SceneAssetReferenceKey[checked((int)descriptor.Count)];
        var reader = new ScenePayloadReader(GetSection(fileBytes, descriptor));
        SceneAssetReferenceKey? previous = null;
        for (int i = 0; i < references.Length; i++)
        {
            uint rawKind = reader.ReadUInt32();
            uint rawRequired = reader.ReadUInt32();
            Guid guid = reader.ReadGuid();
            uint packageIdIndex = reader.ReadUInt32();

            if (!Enum.IsDefined(typeof(CookedSceneAssetReferenceKind), rawKind))
            {
                throw Invalid($"asset reference {i} has unknown kind '{rawKind}'");
            }

            if (rawRequired > 1 || guid == Guid.Empty)
            {
                throw Invalid($"asset reference {i} has invalid flags or an empty GUID");
            }

            var kind = (CookedSceneAssetReferenceKind)rawKind;
            bool required = rawRequired != 0;
            if (required != (kind == CookedSceneAssetReferenceKind.Mesh))
            {
                throw Invalid($"asset reference {i} has a non-canonical required flag");
            }

            string packageId = ReadOptionalString(strings, packageIdIndex, $"asset reference {i} package id");
            var reference = new SceneAssetReferenceKey(kind, guid, packageId, required);
            if (previous is { } previousReference && CompareAssetReferences(previousReference, reference) >= 0)
            {
                throw Invalid("asset-reference table is not strictly canonical and unique");
            }

            ValidateAssetReference(assetDatabase, reference, i);
            references[i] = reference;
            previous = reference;
        }

        reader.EnsureEnd("asset-reference table");
        return references;
    }

    private static void ValidateAssetReference(
        IAssetDatabase assetDatabase,
        SceneAssetReferenceKey reference,
        int referenceIndex)
    {
        string expectedType = GetAssetType(reference.Kind);
        if (!assetDatabase.TryGetAssetDescriptor(reference.Guid, out AssetDescriptor asset))
        {
            throw Invalid(
                $"asset reference {referenceIndex} points to missing {expectedType} '{reference.Guid:D}'");
        }

        if (!string.Equals(asset.AssetType, expectedType, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid(
                $"asset reference {referenceIndex} points to type '{asset.AssetType}', expected '{expectedType}'");
        }

        if (!string.IsNullOrEmpty(reference.PackageId) &&
            !string.Equals(asset.PackageId, reference.PackageId, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid(
                $"asset reference {referenceIndex} package '{reference.PackageId}' does not match available package '{asset.PackageId}'");
        }
    }

    private static CookedSceneSchemaReadResult ReadComponentSchemas(
        ReadOnlySpan<byte> fileBytes,
        CookedSceneSectionDescriptor descriptor)
    {
        var supported = new List<SceneComponentSchemaInfo>(checked((int)descriptor.Count));
        var reader = new ScenePayloadReader(GetSection(fileBytes, descriptor));
        uint previousTypeId = 0;
        var declaredMask = CookedSceneComponentMask.None;
        var ignoredMask = CookedSceneComponentMask.None;
        bool hasRequiredTransform = false;
        for (int i = 0; i < descriptor.Count; i++)
        {
            uint typeId = reader.ReadUInt32();
            uint rawVersion = reader.ReadUInt32();
            uint rawFlags = reader.ReadUInt32();
            uint rawSectionType = reader.ReadUInt32();
            if (typeId == 0 || typeId <= previousTypeId || rawVersion == 0 ||
                (rawFlags & ~(uint)CookedSceneComponentSchemaFlags.Required) != 0)
            {
                throw Invalid($"component schema {i} has invalid identity, order, version, or flags");
            }

            bool required = (rawFlags & (uint)CookedSceneComponentSchemaFlags.Required) != 0;
            if (!SceneComponentSchemas.TryGetByTypeId(typeId, out var codec))
            {
                if (required)
                {
                    throw Invalid($"required component TypeId '{typeId}' is unknown");
                }

                previousTypeId = typeId;
                continue;
            }

            if (rawSectionType != (uint)GetComponentSectionType(typeId))
            {
                throw Invalid(
                    $"component '{codec.Info.Name}' maps to section '{rawSectionType}', expected '{(uint)GetComponentSectionType(typeId)}'");
            }

            var mask = GetComponentMask(typeId);
            if (rawVersion > codec.Info.Version)
            {
                if (required)
                {
                    throw Invalid(
                        $"required component '{codec.Info.Name}' version '{rawVersion}' is newer than supported version '{codec.Info.Version}'");
                }

                ignoredMask |= mask;
                previousTypeId = typeId;
                continue;
            }

            if (rawVersion < codec.Info.Version)
            {
                throw Invalid(
                    $"cooked component '{codec.Info.Name}' version '{rawVersion}' is not canonical; recook with version '{codec.Info.Version}'");
            }

            var schema = new SceneComponentSchemaInfo(
                typeId,
                codec.Info.Name,
                checked((int)rawVersion),
                required);
            supported.Add(schema);
            declaredMask |= mask;
            hasRequiredTransform |= typeId == SceneComponentSchemas.TransformTypeId && required;
            previousTypeId = typeId;
        }

        reader.EnsureEnd("component schema table");
        if (!hasRequiredTransform)
        {
            throw Invalid("required Transform component schema is missing");
        }

        return new CookedSceneSchemaReadResult(
            supported.ToArray(),
            declaredMask,
            ignoredMask);
    }

    private static SceneEntityBuilder[] ReadEntities(
        ReadOnlySpan<byte> fileBytes,
        CookedSceneSectionDescriptor descriptor,
        IReadOnlyList<string> strings,
        CookedSceneSchemaReadResult componentSchemas)
    {
        int entityCount = checked((int)descriptor.Count);
        var entities = new SceneEntityBuilder[entityCount];
        var reader = new ScenePayloadReader(GetSection(fileBytes, descriptor));
        var supportedMask = CookedSceneComponentMask.Name |
                            CookedSceneComponentMask.Transform |
                            CookedSceneComponentMask.Camera |
                            CookedSceneComponentMask.MeshRenderer |
                            CookedSceneComponentMask.DirectionalLight |
                            CookedSceneComponentMask.PointLight |
                            CookedSceneComponentMask.SpotLight |
                            CookedSceneComponentMask.Environment;

        for (int i = 0; i < entityCount; i++)
        {
            uint entityIndex = reader.ReadUInt32();
            uint nameStringIndex = reader.ReadUInt32();
            var componentMask = (CookedSceneComponentMask)reader.ReadUInt32();
            uint reserved = reader.ReadUInt32();
            Guid authoringGuid = reader.ReadGuid();

            if (entityIndex != i || reserved != 0 || authoringGuid == Guid.Empty)
            {
                throw Invalid($"entity record {i} has a non-canonical index, reserved value, or authoring GUID");
            }

            if (i > 0 && entities[i - 1].AuthoringGuid.CompareTo(authoringGuid) >= 0)
            {
                throw Invalid("entity table is not in strictly increasing authoring-GUID order");
            }

            if ((componentMask & ~supportedMask) != 0 ||
                (componentMask & CookedSceneComponentMask.Transform) == 0)
            {
                throw Invalid($"entity record {i} has an unsupported component mask");
            }

            var componentOnlyMask = componentMask & ~CookedSceneComponentMask.Name;
            if ((componentOnlyMask &
                 ~(componentSchemas.DeclaredMask | componentSchemas.IgnoredMask)) != 0)
            {
                throw Invalid($"entity record {i} uses a component without a schema declaration");
            }
            componentMask &= ~componentSchemas.IgnoredMask;

            string name = ReadOptionalString(strings, nameStringIndex, $"entity {i} name");
            bool hasName = !string.IsNullOrEmpty(name);
            if (hasName != ((componentMask & CookedSceneComponentMask.Name) != 0))
            {
                throw Invalid($"entity record {i} name does not match its component mask");
            }

            entities[i] = new SceneEntityBuilder(authoringGuid, name, componentMask);
        }

        reader.EnsureEnd("entity table");
        return entities;
    }

    private static void ReadHierarchy(
        ReadOnlySpan<byte> fileBytes,
        CookedSceneSectionDescriptor descriptor,
        SceneEntityBuilder[] entities)
    {
        var reader = new ScenePayloadReader(GetSection(fileBytes, descriptor));
        int previousChild = -1;
        for (int i = 0; i < descriptor.Count; i++)
        {
            int childIndex = checked((int)reader.ReadUInt32());
            int parentIndex = checked((int)reader.ReadUInt32());
            if (childIndex <= previousChild ||
                childIndex < 0 || childIndex >= entities.Length ||
                parentIndex < 0 || parentIndex >= entities.Length ||
                childIndex == parentIndex)
            {
                throw Invalid($"hierarchy record {i} has invalid or non-canonical entity indices");
            }

            entities[childIndex].ParentGuid = entities[parentIndex].AuthoringGuid;
            previousChild = childIndex;
        }

        reader.EnsureEnd("hierarchy table");
    }

    private static void ReadTransforms(
        ReadOnlySpan<byte> fileBytes,
        CookedSceneSectionDescriptor descriptor,
        SceneEntityBuilder[] entities)
    {
        var reader = new ScenePayloadReader(GetSection(fileBytes, descriptor));
        for (int i = 0; i < entities.Length; i++)
        {
            RequireCanonicalEntityIndex(reader.ReadUInt32(), i, "Transform");
            entities[i].Transform = new TransformComponent
            {
                Position = reader.ReadVector3(),
                Rotation = reader.ReadQuaternion(),
                Scale = reader.ReadVector3()
            };
            entities[i].HasTransform = true;
        }

        reader.EnsureEnd("Transform component stream");
    }

    private static void ReadCameras(
        ReadOnlySpan<byte> fileBytes,
        CookedSceneSectionDescriptor? descriptor,
        SceneEntityBuilder[] entities)
    {
        if (descriptor == null)
        {
            VerifyNoMaskedComponent(entities, CookedSceneComponentMask.Camera, "Camera");
            return;
        }

        var reader = new ScenePayloadReader(GetSection(fileBytes, descriptor.Value));
        int previous = -1;
        for (uint i = 0; i < descriptor.Value.Count; i++)
        {
            int entityIndex = ReadComponentEntityIndex(ref reader, entities, ref previous, CookedSceneComponentMask.Camera, "Camera");
            float verticalFov = reader.ReadSingle();
            float nearPlane = reader.ReadSingle();
            float farPlane = reader.ReadSingle();
            uint perspective = reader.ReadUInt32();
            if (perspective > 1)
            {
                throw Invalid($"Camera component for entity {entityIndex} has invalid perspective flag '{perspective}'");
            }

            entities[entityIndex].Camera = new CameraComponent
            {
                VerticalFov = verticalFov,
                NearPlane = nearPlane,
                FarPlane = farPlane,
                IsPerspective = checked((byte)perspective)
            };
        }

        reader.EnsureEnd("Camera component stream");
    }

    private static void ReadMeshRenderers(
        ReadOnlySpan<byte> fileBytes,
        CookedSceneSectionDescriptor? descriptor,
        SceneEntityBuilder[] entities,
        IReadOnlyList<SceneAssetReferenceKey> assetReferences)
    {
        if (descriptor == null)
        {
            VerifyNoMaskedComponent(entities, CookedSceneComponentMask.MeshRenderer, "MeshRenderer");
            return;
        }

        var reader = new ScenePayloadReader(GetSection(fileBytes, descriptor.Value));
        int previous = -1;
        for (uint i = 0; i < descriptor.Value.Count; i++)
        {
            int entityIndex = ReadComponentEntityIndex(ref reader, entities, ref previous, CookedSceneComponentMask.MeshRenderer, "MeshRenderer");
            SceneAssetReferenceKey meshReference = ReadAssetReference(
                assetReferences,
                reader.ReadUInt32(),
                CookedSceneAssetReferenceKind.Mesh,
                required: true,
                "MeshRenderer.Mesh");
            uint materialIndex = reader.ReadUInt32();
            SceneAssetReferenceKey? materialReference = materialIndex == NoIndex
                ? null
                : ReadAssetReference(
                    assetReferences,
                    materialIndex,
                    CookedSceneAssetReferenceKind.Material,
                    required: false,
                    "MeshRenderer.Material");
            int firstSubmeshIndex = reader.ReadInt32();
            int submeshCount = reader.ReadInt32();
            System.Numerics.Vector3 boundsCenter = reader.ReadVector3();
            System.Numerics.Vector3 boundsExtents = reader.ReadVector3();
            uint visible = reader.ReadUInt32();
            if (visible > 1)
            {
                throw Invalid($"MeshRenderer component for entity {entityIndex} has invalid visibility '{visible}'");
            }

            entities[entityIndex].MeshRenderer = new MeshRendererComponent
            {
                MeshGuid = meshReference.Guid,
                MaterialGuid = materialReference?.Guid ?? Guid.Empty,
                FirstSubmeshIndex = firstSubmeshIndex,
                SubmeshCount = submeshCount,
                BoundsCenter = boundsCenter,
                BoundsExtents = boundsExtents,
                Visible = checked((byte)visible)
            };
            entities[entityIndex].MeshPackageId = meshReference.PackageId;
            entities[entityIndex].MaterialPackageId = materialReference?.PackageId ?? string.Empty;
        }

        reader.EnsureEnd("MeshRenderer component stream");
    }

    private static void ReadDirectionalLights(
        ReadOnlySpan<byte> fileBytes,
        CookedSceneSectionDescriptor? descriptor,
        SceneEntityBuilder[] entities)
    {
        if (descriptor == null)
        {
            VerifyNoMaskedComponent(entities, CookedSceneComponentMask.DirectionalLight, "DirectionalLight");
            return;
        }

        var reader = new ScenePayloadReader(GetSection(fileBytes, descriptor.Value));
        int previous = -1;
        for (uint i = 0; i < descriptor.Value.Count; i++)
        {
            int entityIndex = ReadComponentEntityIndex(ref reader, entities, ref previous, CookedSceneComponentMask.DirectionalLight, "DirectionalLight");
            var light = new DirectionalLightComponent
            {
                Direction = reader.ReadVector3(),
                Color = reader.ReadVector3(),
                Intensity = reader.ReadSingle(),
                AmbientIntensity = reader.ReadSingle()
            };
            light.Enabled = ReadBooleanByte(ref reader, "DirectionalLight.Enabled", entityIndex);
            entities[entityIndex].DirectionalLight = light;
        }

        reader.EnsureEnd("DirectionalLight component stream");
    }

    private static void ReadPointLights(
        ReadOnlySpan<byte> fileBytes,
        CookedSceneSectionDescriptor? descriptor,
        SceneEntityBuilder[] entities)
    {
        if (descriptor == null)
        {
            VerifyNoMaskedComponent(entities, CookedSceneComponentMask.PointLight, "PointLight");
            return;
        }

        var reader = new ScenePayloadReader(GetSection(fileBytes, descriptor.Value));
        int previous = -1;
        for (uint i = 0; i < descriptor.Value.Count; i++)
        {
            int entityIndex = ReadComponentEntityIndex(ref reader, entities, ref previous, CookedSceneComponentMask.PointLight, "PointLight");
            var light = new PointLightComponent
            {
                Color = reader.ReadVector3(),
                Intensity = reader.ReadSingle(),
                Range = reader.ReadSingle()
            };
            light.Enabled = ReadBooleanByte(ref reader, "PointLight.Enabled", entityIndex);
            entities[entityIndex].PointLight = light;
        }

        reader.EnsureEnd("PointLight component stream");
    }

    private static void ReadSpotLights(
        ReadOnlySpan<byte> fileBytes,
        CookedSceneSectionDescriptor? descriptor,
        SceneEntityBuilder[] entities)
    {
        if (descriptor == null)
        {
            VerifyNoMaskedComponent(entities, CookedSceneComponentMask.SpotLight, "SpotLight");
            return;
        }

        var reader = new ScenePayloadReader(GetSection(fileBytes, descriptor.Value));
        int previous = -1;
        for (uint i = 0; i < descriptor.Value.Count; i++)
        {
            int entityIndex = ReadComponentEntityIndex(ref reader, entities, ref previous, CookedSceneComponentMask.SpotLight, "SpotLight");
            var light = new SpotLightComponent
            {
                Color = reader.ReadVector3(),
                Intensity = reader.ReadSingle(),
                Range = reader.ReadSingle(),
                InnerConeAngleDegrees = reader.ReadSingle(),
                OuterConeAngleDegrees = reader.ReadSingle()
            };
            light.Enabled = ReadBooleanByte(ref reader, "SpotLight.Enabled", entityIndex);
            entities[entityIndex].SpotLight = light;
        }

        reader.EnsureEnd("SpotLight component stream");
    }

    private static void ReadEnvironments(
        ReadOnlySpan<byte> fileBytes,
        CookedSceneSectionDescriptor? descriptor,
        SceneEntityBuilder[] entities,
        IReadOnlyList<SceneAssetReferenceKey> assetReferences)
    {
        if (descriptor == null)
        {
            VerifyNoMaskedComponent(entities, CookedSceneComponentMask.Environment, "Environment");
            return;
        }

        var reader = new ScenePayloadReader(GetSection(fileBytes, descriptor.Value));
        int previous = -1;
        for (uint i = 0; i < descriptor.Value.Count; i++)
        {
            int entityIndex = ReadComponentEntityIndex(ref reader, entities, ref previous, CookedSceneComponentMask.Environment, "Environment");
            uint environmentTextureIndex = reader.ReadUInt32();
            SceneAssetReferenceKey? environmentTexture = environmentTextureIndex == NoIndex
                ? null
                : ReadAssetReference(
                    assetReferences,
                    environmentTextureIndex,
                    CookedSceneAssetReferenceKind.EnvironmentTexture,
                    required: false,
                    "Environment.EnvironmentTexture");
            var environment = new SceneEnvironmentComponent
            {
                EnvironmentTextureGuid = environmentTexture?.Guid ?? Guid.Empty,
                SkyColor = reader.ReadVector3(),
                HorizonColor = reader.ReadVector3(),
                GroundColor = reader.ReadVector3(),
                AmbientColor = reader.ReadVector3(),
                SkyIntensity = reader.ReadSingle(),
                AmbientIntensity = reader.ReadSingle(),
                Exposure = reader.ReadSingle()
            };
            environment.Enabled = ReadBooleanByte(ref reader, "Environment.Enabled", entityIndex);
            entities[entityIndex].Environment = environment;
            entities[entityIndex].EnvironmentTexturePackageId = environmentTexture?.PackageId ?? string.Empty;
        }

        reader.EnsureEnd("Environment component stream");
    }

    private static byte ReadBooleanByte(ref ScenePayloadReader reader, string field, int entityIndex)
    {
        uint value = reader.ReadUInt32();
        if (value > 1)
        {
            throw Invalid($"{field} for entity {entityIndex} has invalid value '{value}'");
        }

        return checked((byte)value);
    }

    private static int ReadComponentEntityIndex(
        ref ScenePayloadReader reader,
        IReadOnlyList<SceneEntityBuilder> entities,
        ref int previous,
        CookedSceneComponentMask mask,
        string componentName)
    {
        uint rawIndex = reader.ReadUInt32();
        if (rawIndex >= (uint)entities.Count)
        {
            throw Invalid($"{componentName} references entity index '{rawIndex}' outside the entity table");
        }

        int entityIndex = checked((int)rawIndex);
        if (entityIndex <= previous)
        {
            throw Invalid($"{componentName} records are not in canonical entity order");
        }

        if ((entities[entityIndex].Mask & mask) == 0)
        {
            throw Invalid($"{componentName} record for entity {entityIndex} is absent from its component mask");
        }

        previous = entityIndex;
        return entityIndex;
    }

    private static void VerifyNoMaskedComponent(
        IReadOnlyList<SceneEntityBuilder> entities,
        CookedSceneComponentMask mask,
        string componentName)
    {
        for (int i = 0; i < entities.Count; i++)
        {
            if ((entities[i].Mask & mask) != 0)
            {
                throw Invalid($"entity {i} requires a missing {componentName} section");
            }
        }
    }

    private static SceneAssetReferenceKey ReadAssetReference(
        IReadOnlyList<SceneAssetReferenceKey> references,
        uint index,
        CookedSceneAssetReferenceKind expectedKind,
        bool required,
        string field)
    {
        if (index >= (uint)references.Count)
        {
            throw Invalid($"{field} asset-reference index '{index}' is outside the table");
        }

        SceneAssetReferenceKey reference = references[checked((int)index)];
        if (reference.Kind != expectedKind || reference.Required != required)
        {
            throw Invalid($"{field} asset-reference kind or required flag is invalid");
        }

        return reference;
    }

    private static CookedSceneSectionDescriptor RequireSection(
        IReadOnlyDictionary<CookedSceneSectionType, CookedSceneSectionDescriptor> sections,
        CookedSceneSectionType type,
        uint expectedStride,
        uint? expectedCount = null,
        int minimumCount = 0,
        int maximumCount = int.MaxValue)
    {
        if (!sections.TryGetValue(type, out var descriptor))
        {
            throw Invalid($"required section '{type}' is missing");
        }

        if ((descriptor.Flags & CookedSceneSectionFlags.Required) == 0)
        {
            throw Invalid($"core section '{type}' is not marked required");
        }

        ValidateKnownSection(descriptor, expectedStride, expectedCount, minimumCount, maximumCount);
        return descriptor;
    }

    private static CookedSceneSectionDescriptor RequireVariableSection(
        IReadOnlyDictionary<CookedSceneSectionType, CookedSceneSectionDescriptor> sections,
        CookedSceneSectionType type,
        int maximumCount)
    {
        if (!sections.TryGetValue(type, out var descriptor))
        {
            throw Invalid($"required section '{type}' is missing");
        }

        if ((descriptor.Flags & CookedSceneSectionFlags.Required) == 0 || descriptor.Stride != 0)
        {
            throw Invalid($"variable core section '{type}' has invalid flags or stride");
        }

        if (descriptor.Count > (uint)maximumCount)
        {
            throw Invalid($"section '{type}' count '{descriptor.Count}' exceeds '{maximumCount}'");
        }

        return descriptor;
    }

    private static CookedSceneSectionDescriptor? GetOptionalSection(
        IReadOnlyDictionary<CookedSceneSectionType, CookedSceneSectionDescriptor> sections,
        CookedSceneSectionType type,
        uint expectedStride)
    {
        if (!sections.TryGetValue(type, out var descriptor))
        {
            return null;
        }

        if ((descriptor.Flags & CookedSceneSectionFlags.Required) != 0)
        {
            throw Invalid($"optional section '{type}' is unexpectedly marked required");
        }

        ValidateKnownSection(descriptor, expectedStride, null, 0, MaxEntityCount);
        return descriptor;
    }

    private static CookedSceneSectionDescriptor? ResolveComponentSection(
        IReadOnlyDictionary<CookedSceneSectionType, CookedSceneSectionDescriptor> sections,
        CookedSceneSchemaReadResult componentSchemas,
        uint typeId,
        CookedSceneSectionType sectionType,
        uint expectedStride)
    {
        CookedSceneSectionDescriptor? descriptor = GetOptionalSection(
            sections,
            sectionType,
            expectedStride);
        if (componentSchemas.IsSupported(typeId))
        {
            return descriptor;
        }

        var mask = GetComponentMask(typeId);
        if ((componentSchemas.IgnoredMask & mask) != 0)
        {
            return null;
        }

        if (descriptor is { Count: > 0 })
        {
            throw Invalid(
                $"component section '{sectionType}' contains records without a schema declaration");
        }

        return null;
    }

    private static void ValidateKnownSection(
        CookedSceneSectionDescriptor descriptor,
        uint expectedStride,
        uint? expectedCount,
        int minimumCount,
        int maximumCount)
    {
        if (descriptor.Stride != expectedStride)
        {
            throw Invalid(
                $"section '{descriptor.Type}' stride '{descriptor.Stride}' does not match '{expectedStride}'");
        }

        if (expectedCount.HasValue && descriptor.Count != expectedCount.Value)
        {
            throw Invalid(
                $"section '{descriptor.Type}' count '{descriptor.Count}' does not match '{expectedCount.Value}'");
        }

        if (descriptor.Count < (uint)minimumCount || descriptor.Count > (uint)maximumCount)
        {
            throw Invalid($"section '{descriptor.Type}' count '{descriptor.Count}' is outside the supported range");
        }
    }

    private static ReadOnlySpan<byte> GetSection(
        ReadOnlySpan<byte> fileBytes,
        CookedSceneSectionDescriptor descriptor)
    {
        return fileBytes.Slice(checked((int)descriptor.Offset), checked((int)descriptor.Size));
    }

    private static void RequireCanonicalEntityIndex(uint rawIndex, int expected, string componentName)
    {
        if (rawIndex != expected)
        {
            throw Invalid(
                $"{componentName} record index '{rawIndex}' does not match canonical entity index '{expected}'");
        }
    }

    private static void EnsureZeroPadding(ReadOnlySpan<byte> bytes, string field)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != 0)
            {
                throw Invalid($"{field} contains nonzero data");
            }
        }
    }

    private static string ReadRequiredString(
        IReadOnlyList<string> strings,
        uint index,
        string field)
    {
        string value = ReadOptionalString(strings, index, field);
        if (string.IsNullOrEmpty(value))
        {
            throw Invalid($"{field} cannot be empty");
        }

        return value;
    }

    private static string ReadOptionalString(
        IReadOnlyList<string> strings,
        uint index,
        string field)
    {
        if (index == NoIndex)
        {
            return string.Empty;
        }

        if (index >= (uint)strings.Count)
        {
            throw Invalid($"{field} string index '{index}' is outside the string table");
        }

        return strings[checked((int)index)];
    }

    private static uint GetStringIndex(
        string value,
        IReadOnlyDictionary<string, uint> stringIndices)
    {
        return string.IsNullOrEmpty(value) ? NoIndex : stringIndices[value];
    }

    private static CookedSceneComponentMask GetComponentMask(in SceneStagingEntity entity)
    {
        var mask = CookedSceneComponentMask.Transform;
        if (!string.IsNullOrEmpty(entity.Name)) mask |= CookedSceneComponentMask.Name;
        if (entity.Camera.HasValue) mask |= CookedSceneComponentMask.Camera;
        if (entity.MeshRenderer.HasValue) mask |= CookedSceneComponentMask.MeshRenderer;
        if (entity.DirectionalLight.HasValue) mask |= CookedSceneComponentMask.DirectionalLight;
        if (entity.PointLight.HasValue) mask |= CookedSceneComponentMask.PointLight;
        if (entity.SpotLight.HasValue) mask |= CookedSceneComponentMask.SpotLight;
        if (entity.Environment.HasValue) mask |= CookedSceneComponentMask.Environment;
        return mask;
    }

    private static void ValidateComponentSchemas(
        IReadOnlyList<SceneComponentSchemaInfo> schemas)
    {
        if (schemas.Count == 0 || schemas.Count > SceneComponentSchemas.Supported.Count)
        {
            throw new InvalidOperationException(
                $"[SceneAssetCooker] Component schema count '{schemas.Count}' is invalid.");
        }

        uint previousTypeId = 0;
        bool hasRequiredTransform = false;
        for (int i = 0; i < schemas.Count; i++)
        {
            SceneComponentSchemaInfo schema = schemas[i];
            if (schema.TypeId <= previousTypeId ||
                !SceneComponentSchemas.TryGetByTypeId(schema.TypeId, out var codec) ||
                !string.Equals(schema.Name, codec.Info.Name, StringComparison.Ordinal) ||
                schema.Version != codec.Info.Version)
            {
                throw new InvalidOperationException(
                    $"[SceneAssetCooker] Component schema {i} is unsupported or not in canonical TypeId order.");
            }

            if (schema.TypeId == SceneComponentSchemas.TransformTypeId && schema.Required)
            {
                hasRequiredTransform = true;
            }

            previousTypeId = schema.TypeId;
        }

        if (!hasRequiredTransform)
        {
            throw new InvalidOperationException(
                "[SceneAssetCooker] A required Transform component schema is missing.");
        }
    }

    private static CookedSceneSectionType GetComponentSectionType(uint typeId)
    {
        return typeId switch
        {
            SceneComponentSchemas.TransformTypeId => CookedSceneSectionType.Transforms,
            SceneComponentSchemas.CameraTypeId => CookedSceneSectionType.Cameras,
            SceneComponentSchemas.MeshRendererTypeId => CookedSceneSectionType.MeshRenderers,
            SceneComponentSchemas.DirectionalLightTypeId => CookedSceneSectionType.DirectionalLights,
            SceneComponentSchemas.PointLightTypeId => CookedSceneSectionType.PointLights,
            SceneComponentSchemas.SpotLightTypeId => CookedSceneSectionType.SpotLights,
            SceneComponentSchemas.EnvironmentTypeId => CookedSceneSectionType.Environments,
            _ => throw Invalid($"component TypeId '{typeId}' has no cooked section")
        };
    }

    private static CookedSceneComponentMask GetComponentMask(uint typeId)
    {
        return typeId switch
        {
            SceneComponentSchemas.TransformTypeId => CookedSceneComponentMask.Transform,
            SceneComponentSchemas.CameraTypeId => CookedSceneComponentMask.Camera,
            SceneComponentSchemas.MeshRendererTypeId => CookedSceneComponentMask.MeshRenderer,
            SceneComponentSchemas.DirectionalLightTypeId => CookedSceneComponentMask.DirectionalLight,
            SceneComponentSchemas.PointLightTypeId => CookedSceneComponentMask.PointLight,
            SceneComponentSchemas.SpotLightTypeId => CookedSceneComponentMask.SpotLight,
            SceneComponentSchemas.EnvironmentTypeId => CookedSceneComponentMask.Environment,
            _ => CookedSceneComponentMask.None
        };
    }

    private static string GetAssetType(CookedSceneAssetReferenceKind kind)
    {
        return kind switch
        {
            CookedSceneAssetReferenceKind.Mesh => "Mesh",
            CookedSceneAssetReferenceKind.Material => "Material",
            CookedSceneAssetReferenceKind.EnvironmentTexture => "EnvironmentTexture",
            _ => throw Invalid($"asset-reference kind '{kind}' is unsupported")
        };
    }

    private static void WriteDescriptor(
        Span<byte> destination,
        CookedSceneSectionDescriptor descriptor)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(0, 4), (uint)descriptor.Type);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(4, 4), (uint)descriptor.Flags);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(8, 8), descriptor.Offset);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(16, 8), descriptor.Size);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(24, 4), descriptor.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(28, 4), descriptor.Stride);
    }

    private static void WriteGuid(Span<byte> destination, Guid guid)
    {
        if (!guid.TryWriteBytes(destination, bigEndian: true, out int bytesWritten) || bytesWritten != 16)
        {
            throw new InvalidOperationException("[SceneAssetCooker] Failed to write a GUID.");
        }
    }

    private static Guid ReadGuid(ReadOnlySpan<byte> source)
    {
        return new Guid(source, bigEndian: true);
    }

    private static int Align8(int value)
    {
        return checked((value + 7) & ~7);
    }

    private static InvalidDataException Invalid(string reason)
    {
        return new InvalidDataException(reason);
    }

    private static SceneLoadResult Failure(string diagnostic)
    {
        return new SceneLoadResult(false, 0, 0, 0, 0, 0, 0, 0, diagnostic);
    }

    private sealed class SceneEntityBuilder
    {
        public SceneEntityBuilder(
            Guid authoringGuid,
            string name,
            CookedSceneComponentMask mask)
        {
            AuthoringGuid = authoringGuid;
            Name = name;
            Mask = mask;
        }

        public Guid AuthoringGuid { get; }
        public Guid ParentGuid { get; set; }
        public string Name { get; }
        public CookedSceneComponentMask Mask { get; }
        public bool HasTransform { get; set; }
        public TransformComponent Transform { get; set; }
        public CameraComponent? Camera { get; set; }
        public MeshRendererComponent? MeshRenderer { get; set; }
        public string MeshPackageId { get; set; } = string.Empty;
        public string MaterialPackageId { get; set; } = string.Empty;
        public DirectionalLightComponent? DirectionalLight { get; set; }
        public PointLightComponent? PointLight { get; set; }
        public SpotLightComponent? SpotLight { get; set; }
        public SceneEnvironmentComponent? Environment { get; set; }
        public string EnvironmentTexturePackageId { get; set; } = string.Empty;

        public SceneStagingEntity Build(int entityIndex)
        {
            if (!HasTransform)
            {
                throw Invalid($"entity {entityIndex} is missing its Transform record");
            }

            VerifyMask(entityIndex, CookedSceneComponentMask.Camera, Camera.HasValue, "Camera");
            VerifyMask(entityIndex, CookedSceneComponentMask.MeshRenderer, MeshRenderer.HasValue, "MeshRenderer");
            VerifyMask(entityIndex, CookedSceneComponentMask.DirectionalLight, DirectionalLight.HasValue, "DirectionalLight");
            VerifyMask(entityIndex, CookedSceneComponentMask.PointLight, PointLight.HasValue, "PointLight");
            VerifyMask(entityIndex, CookedSceneComponentMask.SpotLight, SpotLight.HasValue, "SpotLight");
            VerifyMask(entityIndex, CookedSceneComponentMask.Environment, Environment.HasValue, "Environment");

            return new SceneStagingEntity(
                AuthoringGuid,
                ParentGuid,
                Name,
                Transform,
                Camera,
                MeshRenderer,
                MeshPackageId,
                MaterialPackageId,
                DirectionalLight,
                PointLight,
                SpotLight,
                Environment,
                EnvironmentTexturePackageId);
        }

        private void VerifyMask(
            int entityIndex,
            CookedSceneComponentMask component,
            bool hasValue,
            string componentName)
        {
            if (((Mask & component) != 0) != hasValue)
            {
                throw Invalid($"entity {entityIndex} {componentName} mask does not match its component stream");
            }
        }
    }

    private sealed class ScenePayloadWriter : IDisposable
    {
        private readonly MemoryStream m_Stream = new();

        public void WriteUInt32(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            m_Stream.Write(bytes);
        }

        public void WriteInt32(int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            m_Stream.Write(bytes);
        }

        public void WriteSingle(float value)
        {
            WriteInt32(BitConverter.SingleToInt32Bits(value));
        }

        public void WriteGuid(Guid value)
        {
            Span<byte> bytes = stackalloc byte[16];
            SceneAssetCooker.WriteGuid(bytes, value);
            m_Stream.Write(bytes);
        }

        public void WriteVector3(System.Numerics.Vector3 value)
        {
            WriteSingle(value.X);
            WriteSingle(value.Y);
            WriteSingle(value.Z);
        }

        public void WriteQuaternion(System.Numerics.Quaternion value)
        {
            WriteSingle(value.X);
            WriteSingle(value.Y);
            WriteSingle(value.Z);
            WriteSingle(value.W);
        }

        public void WriteBytes(ReadOnlySpan<byte> bytes)
        {
            m_Stream.Write(bytes);
        }

        public byte[] ToArray()
        {
            return m_Stream.ToArray();
        }

        public void Dispose()
        {
            m_Stream.Dispose();
        }
    }

    private ref struct ScenePayloadReader
    {
        private readonly ReadOnlySpan<byte> m_Bytes;
        private int m_Offset;

        public ScenePayloadReader(ReadOnlySpan<byte> bytes)
        {
            m_Bytes = bytes;
            m_Offset = 0;
        }

        public uint ReadUInt32()
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(4));
        }

        public int ReadInt32()
        {
            return BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(4));
        }

        public float ReadSingle()
        {
            return BitConverter.Int32BitsToSingle(ReadInt32());
        }

        public Guid ReadGuid()
        {
            return SceneAssetCooker.ReadGuid(ReadBytes(16));
        }

        public System.Numerics.Vector3 ReadVector3()
        {
            return new System.Numerics.Vector3(ReadSingle(), ReadSingle(), ReadSingle());
        }

        public System.Numerics.Quaternion ReadQuaternion()
        {
            return new System.Numerics.Quaternion(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
        }

        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            if (count < 0 || m_Offset > m_Bytes.Length - count)
            {
                throw Invalid("section payload is truncated");
            }

            ReadOnlySpan<byte> result = m_Bytes.Slice(m_Offset, count);
            m_Offset += count;
            return result;
        }

        public void EnsureEnd(string sectionName)
        {
            if (m_Offset != m_Bytes.Length)
            {
                throw Invalid($"{sectionName} has trailing or unread bytes");
            }
        }
    }
}
