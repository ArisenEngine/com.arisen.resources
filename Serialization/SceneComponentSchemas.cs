using System.Globalization;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace ArisenEngine.Resources.Serialization;

public readonly record struct SceneComponentSchemaInfo(
    uint TypeId,
    string Name,
    int Version,
    bool Required);

public static class SceneComponentSchemas
{
    public const int CurrentSceneVersion = 2;

    public const uint TransformTypeId = 1;
    public const uint CameraTypeId = 2;
    public const uint MeshRendererTypeId = 3;
    public const uint DirectionalLightTypeId = 4;
    public const uint PointLightTypeId = 5;
    public const uint SpotLightTypeId = 6;
    public const uint EnvironmentTypeId = 7;

    private static readonly SceneComponentCodec[] s_Codecs =
    [
        new(
            new SceneComponentSchemaInfo(TransformTypeId, "Transform", 1, true),
            entity => entity.Transform,
            (entity, value) => entity.Transform = (SceneTransformSource?)value),
        new(
            new SceneComponentSchemaInfo(CameraTypeId, "Camera", 2, true),
            entity => entity.Camera,
            (entity, value) => entity.Camera = (SceneCameraSource?)value,
            new Dictionary<int, Action<YamlMappingNode>>
            {
                [1] = MigrateCameraV1ToV2
            }),
        new(
            new SceneComponentSchemaInfo(MeshRendererTypeId, "MeshRenderer", 1, true),
            entity => entity.MeshRenderer,
            (entity, value) => entity.MeshRenderer = (SceneMeshRendererSource?)value),
        new(
            new SceneComponentSchemaInfo(DirectionalLightTypeId, "DirectionalLight", 1, true),
            entity => entity.DirectionalLight,
            (entity, value) => entity.DirectionalLight = (SceneDirectionalLightSource?)value),
        new(
            new SceneComponentSchemaInfo(PointLightTypeId, "PointLight", 1, true),
            entity => entity.PointLight,
            (entity, value) => entity.PointLight = (ScenePointLightSource?)value),
        new(
            new SceneComponentSchemaInfo(SpotLightTypeId, "SpotLight", 1, true),
            entity => entity.SpotLight,
            (entity, value) => entity.SpotLight = (SceneSpotLightSource?)value),
        new(
            new SceneComponentSchemaInfo(EnvironmentTypeId, "Environment", 1, true),
            entity => entity.Environment,
            (entity, value) => entity.Environment = (SceneEnvironmentSource?)value)
    ];

    public static IReadOnlyList<SceneComponentSchemaInfo> Supported =>
        GetCodecs().Select(codec => codec.Info).ToArray();

    internal static IReadOnlyList<SceneComponentCodec> Codecs => GetCodecs();

    internal static bool TryGetByTypeId(uint typeId, out SceneComponentCodec codec)
    {
        for (int i = 0; i < s_Codecs.Length; i++)
        {
            if (s_Codecs[i].Info.TypeId == typeId)
            {
                codec = s_Codecs[i];
                return true;
            }
        }

        if (SceneComponentExtensionRegistry.Shared.TryGetByTypeId(typeId, out var extension))
        {
            codec = new SceneComponentCodec(extension);
            return true;
        }

        codec = null!;
        return false;
    }

