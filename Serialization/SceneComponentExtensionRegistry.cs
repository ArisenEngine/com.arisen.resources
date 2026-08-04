using ArisenEngine.Core.Assets;
using ArisenEngine.Core.ECS;
using YamlDotNet.RepresentationModel;

namespace ArisenEngine.Resources.Serialization;

public readonly record struct SceneComponentReadContext(
    IAssetDatabase AssetDatabase,
    Guid SceneGuid,
    Guid EntityGuid,
    string DiagnosticPath);

public readonly record struct SceneComponentActivationContext(
    WorldCellId WorldCellId,
    WorldPosition CellOrigin,
    WorldBounds CellBounds)
{
    public bool HasWorldCell => WorldCellId.IsValid;
}

public interface ISceneComponentExtensionActivationValidator
{
    bool TryValidateActivation(
        in SceneComponentActivationContext context,
        object component,
        out string diagnostic);
}

public interface ISceneComponentExtensionCodec
{
    SceneComponentSchemaInfo Schema { get; }

    bool TryReadSource(
        in SceneComponentReadContext context,
        YamlMappingNode source,
        out object component,
        out string diagnostic);

    byte[] WriteCooked(object component);

    bool TryReadCooked(
        in SceneComponentReadContext context,
        ReadOnlySpan<byte> payload,
        out object component,
        out string diagnostic);

    IReadOnlyList<CookedSceneDependency> GetDependencies(object component);

    Guid GetExclusiveOwnershipId(object component);

    void AddToEntity(EntityManager entityManager, Entity entity, object component);
}

public interface ISceneComponentExtensionRegistry
{
    void Register(ISceneComponentExtensionCodec codec);

    bool Unregister(ISceneComponentExtensionCodec codec);

    IReadOnlyList<SceneComponentSchemaInfo> GetRegistrations();
}

public sealed class SceneComponentExtensionRegistry : ISceneComponentExtensionRegistry
{
    public const uint MinimumExtensionTypeId = 1_024;

    private readonly object m_Gate = new();
    private readonly Dictionary<uint, ISceneComponentExtensionCodec> m_CodecsByTypeId = new();
    private readonly Dictionary<string, ISceneComponentExtensionCodec> m_CodecsByName =
        new(StringComparer.OrdinalIgnoreCase);

    private SceneComponentExtensionRegistry()
    {
    }

    public static SceneComponentExtensionRegistry Shared { get; } = new();

    public void Register(ISceneComponentExtensionCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        SceneComponentSchemaInfo schema = codec.Schema;
        ValidateSchema(schema);

        lock (m_Gate)
        {
            if (m_CodecsByTypeId.ContainsKey(schema.TypeId))
            {
                throw Invalid($"Component TypeId '{schema.TypeId}' is already registered.");
            }

            if (m_CodecsByName.ContainsKey(schema.Name))
            {
                throw Invalid($"Component name '{schema.Name}' is already registered.");
            }

            m_CodecsByTypeId.Add(schema.TypeId, codec);
            m_CodecsByName.Add(schema.Name, codec);
        }
    }

    public bool Unregister(ISceneComponentExtensionCodec codec)
    {
        if (codec == null)
        {
            return false;
        }

        lock (m_Gate)
        {
            SceneComponentSchemaInfo schema = codec.Schema;
            if (!m_CodecsByTypeId.TryGetValue(schema.TypeId, out var registered) ||
                !ReferenceEquals(registered, codec))
            {
                return false;
            }

            m_CodecsByTypeId.Remove(schema.TypeId);
            m_CodecsByName.Remove(schema.Name);
            return true;
        }
    }

    public IReadOnlyList<SceneComponentSchemaInfo> GetRegistrations()
    {
        lock (m_Gate)
        {
            return m_CodecsByTypeId.Values
                .Select(codec => codec.Schema)
                .OrderBy(schema => schema.TypeId)
                .ToArray();
        }
    }

    internal bool TryGetByTypeId(
        uint typeId,
        out ISceneComponentExtensionCodec codec)
    {
        lock (m_Gate)
        {
            return m_CodecsByTypeId.TryGetValue(typeId, out codec!);
        }
    }

    internal bool TryGetByName(
        string name,
        out ISceneComponentExtensionCodec codec)
    {
        lock (m_Gate)
        {
            return m_CodecsByName.TryGetValue(name, out codec!);
        }
    }

    internal ISceneComponentExtensionCodec[] GetCodecs()
    {
        lock (m_Gate)
        {
            return m_CodecsByTypeId.Values
                .OrderBy(codec => codec.Schema.TypeId)
                .ToArray();
        }
    }

    private static void ValidateSchema(SceneComponentSchemaInfo schema)
    {
        if (schema.TypeId < MinimumExtensionTypeId)
        {
            throw Invalid(
                $"Extension TypeId '{schema.TypeId}' is reserved; extension IDs begin at " +
                $"'{MinimumExtensionTypeId}'.");
        }

        if (string.IsNullOrWhiteSpace(schema.Name) ||
            !string.Equals(schema.Name, schema.Name.Trim(), StringComparison.Ordinal) ||
            schema.Name.Any(char.IsControl))
        {
            throw Invalid("Component name must be non-empty canonical text.");
        }

        if (schema.Version <= 0)
        {
            throw Invalid($"Component '{schema.Name}' version must be positive.");
        }
    }

    private static InvalidOperationException Invalid(string message)
    {
        return new InvalidOperationException($"[SceneComponentExtensions] {message}");
    }
}