    internal static bool TryGetByName(string name, out SceneComponentCodec codec)
    {
        for (int i = 0; i < s_Codecs.Length; i++)
        {
            if (string.Equals(s_Codecs[i].Info.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                codec = s_Codecs[i];
                return true;
            }
        }

        if (SceneComponentExtensionRegistry.Shared.TryGetByName(name, out var extension))
        {
            codec = new SceneComponentCodec(extension);
            return true;
        }

        codec = null!;
        return false;
    }

    internal static bool TryPrepareDocument(
        YamlMappingNode root,
        string sourcePath,
        out string diagnostic)
    {
        if (!TryReadInt32(root, "Version", out int sceneVersion) ||
            sceneVersion != CurrentSceneVersion)
        {
            diagnostic = sceneVersion == 1
                ? $"[SceneAssetLoader] Scene '{sourcePath}' uses legacy schema version 1. Run the explicit MigrateLegacySceneSource migration once to assign persistent entity GUIDs and component schemas."
                : $"[SceneAssetLoader] Scene '{sourcePath}' schema version '{sceneVersion}' is not supported; expected '{CurrentSceneVersion}'.";
            return false;
        }

        if (!TryGetSequence(root, "ComponentSchemas", out var schemaSequence))
        {
            diagnostic =
                $"[SceneAssetLoader] Scene '{sourcePath}' schema version 2 requires a ComponentSchemas sequence.";
            return false;
        }

        var schemasById = new Dictionary<uint, ResolvedSceneComponentSchema>();
        var schemasByName = new Dictionary<string, ResolvedSceneComponentSchema>(StringComparer.OrdinalIgnoreCase);
        var unsupportedOptionalDeclarations = new List<YamlNode>();
        for (int i = 0; i < schemaSequence.Children.Count; i++)
        {
            if (schemaSequence.Children[i] is not YamlMappingNode schemaNode)
            {
                diagnostic =
                    $"[SceneAssetLoader] Scene '{sourcePath}' component schema {i} must be a mapping.";
                return false;
            }

            if (!TryReadUInt32(schemaNode, "TypeId", out uint typeId) || typeId == 0 ||
                !TryReadString(schemaNode, "Name", out string name) || string.IsNullOrWhiteSpace(name) ||
                !TryReadInt32(schemaNode, "Version", out int version) || version < 0 ||
                !TryReadBoolean(schemaNode, "Required", out bool required))
            {
                diagnostic =
                    $"[SceneAssetLoader] Scene '{sourcePath}' component schema {i} requires non-zero TypeId, Name, non-negative Version, and Required fields.";
                return false;
            }

            name = name.Trim();
            if (schemasById.ContainsKey(typeId) || schemasByName.ContainsKey(name))
            {
                diagnostic =
                    $"[SceneAssetLoader] Scene '{sourcePath}' has duplicate component schema identity TypeId '{typeId}' or Name '{name}'.";
                return false;
            }

            SceneComponentCodec? codec = null;
            if (TryGetByTypeId(typeId, out var byId))
            {
                if (!string.Equals(byId.Info.Name, name, StringComparison.Ordinal))
                {
                    diagnostic =
                        $"[SceneAssetLoader] Scene '{sourcePath}' component TypeId '{typeId}' is named '{name}', expected stable name '{byId.Info.Name}'.";
                    return false;
                }

                codec = byId;
            }
            else if (TryGetByName(name, out var byName))
            {
                diagnostic =
                    $"[SceneAssetLoader] Scene '{sourcePath}' component '{name}' uses TypeId '{typeId}', expected stable TypeId '{byName.Info.TypeId}'.";
                return false;
            }

            bool supported = codec != null && version <= codec.Info.Version;
            if (!supported && required)
            {
                string reason = codec == null
                    ? "is unknown"
                    : $"uses newer version '{version}', while this runtime supports '{codec.Info.Version}'";
                diagnostic =
                    $"[SceneAssetLoader] Scene '{sourcePath}' required component '{name}' (TypeId {typeId}) {reason}.";
                return false;
            }

            var resolved = new ResolvedSceneComponentSchema(
                typeId,
                name,
                version,
                required,
                codec,
                schemaNode);
            schemasById.Add(typeId, resolved);
            schemasByName.Add(name, resolved);
            if (!supported)
            {
                unsupportedOptionalDeclarations.Add(schemaNode);
            }
        }

        if (!schemasById.TryGetValue(TransformTypeId, out var transformSchema) ||
            !transformSchema.Required ||
            transformSchema.Codec == null)
        {
            diagnostic =
                $"[SceneAssetLoader] Scene '{sourcePath}' must declare required Transform component TypeId '{TransformTypeId}'.";
            return false;
        }

        if (!TryGetSequence(root, "Entities", out var entities))
        {
            diagnostic = $"[SceneAssetLoader] Scene '{sourcePath}' has no Entities sequence.";
            return false;
        }

        var entityGuids = new HashSet<Guid>();
        for (int i = 0; i < entities.Children.Count; i++)
        {
            if (entities.Children[i] is not YamlMappingNode entity)
            {
                diagnostic =
                    $"[SceneAssetLoader] Scene '{sourcePath}' entity {i} must be a mapping.";
                return false;
            }

            if (!TryReadGuid(entity, "Guid", out Guid entityGuid) || entityGuid == Guid.Empty)
            {
                diagnostic =
                    $"[SceneAssetLoader] Scene '{sourcePath}' entity {i} has no persistent Guid. Run the explicit legacy migration or assign a non-empty GUID.";
                return false;
            }

            if (!entityGuids.Add(entityGuid))
            {
                diagnostic =
                    $"[SceneAssetLoader] Scene '{sourcePath}' contains duplicate entity Guid '{entityGuid:D}'.";
                return false;
            }

            var fieldsToRemove = new List<YamlNode>();
            foreach (var child in entity.Children)
            {
                if (child.Key is not YamlScalarNode key || string.IsNullOrWhiteSpace(key.Value))
                {
                    diagnostic =
                        $"[SceneAssetLoader] Scene '{sourcePath}' entity '{entityGuid:D}' has a non-scalar field name.";
                    return false;
                }

                string fieldName = key.Value;
                if (IsEntityMetadataField(fieldName))
                {
                    continue;
                }

                if (!schemasByName.TryGetValue(fieldName, out var schema))
                {
                    diagnostic =
                        $"[SceneAssetLoader] Scene '{sourcePath}' entity '{entityGuid:D}' field '{fieldName}' has no ComponentSchemas declaration.";
                    return false;
                }

                if (schema.Codec == null || schema.Version > schema.Codec.Info.Version)
                {
                    fieldsToRemove.Add(child.Key);
                    continue;
                }

                if (child.Value is not YamlMappingNode componentNode)
                {
                    diagnostic =
                        $"[SceneAssetLoader] Scene '{sourcePath}' entity '{entityGuid:D}' component '{fieldName}' must be a mapping.";
                    return false;
                }

                int version = schema.Version;
                while (version < schema.Codec.Info.Version)
                {
                    if (!schema.Codec.Migrations.TryGetValue(version, out var migration))
                    {
                        diagnostic =
                            $"[SceneAssetLoader] Scene '{sourcePath}' component '{fieldName}' has no migration from version '{version}' to '{version + 1}'.";
                        return false;
                    }

                    migration(componentNode);
                    version++;
                }

                if (version != schema.Version)
                {
                    SetChild(schema.SourceNode, "Version", version.ToString(CultureInfo.InvariantCulture));
                }
            }

            for (int removeIndex = 0; removeIndex < fieldsToRemove.Count; removeIndex++)
            {
                entity.Children.Remove(fieldsToRemove[removeIndex]);
            }
        }

        for (int i = 0; i < unsupportedOptionalDeclarations.Count; i++)
        {
            schemaSequence.Children.Remove(unsupportedOptionalDeclarations[i]);
        }

        diagnostic = string.Empty;
        return true;
    }

    internal static YamlSequenceNode CreateCurrentDeclarations(
        IReadOnlySet<string> componentNames)
    {
        var declarations = new YamlSequenceNode();
        IReadOnlyList<SceneComponentCodec> codecs = Codecs;
        for (int i = 0; i < codecs.Count; i++)
        {
            var codec = codecs[i];
            if (codec.Info.TypeId != TransformTypeId && !componentNames.Contains(codec.Info.Name))
            {
                continue;
            }

            declarations.Add(new YamlMappingNode
            {
                { "TypeId", codec.Info.TypeId.ToString(CultureInfo.InvariantCulture) },
                { "Name", codec.Info.Name },
                { "Version", codec.Info.Version.ToString(CultureInfo.InvariantCulture) },
                { "Required", "true" }
            });
        }

        return declarations;
    }

    private static SceneComponentCodec[] GetCodecs()
    {
        ISceneComponentExtensionCodec[] extensions =
            SceneComponentExtensionRegistry.Shared.GetCodecs();
        if (extensions.Length == 0)
        {
            return s_Codecs;
        }

        var codecs = new SceneComponentCodec[s_Codecs.Length + extensions.Length];
        Array.Copy(s_Codecs, codecs, s_Codecs.Length);
        for (int i = 0; i < extensions.Length; i++)
        {
            codecs[s_Codecs.Length + i] = new SceneComponentCodec(extensions[i]);
        }

        Array.Sort(codecs, static (left, right) => left.Info.TypeId.CompareTo(right.Info.TypeId));
        return codecs;
    }

    internal static bool TryGetChild(
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

    internal static void SetChild(YamlMappingNode mapping, string key, YamlNode value)
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
        }
        else
        {
            mapping.Add(key, value);
        }
    }

    private static bool IsEntityMetadataField(string fieldName)
    {
        return string.Equals(fieldName, "Guid", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fieldName, "Name", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fieldName, "Parent", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetSequence(
        YamlMappingNode mapping,
        string key,
        out YamlSequenceNode sequence)
    {
        if (TryGetChild(mapping, key, out var node) && node is YamlSequenceNode found)
        {
            sequence = found;
            return true;
        }

        sequence = null!;
        return false;
    }

    private static bool TryReadString(
        YamlMappingNode mapping,
        string key,
        out string value)
    {
        if (TryGetChild(mapping, key, out var node) &&
            node is YamlScalarNode scalar &&
            scalar.Value != null)
        {
            value = scalar.Value;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryReadInt32(
        YamlMappingNode mapping,
        string key,
        out int value)
    {
        value = 0;
        return TryReadString(mapping, key, out string text) &&
               int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadUInt32(
        YamlMappingNode mapping,
        string key,
        out uint value)
    {
        value = 0;
        return TryReadString(mapping, key, out string text) &&
               uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadBoolean(
        YamlMappingNode mapping,
        string key,
        out bool value)
    {
        value = false;
        return TryReadString(mapping, key, out string text) && bool.TryParse(text, out value);
    }

    private static bool TryReadGuid(
        YamlMappingNode mapping,
        string key,
        out Guid value)
    {
        value = Guid.Empty;
        return TryReadString(mapping, key, out string text) && Guid.TryParse(text, out value);
    }

    private static void MigrateCameraV1ToV2(YamlMappingNode component)
    {
        if (!TryGetChild(component, "FieldOfView", out var fieldOfView))
        {
            return;
        }

        if (TryGetChild(component, "VerticalFov", out _))
        {
            throw new InvalidDataException(
                "Camera v1 payload contains both FieldOfView and VerticalFov.");
        }

        YamlNode? fieldOfViewKey = null;
        foreach (var child in component.Children)
        {
            if (child.Key is YamlScalarNode scalar &&
                string.Equals(scalar.Value, "FieldOfView", StringComparison.OrdinalIgnoreCase))
            {
                fieldOfViewKey = child.Key;
                break;
            }
        }

        if (fieldOfViewKey != null)
        {
            component.Children.Remove(fieldOfViewKey);
        }
        component.Add("VerticalFov", fieldOfView);
    }

    private sealed record ResolvedSceneComponentSchema(
        uint TypeId,
        string Name,
        int Version,
        bool Required,
        SceneComponentCodec? Codec,
        YamlMappingNode SourceNode);
}

public static class SceneAuthoringIdentity
{
    public static Guid CreateEntityGuid()
    {
        return Guid.NewGuid();
    }
}

internal sealed class SceneComponentCodec
{
    public SceneComponentCodec(
        SceneComponentSchemaInfo info,
        Func<SceneEntitySource, object?> read,
        Action<SceneEntitySource, object?> write,
        IReadOnlyDictionary<int, Action<YamlMappingNode>>? migrations = null)
    {
        Info = info;
        Read = read;
        Write = write;
        Migrations = migrations ?? new Dictionary<int, Action<YamlMappingNode>>();
    }

    public SceneComponentCodec(ISceneComponentExtensionCodec extension)
    {
        Extension = extension ?? throw new ArgumentNullException(nameof(extension));
        Info = extension.Schema;
        Read = _ => null;
        Write = static (_, _) => { };
        Migrations = new Dictionary<int, Action<YamlMappingNode>>();
    }

    public SceneComponentSchemaInfo Info { get; }
    public Func<SceneEntitySource, object?> Read { get; }
    public Action<SceneEntitySource, object?> Write { get; }
    public IReadOnlyDictionary<int, Action<YamlMappingNode>> Migrations { get; }
    public ISceneComponentExtensionCodec? Extension { get; }
}

internal sealed class SceneSourceDocument
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = string.Empty;
    public List<SceneComponentSchemaSource> ComponentSchemas { get; set; } = new();
    public List<SceneEntitySource> Entities { get; set; } = new();
}

internal sealed class SceneComponentSchemaSource
{
    public uint TypeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; }
    public bool Required { get; set; }
}

internal sealed class SceneEntitySource
{
    public Guid Guid { get; set; }
    public string Name { get; set; } = string.Empty;
    public SceneParentReferenceSource? Parent { get; set; }
    public SceneTransformSource? Transform { get; set; }
    public SceneCameraSource? Camera { get; set; }
    public SceneDirectionalLightSource? DirectionalLight { get; set; }
    public ScenePointLightSource? PointLight { get; set; }
    public SceneSpotLightSource? SpotLight { get; set; }
    public SceneEnvironmentSource? Environment { get; set; }
    public SceneMeshRendererSource? MeshRenderer { get; set; }

    [YamlIgnore]
    public Dictionary<uint, YamlMappingNode> ExtensionComponents { get; } = new();
}

internal sealed class SceneParentReferenceSource
{
    public Guid SceneGuid { get; set; }
    public Guid EntityGuid { get; set; }
}

internal sealed class SceneTransformSource
{
    public SceneVector3Source? Position { get; set; }
    public SceneQuaternionSource? Rotation { get; set; }
    public SceneVector3Source? Scale { get; set; }
}

internal sealed class SceneCameraSource
{
    public float VerticalFov { get; set; } = 60.0f;
    public float NearPlane { get; set; } = 0.1f;
    public float FarPlane { get; set; } = 1000.0f;
    public bool IsPerspective { get; set; } = true;
}

internal sealed class SceneMeshRendererSource
{
    public SceneAssetReferenceSource? Mesh { get; set; }
    public SceneAssetReferenceSource? Material { get; set; }
    public int FirstSubmeshIndex { get; set; }
    public int SubmeshCount { get; set; } = -1;
    public SceneVector3Source? BoundsCenter { get; set; }
    public SceneVector3Source? BoundsExtents { get; set; }
    public bool Visible { get; set; } = true;
}

internal sealed class SceneDirectionalLightSource
{
    public SceneVector3Source? Direction { get; set; }
    public SceneVector3Source? Color { get; set; }
    public float Intensity { get; set; } = 1.0f;
    public float AmbientIntensity { get; set; } = 0.18f;
    public bool Enabled { get; set; } = true;
}

internal sealed class ScenePointLightSource
{
    public SceneVector3Source? Color { get; set; }
    public float Intensity { get; set; } = 1.0f;
    public float Range { get; set; } = 4.0f;
    public bool Enabled { get; set; } = true;
}

internal sealed class SceneSpotLightSource
{
    public SceneVector3Source? Color { get; set; }
    public float Intensity { get; set; } = 1.0f;
    public float Range { get; set; } = 4.0f;
    public float InnerConeAngleDegrees { get; set; } = 18.0f;
    public float OuterConeAngleDegrees { get; set; } = 28.0f;
    public bool Enabled { get; set; } = true;
}

internal sealed class SceneEnvironmentSource
{
    public SceneAssetReferenceSource? EnvironmentTexture { get; set; }
    public SceneVector3Source? SkyColor { get; set; }
    public SceneVector3Source? HorizonColor { get; set; }
    public SceneVector3Source? GroundColor { get; set; }
    public SceneVector3Source? AmbientColor { get; set; }
    public float SkyIntensity { get; set; } = 0.85f;
    public float AmbientIntensity { get; set; } = 0.32f;
    public float Exposure { get; set; } = ArisenEngine.Core.ECS.SceneEnvironmentComponent.DefaultExposure;
    public bool Enabled { get; set; } = true;
}

internal sealed class SceneAssetReferenceSource
{
    public Guid Guid { get; set; }
    public string PackageId { get; set; } = string.Empty;
}

internal sealed class SceneVector3Source
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}

internal sealed class SceneQuaternionSource
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float W { get; set; } = 1.0f;
}
