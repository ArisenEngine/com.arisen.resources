using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ArisenEngine.Core.Assets;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ArisenEngine.Resources.Serialization;

public readonly record struct WorldCellId(Guid Value) : IComparable<WorldCellId>
{
    public bool IsValid => Value != Guid.Empty;

    public int CompareTo(WorldCellId other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString("D");
}

public readonly record struct WorldCellCoordinate(int X, int Y, int Z);

public readonly record struct WorldCellKey(WorldCellCoordinate Coordinate, string Layer);

public readonly record struct WorldPosition(double X, double Y, double Z)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
}

public readonly record struct WorldBounds(WorldPosition Min, WorldPosition Max)
{
    public bool IsValid =>
        Min.IsFinite &&
        Max.IsFinite &&
        Min.X < Max.X &&
        Min.Y < Max.Y &&
        Min.Z < Max.Z;
}

public static class WorldPartitionCoordinates
{
    public static WorldCellCoordinate GetCoordinate(
        WorldPartitionSettings partition,
        WorldPosition position)
    {
        ArgumentNullException.ThrowIfNull(partition);
        if (!position.IsFinite)
        {
            throw new ArgumentException("World position must be finite.", nameof(position));
        }

        return new WorldCellCoordinate(
            ToCoordinate((position.X - partition.Origin.X) / partition.CellSize.X),
            ToCoordinate((position.Y - partition.Origin.Y) / partition.CellSize.Y),
            ToCoordinate((position.Z - partition.Origin.Z) / partition.CellSize.Z));
    }

    public static WorldPosition GetCellOrigin(
        WorldPartitionSettings partition,
        WorldCellCoordinate coordinate)
    {
        ArgumentNullException.ThrowIfNull(partition);
        WorldCellIdentity.ValidateCoordinate(coordinate);
        var result = new WorldPosition(
            partition.Origin.X + coordinate.X * partition.CellSize.X,
            partition.Origin.Y + coordinate.Y * partition.CellSize.Y,
            partition.Origin.Z + coordinate.Z * partition.CellSize.Z);
        if (!result.IsFinite)
        {
            throw new InvalidOperationException("World-cell origin is not finite.");
        }
        return result;
    }

    private static int ToCoordinate(double value)
    {
        double floored = Math.Floor(value);
        if (floored <= int.MinValue) return int.MinValue;
        if (floored >= int.MaxValue) return int.MaxValue;
        return (int)floored;
    }
}

public sealed record WorldPartitionSettings(
    WorldPosition Origin,
    WorldPosition CellSize,
    int LoadRadius,
    int UnloadHysteresis,
    int MaxActiveCells);

public enum WorldUnresolvedReferencePolicy
{
    KeepUnresolved = 1
}

public enum WorldUnloadedTargetPolicy
{
    ClearAndLateResolve = 1
}

public enum WorldDependencyCyclePolicy
{
    Reject = 1
}

public sealed record WorldStreamingPolicy(
    WorldUnresolvedReferencePolicy UnresolvedReferences,
    WorldUnloadedTargetPolicy UnloadedTargets,
    WorldDependencyCyclePolicy DependencyCycles);

public sealed record WorldLayerDescriptor(string Id, int Priority);

public sealed record WorldSceneReference(
    Guid Guid,
    string PackageId,
    string Variant);

public enum WorldEntityReferenceScope
{
    Persistent = 1,
    Cell = 2
}

public sealed record WorldEntityReferenceDescriptor(
    WorldCellId SourceCellId,
    Guid SourceEntityGuid,
    WorldEntityReferenceScope TargetScope,
    WorldCellId TargetCellId,
    Guid TargetEntityGuid,
    bool Required);

public sealed record WorldCellDescriptor(
    WorldCellId Id,
    WorldCellKey Key,
    WorldSceneReference Scene,
    WorldBounds Bounds,
    byte[] SceneContentHash,
    long ScenePayloadBytes,
    long EstimatedCpuBytes,
    long EstimatedGpuBytes,
    IReadOnlyList<WorldCellId> Neighbors,
    IReadOnlyList<WorldCellId> Dependencies);

public sealed record WorldDescriptor(
    Guid WorldGuid,
    int SourceSchemaVersion,
    string Name,
    WorldSceneReference PersistentScene,
    byte[] PersistentSceneContentHash,
    long PersistentScenePayloadBytes,
    WorldPartitionSettings Partition,
    WorldStreamingPolicy Policy,
    IReadOnlyList<WorldLayerDescriptor> Layers,
    IReadOnlyList<WorldCellDescriptor> Cells,
    IReadOnlyList<WorldEntityReferenceDescriptor> EntityReferences);

public readonly record struct WorldDescriptorLoadResult(
    bool Success,
    WorldDescriptor? Descriptor,
    string Diagnostic);

public static class WorldCellIdentity
{
    private const int MaxCoordinateMagnitude = 1_000_000;

    public static WorldCellId Create(Guid worldGuid, WorldCellCoordinate coordinate, string layer)
    {
        if (worldGuid == Guid.Empty)
        {
            throw new ArgumentException("World-cell identity requires a non-empty world GUID.", nameof(worldGuid));
        }

        string canonicalLayer = NormalizeLayer(layer);
        ValidateCoordinate(coordinate);
        string identityText = string.Create(
            CultureInfo.InvariantCulture,
            $"arisen.world-cell.v1|{worldGuid:N}|{coordinate.X}|{coordinate.Y}|{coordinate.Z}|{canonicalLayer}");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identityText));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new WorldCellId(new Guid(hash.AsSpan(0, 16), bigEndian: true));
    }

    public static string NormalizeLayer(string layer)
    {
        if (string.IsNullOrWhiteSpace(layer))
        {
            throw new ArgumentException("World-cell layer must be non-empty canonical text.", nameof(layer));
        }

        string canonical = layer.Trim().ToLowerInvariant();
        if (canonical.Length > 64 ||
            canonical.Any(character =>
                !(character is >= 'a' and <= 'z') &&
                !(character is >= '0' and <= '9') &&
                character != '.' &&
                character != '-' &&
                character != '_'))
        {
            throw new ArgumentException(
                $"World-cell layer '{layer}' must contain only ASCII letters, digits, '.', '-', or '_'.",
                nameof(layer));
        }

        return canonical;
    }

    internal static void ValidateCoordinate(WorldCellCoordinate coordinate)
    {
        if (Math.Abs((long)coordinate.X) > MaxCoordinateMagnitude ||
            Math.Abs((long)coordinate.Y) > MaxCoordinateMagnitude ||
            Math.Abs((long)coordinate.Z) > MaxCoordinateMagnitude)
        {
            throw new ArgumentOutOfRangeException(
                nameof(coordinate),
                $"World-cell coordinates must be within +/-{MaxCoordinateMagnitude}.");
        }
    }
}

public static class WorldDescriptorLoader
{
    public const int CurrentSourceSchemaVersion = 1;

    private const string WorldAssetType = "World";
    private const string SceneAssetType = "Scene";
    private const int MaxCellCount = 65_536;
    private const int MaxReferenceCount = 1_000_000;
    private const long MaxResidencyEstimate = 16L * 1024 * 1024 * 1024 * 1024;

    public static WorldDescriptorLoadResult LoadSource(
        IAssetDatabase assetDatabase,
        AssetRef<WorldSourceAsset> worldRef)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        if (!worldRef.IsValid)
        {
            return Failure("[WorldDescriptorLoader] World asset ref is empty.");
        }

        if (!assetDatabase.TryGetAsset(worldRef, out AssetRecord? worldAsset) ||
            !string.Equals(worldAsset.AssetType, WorldAssetType, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                $"[WorldDescriptorLoader] World asset '{worldRef.Guid:D}' is not indexed as '{WorldAssetType}'.");
        }

        if (!string.IsNullOrWhiteSpace(worldRef.PackageId) &&
            !string.Equals(worldRef.PackageId, worldAsset.PackageId, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                $"[WorldDescriptorLoader] World asset '{worldRef.Guid:D}' belongs to package " +
                $"'{worldAsset.PackageId}', expected '{worldRef.PackageId}'.");
        }

        if (!File.Exists(worldAsset.SourcePath))
        {
            return Failure(
                $"[WorldDescriptorLoader] World source is missing: {worldAsset.SourcePath}");
        }

        try
        {
            return LoadSourceText(
                assetDatabase,
                worldRef.Guid,
                worldAsset.SourcePath,
                File.ReadAllText(worldAsset.SourcePath));
        }
        catch (Exception ex)
        {
            return Failure(
                $"[WorldDescriptorLoader] Failed to read world '{worldAsset.SourcePath}': {ex.Message}");
        }
    }

    public static WorldDescriptorLoadResult LoadSourceText(
        IAssetDatabase assetDatabase,
        Guid expectedWorldGuid,
        string diagnosticPath,
        string sourceText)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        try
        {
            if (!TryDeserializeSource(diagnosticPath, sourceText, out WorldSourceDocument? source, out string diagnostic))
            {
                return Failure(diagnostic);
            }

            if (source!.Version != CurrentSourceSchemaVersion)
            {
                return Failure(
                    $"[WorldDescriptorLoader] World '{diagnosticPath}' schema version " +
                    $"'{source.Version}' is unsupported; expected '{CurrentSourceSchemaVersion}'.");
            }

            if (source.WorldGuid == Guid.Empty || source.WorldGuid != expectedWorldGuid)
            {
                return Failure(
                    $"[WorldDescriptorLoader] World '{diagnosticPath}' declares GUID " +
                    $"'{source.WorldGuid:D}', expected asset GUID '{expectedWorldGuid:D}'.");
            }

            if (string.IsNullOrWhiteSpace(source.Name) || source.Name.Length > 512)
            {
                return Failure(
                    $"[WorldDescriptorLoader] World '{diagnosticPath}' requires a name no longer than 512 characters.");
            }

            if (!TryCreatePartition(source.Partition, diagnosticPath, out WorldPartitionSettings partition, out diagnostic) ||
                !TryCreatePolicy(source.Policy, diagnosticPath, out WorldStreamingPolicy policy, out diagnostic) ||
                !TryCreateLayers(source.Layers, diagnosticPath, out WorldLayerDescriptor[] layers, out diagnostic) ||
                !TryResolveScene(assetDatabase, source.PersistentScene, diagnosticPath, "persistent scene", out WorldSceneReference persistentScene, out diagnostic))
            {
                return Failure(diagnostic);
            }

            if (source.Cells.Count == 0 || source.Cells.Count > MaxCellCount)
            {
                return Failure(
                    $"[WorldDescriptorLoader] World '{diagnosticPath}' cell count '{source.Cells.Count}' " +
                    $"must be between 1 and {MaxCellCount}.");
            }

            var layerIds = new HashSet<string>(layers.Select(layer => layer.Id), StringComparer.Ordinal);
            var cellsByKey = new Dictionary<WorldCellKey, WorldCellBuild>();
            var cellsById = new Dictionary<WorldCellId, WorldCellBuild>();
            for (int index = 0; index < source.Cells.Count; index++)
            {
                WorldCellSource cellSource = source.Cells[index];
                if (!TryCreateCell(
                        assetDatabase,
                        source.WorldGuid,
                        cellSource,
                        index,
                        diagnosticPath,
                        layerIds,
                        out WorldCellBuild cell,
                        out diagnostic))
                {
                    return Failure(diagnostic);
                }

                if (!cellsByKey.TryAdd(cell.Key, cell) || !cellsById.TryAdd(cell.Id, cell))
                {
                    return Failure(
                        $"[WorldDescriptorLoader] World '{diagnosticPath}' contains duplicate cell identity " +
                        $"'{cell.Id}' for ({cell.Key.Coordinate.X}, {cell.Key.Coordinate.Y}, " +
                        $"{cell.Key.Coordinate.Z}, {cell.Key.Layer}).");
                }
            }

            if (!TryValidateNonOverlappingBounds(cellsById.Values, diagnosticPath, out diagnostic))
            {
                return Failure(diagnostic);
            }

            var sceneEntityGuids = new Dictionary<Guid, HashSet<Guid>>();
            if (!TryGetSceneEntityGuids(
                    assetDatabase,
                    persistentScene,
                    diagnosticPath,
                    sceneEntityGuids,
                    out HashSet<Guid> persistentEntities,
                    out diagnostic))
            {
                return Failure(diagnostic);
            }

            foreach (WorldCellBuild cell in cellsById.Values)
            {
                if (!TryGetSceneEntityGuids(
                        assetDatabase,
                        cell.Scene,
                        diagnosticPath,
                        sceneEntityGuids,
                        out HashSet<Guid> cellEntities,
                        out diagnostic))
                {
                    return Failure(diagnostic);
                }

                cell.EntityGuids = cellEntities;
                if (!TryResolveDependencies(cell, cellsByKey, diagnosticPath, out diagnostic))
                {
                    return Failure(diagnostic);
                }
            }

            var references = new List<WorldEntityReferenceDescriptor>();
            var referenceKeys = new HashSet<WorldEntityReferenceKey>();
            foreach (WorldCellBuild cell in cellsById.Values)
            {
                if (!TryResolveEntityReferences(
                        cell,
                        cellsByKey,
                        persistentEntities,
                        references,
                        referenceKeys,
                        diagnosticPath,
                        out diagnostic))
                {
                    return Failure(diagnostic);
                }
            }

            if (references.Count > MaxReferenceCount)
            {
                return Failure(
                    $"[WorldDescriptorLoader] World '{diagnosticPath}' reference count " +
                    $"'{references.Count}' exceeds {MaxReferenceCount}.");
            }

            if (!TryValidateAcyclicDependencies(cellsById.Values, diagnosticPath, out diagnostic))
            {
                return Failure(diagnostic);
            }

            PopulateNeighbors(cellsByKey);
            WorldCellDescriptor[] cells = cellsById.Values
                .OrderBy(cell => cell.Id)
                .Select(cell => cell.ToDescriptor())
                .ToArray();
            references.Sort(CompareReferences);
            return new WorldDescriptorLoadResult(
                true,
                new WorldDescriptor(
                    source.WorldGuid,
                    source.Version,
                    source.Name.Trim(),
                    persistentScene,
                    Array.Empty<byte>(),
                    0,
                    partition,
                    policy,
                    layers,
                    cells,
                    references),
                string.Empty);
        }
        catch (Exception ex)
        {
            return Failure(
                $"[WorldDescriptorLoader] World '{diagnosticPath}' validation failed: {ex.Message}");
        }
    }

    private static bool TryDeserializeSource(
        string path,
        string sourceText,
        out WorldSourceDocument? source,
        out string diagnostic)
    {
        try
        {
            var stream = new YamlStream();
            using (var reader = new StringReader(sourceText))
            {
                stream.Load(reader);
            }

            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                source = null;
                diagnostic = $"[WorldDescriptorLoader] World '{path}' must contain one YAML mapping document.";
                return false;
            }

            ValidateSourceShape(root, path);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(PascalCaseNamingConvention.Instance)
                .Build();
            source = deserializer.Deserialize<WorldSourceDocument>(sourceText);
            if (source == null)
            {
                diagnostic = $"[WorldDescriptorLoader] World '{path}' deserialized to an empty document.";
                return false;
            }

            diagnostic = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            source = null;
            diagnostic = $"[WorldDescriptorLoader] Failed to parse world '{path}': {ex.Message}";
            return false;
        }
    }

    private static bool TryCreatePartition(
        WorldPartitionSource? source,
        string path,
        out WorldPartitionSettings partition,
        out string diagnostic)
    {
        partition = null!;
        if (source?.Origin == null || source.CellSize == null)
        {
            diagnostic = $"[WorldDescriptorLoader] World '{path}' requires Partition.Origin and Partition.CellSize.";
            return false;
        }

        WorldPosition origin = source.Origin.ToPosition();
        WorldPosition cellSize = source.CellSize.ToPosition();
        if (!origin.IsFinite || !cellSize.IsFinite ||
            cellSize.X <= 0 || cellSize.Y <= 0 || cellSize.Z <= 0 ||
            source.LoadRadius < 0 || source.LoadRadius > 1024 ||
            source.UnloadHysteresis < 0 || source.UnloadHysteresis > 1024 ||
            source.MaxActiveCells <= 0 || source.MaxActiveCells > MaxCellCount)
        {
            diagnostic =
                $"[WorldDescriptorLoader] World '{path}' has invalid partition origin, cell size, radius, hysteresis, or active-cell limit.";
            return false;
        }

        partition = new WorldPartitionSettings(
            origin,
            cellSize,
            source.LoadRadius,
            source.UnloadHysteresis,
            source.MaxActiveCells);
        diagnostic = string.Empty;
        return true;
    }

    private static bool TryCreatePolicy(
        WorldPolicySource? source,
        string path,
        out WorldStreamingPolicy policy,
        out string diagnostic)
    {
        policy = null!;
        if (source == null ||
            !Enum.TryParse(source.UnresolvedReferences, ignoreCase: true, out WorldUnresolvedReferencePolicy unresolved) ||
            unresolved != WorldUnresolvedReferencePolicy.KeepUnresolved ||
            !Enum.TryParse(source.UnloadedTargets, ignoreCase: true, out WorldUnloadedTargetPolicy unloaded) ||
            unloaded != WorldUnloadedTargetPolicy.ClearAndLateResolve ||
            !Enum.TryParse(source.DependencyCycles, ignoreCase: true, out WorldDependencyCyclePolicy cycles) ||
            cycles != WorldDependencyCyclePolicy.Reject)
        {
            diagnostic =
                $"[WorldDescriptorLoader] World '{path}' policy must use KeepUnresolved, ClearAndLateResolve, and Reject.";
            return false;
        }

        policy = new WorldStreamingPolicy(unresolved, unloaded, cycles);
        diagnostic = string.Empty;
        return true;
    }

    private static bool TryCreateLayers(
        IReadOnlyList<WorldLayerSource> sources,
        string path,
        out WorldLayerDescriptor[] layers,
        out string diagnostic)
    {
        layers = Array.Empty<WorldLayerDescriptor>();
        if (sources.Count == 0 || sources.Count > 256)
        {
            diagnostic = $"[WorldDescriptorLoader] World '{path}' must declare between 1 and 256 layers.";
            return false;
        }

        var output = new List<WorldLayerDescriptor>(sources.Count);
        var unique = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < sources.Count; index++)
        {
            string id = WorldCellIdentity.NormalizeLayer(sources[index].Id);
            if (!unique.Add(id))
            {
                diagnostic = $"[WorldDescriptorLoader] World '{path}' declares duplicate layer '{id}'.";
                return false;
            }

            output.Add(new WorldLayerDescriptor(id, sources[index].Priority));
        }

        layers = output.OrderBy(layer => layer.Id, StringComparer.Ordinal).ToArray();
        diagnostic = string.Empty;
        return true;
    }

    private static bool TryCreateCell(
        IAssetDatabase assetDatabase,
        Guid worldGuid,
        WorldCellSource source,
        int index,
        string path,
        IReadOnlySet<string> layerIds,
        out WorldCellBuild cell,
        out string diagnostic)
    {
        cell = null!;
        if (source.Coordinate == null || source.Bounds?.Min == null || source.Bounds.Max == null)
        {
            diagnostic = $"[WorldDescriptorLoader] World '{path}' cell {index} requires Coordinate and Bounds.Min/Max.";
            return false;
        }

        var coordinate = new WorldCellCoordinate(
            source.Coordinate.X,
            source.Coordinate.Y,
            source.Coordinate.Z);
        WorldCellIdentity.ValidateCoordinate(coordinate);
        string layer = WorldCellIdentity.NormalizeLayer(source.Layer);
        if (!layerIds.Contains(layer))
        {
            diagnostic = $"[WorldDescriptorLoader] World '{path}' cell {index} uses undeclared layer '{layer}'.";
            return false;
        }

        var key = new WorldCellKey(coordinate, layer);
        WorldCellId id = WorldCellIdentity.Create(worldGuid, coordinate, layer);
        var bounds = new WorldBounds(source.Bounds.Min.ToPosition(), source.Bounds.Max.ToPosition());
        if (!bounds.IsValid)
        {
            diagnostic = $"[WorldDescriptorLoader] World '{path}' cell '{id}' has non-finite or inverted bounds.";
            return false;
        }

        if (source.EstimatedCpuBytes < 0 || source.EstimatedCpuBytes > MaxResidencyEstimate ||
            source.EstimatedGpuBytes < 0 || source.EstimatedGpuBytes > MaxResidencyEstimate)
        {
            diagnostic = $"[WorldDescriptorLoader] World '{path}' cell '{id}' has an invalid residency estimate.";
            return false;
        }

        if (!TryResolveScene(assetDatabase, source.Scene, path, $"cell '{id}' scene", out WorldSceneReference scene, out diagnostic))
        {
            return false;
        }

        cell = new WorldCellBuild(
            id,
            key,
            scene,
            bounds,
            source.EstimatedCpuBytes,
            source.EstimatedGpuBytes,
            source.Dependencies,
            source.References);
        diagnostic = string.Empty;
        return true;
    }

    private static bool TryResolveScene(
        IAssetDatabase assetDatabase,
        WorldSceneReferenceSource? source,
        string path,
        string context,
        out WorldSceneReference scene,
        out string diagnostic)
    {
        scene = null!;
        if (source == null || source.Guid == Guid.Empty || string.IsNullOrWhiteSpace(source.PackageId))
        {
            diagnostic = $"[WorldDescriptorLoader] World '{path}' {context} requires Guid and PackageId.";
            return false;
        }

        if (!assetDatabase.TryGetAsset(source.Guid, out AssetRecord? asset) ||
            !string.Equals(asset.AssetType, SceneAssetType, StringComparison.OrdinalIgnoreCase))
        {
            diagnostic =
                $"[WorldDescriptorLoader] World '{path}' {context} references missing or non-scene asset '{source.Guid:D}'.";
            return false;
        }

        if (!string.Equals(asset.PackageId, source.PackageId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            diagnostic =
                $"[WorldDescriptorLoader] World '{path}' {context} scene '{source.Guid:D}' belongs to package " +
                $"'{asset.PackageId}', not '{source.PackageId}'.";
            return false;
        }

        scene = new WorldSceneReference(
            source.Guid,
            asset.PackageId,
            SceneAssetCooker.RuntimeVariant);
        diagnostic = string.Empty;
        return true;
    }

    private static bool TryValidateNonOverlappingBounds(
        IEnumerable<WorldCellBuild> cells,
        string path,
        out string diagnostic)
    {
        foreach (IGrouping<string, WorldCellBuild> layer in cells.GroupBy(cell => cell.Key.Layer, StringComparer.Ordinal))
        {
            WorldCellBuild[] sorted = layer.OrderBy(cell => cell.Bounds.Min.X).ThenBy(cell => cell.Id).ToArray();
            for (int leftIndex = 0; leftIndex < sorted.Length; leftIndex++)
            {
                WorldCellBuild left = sorted[leftIndex];
                for (int rightIndex = leftIndex + 1;
                     rightIndex < sorted.Length && sorted[rightIndex].Bounds.Min.X < left.Bounds.Max.X;
                     rightIndex++)
                {
                    WorldCellBuild right = sorted[rightIndex];
                    if (Overlaps(left.Bounds, right.Bounds))
                    {
                        diagnostic =
                            $"[WorldDescriptorLoader] World '{path}' cells '{left.Id}' and '{right.Id}' " +
                            $"have overlapping bounds in layer '{layer.Key}'.";
                        return false;
                    }
                }
            }
        }

        diagnostic = string.Empty;
        return true;
    }

    private static bool TryGetSceneEntityGuids(
        IAssetDatabase assetDatabase,
        WorldSceneReference scene,
        string path,
        IDictionary<Guid, HashSet<Guid>> cache,
        out HashSet<Guid> entityGuids,
        out string diagnostic)
    {
        if (cache.TryGetValue(scene.Guid, out entityGuids!))
        {
            diagnostic = string.Empty;
            return true;
        }

        SceneInspectionResult inspection = SceneAssetLoader.InspectScene(
            assetDatabase,
            new AssetRef<SceneSourceAsset>(scene.Guid, SceneAssetType, scene.PackageId));
        if (!inspection.Success)
        {
            entityGuids = null!;
            diagnostic =
                $"[WorldDescriptorLoader] World '{path}' scene '{scene.Guid:D}' is invalid: {inspection.Diagnostic}";
            return false;
        }

        entityGuids = inspection.Entities.Select(entity => entity.AuthoringGuid).ToHashSet();
        cache.Add(scene.Guid, entityGuids);
        diagnostic = string.Empty;
        return true;
    }

    private static bool TryResolveDependencies(
        WorldCellBuild cell,
        IReadOnlyDictionary<WorldCellKey, WorldCellBuild> cellsByKey,
        string path,
        out string diagnostic)
    {
        for (int index = 0; index < cell.DependencySources.Count; index++)
        {
            WorldCellKey dependencyKey = CreateKey(cell.DependencySources[index]);
            if (!cellsByKey.TryGetValue(dependencyKey, out WorldCellBuild? dependency))
            {
                diagnostic =
                    $"[WorldDescriptorLoader] World '{path}' cell '{cell.Id}' references undeclared dependency " +
                    $"({dependencyKey.Coordinate.X}, {dependencyKey.Coordinate.Y}, {dependencyKey.Coordinate.Z}, {dependencyKey.Layer}).";
                return false;
            }

            if (dependency.Id == cell.Id || !cell.Dependencies.Add(dependency.Id))
            {
                diagnostic =
                    $"[WorldDescriptorLoader] World '{path}' cell '{cell.Id}' has a self or duplicate dependency '{dependency.Id}'.";
                return false;
            }
        }

        diagnostic = string.Empty;
        return true;
    }

    private static bool TryResolveEntityReferences(
        WorldCellBuild sourceCell,
        IReadOnlyDictionary<WorldCellKey, WorldCellBuild> cellsByKey,
        IReadOnlySet<Guid> persistentEntities,
        ICollection<WorldEntityReferenceDescriptor> output,
        ISet<WorldEntityReferenceKey> unique,
        string path,
        out string diagnostic)
    {
        for (int index = 0; index < sourceCell.ReferenceSources.Count; index++)
        {
            WorldEntityReferenceSource source = sourceCell.ReferenceSources[index];
            if (source.SourceEntityGuid == Guid.Empty || !sourceCell.EntityGuids.Contains(source.SourceEntityGuid) ||
                source.Target == null || source.Target.EntityGuid == Guid.Empty)
            {
                diagnostic =
                    $"[WorldDescriptorLoader] World '{path}' cell '{sourceCell.Id}' reference {index} has an " +
                    "empty or undeclared source/target entity identity.";
                return false;
            }

            if (!Enum.TryParse(source.Target.Scope, ignoreCase: true, out WorldEntityReferenceScope scope) ||
                scope is not (WorldEntityReferenceScope.Persistent or WorldEntityReferenceScope.Cell))
            {
                diagnostic =
                    $"[WorldDescriptorLoader] World '{path}' cell '{sourceCell.Id}' reference {index} " +
                    "target Scope must be Persistent or Cell.";
                return false;
            }

            WorldCellId targetCellId = default;
            IReadOnlySet<Guid> targetEntities;
            if (scope == WorldEntityReferenceScope.Persistent)
            {
                if (source.Target.Coordinate != null || !string.IsNullOrWhiteSpace(source.Target.Layer))
                {
                    diagnostic =
                        $"[WorldDescriptorLoader] World '{path}' persistent reference target must not specify a cell.";
                    return false;
                }

                targetEntities = persistentEntities;
            }
            else
            {
                if (source.Target.Coordinate == null)
                {
                    diagnostic =
                        $"[WorldDescriptorLoader] World '{path}' cell reference target requires Coordinate and Layer.";
                    return false;
                }

                WorldCellKey targetKey = CreateKey(source.Target);
                if (!cellsByKey.TryGetValue(targetKey, out WorldCellBuild? targetCell) || targetCell.Id == sourceCell.Id)
                {
                    diagnostic =
                        $"[WorldDescriptorLoader] World '{path}' cell '{sourceCell.Id}' reference {index} " +
                        "targets an undeclared or same-cell identity.";
                    return false;
                }

                targetCellId = targetCell.Id;
                targetEntities = targetCell.EntityGuids;
                if (source.Required)
                {
                    sourceCell.Dependencies.Add(targetCell.Id);
                }
            }

            if (!targetEntities.Contains(source.Target.EntityGuid))
            {
                diagnostic =
                    $"[WorldDescriptorLoader] World '{path}' cell '{sourceCell.Id}' reference {index} " +
                    $"targets undeclared entity '{source.Target.EntityGuid:D}'.";
                return false;
            }

            var reference = new WorldEntityReferenceDescriptor(
                sourceCell.Id,
                source.SourceEntityGuid,
                scope,
                targetCellId,
                source.Target.EntityGuid,
                source.Required);
            var key = new WorldEntityReferenceKey(
                reference.SourceCellId,
                reference.SourceEntityGuid,
                reference.TargetScope,
                reference.TargetCellId,
                reference.TargetEntityGuid);
            if (!unique.Add(key))
            {
                diagnostic =
                    $"[WorldDescriptorLoader] World '{path}' contains duplicate world-entity reference at cell '{sourceCell.Id}'.";
                return false;
            }

            output.Add(reference);
        }

        diagnostic = string.Empty;
        return true;
    }

    private static bool TryValidateAcyclicDependencies(
        IEnumerable<WorldCellBuild> cells,
        string path,
        out string diagnostic)
    {
        WorldCellBuild[] all = cells.OrderBy(cell => cell.Id).ToArray();
        var dependentCounts = all.ToDictionary(cell => cell.Id, _ => 0);
        var dependents = all.ToDictionary(cell => cell.Id, _ => new List<WorldCellId>());
        foreach (WorldCellBuild cell in all)
        {
            dependentCounts[cell.Id] = cell.Dependencies.Count;
            foreach (WorldCellId dependency in cell.Dependencies)
            {
                dependents[dependency].Add(cell.Id);
            }
        }

        var ready = new SortedSet<WorldCellId>(dependentCounts.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        int visited = 0;
        while (ready.Count > 0)
        {
            WorldCellId id = ready.Min;
            ready.Remove(id);
            visited++;
            foreach (WorldCellId dependent in dependents[id].OrderBy(value => value))
            {
                if (--dependentCounts[dependent] == 0)
                {
                    ready.Add(dependent);
                }
            }
        }

        if (visited != all.Length)
        {
            string cycleMembers = string.Join(
                ", ",
                dependentCounts.Where(pair => pair.Value > 0).Select(pair => pair.Key).OrderBy(value => value));
            diagnostic =
                $"[WorldDescriptorLoader] World '{path}' dependency cycle policy is Reject; cyclic cells: {cycleMembers}.";
            return false;
        }

        diagnostic = string.Empty;
        return true;
    }

    private static void PopulateNeighbors(IReadOnlyDictionary<WorldCellKey, WorldCellBuild> cellsByKey)
    {
        ReadOnlySpan<WorldCellCoordinate> offsets =
        [
            new(-1, 0, 0), new(1, 0, 0),
            new(0, -1, 0), new(0, 1, 0),
            new(0, 0, -1), new(0, 0, 1)
        ];
        foreach (WorldCellBuild cell in cellsByKey.Values)
        {
            foreach (WorldCellCoordinate offset in offsets)
            {
                long x = (long)cell.Key.Coordinate.X + offset.X;
                long y = (long)cell.Key.Coordinate.Y + offset.Y;
                long z = (long)cell.Key.Coordinate.Z + offset.Z;
                if (x is < int.MinValue or > int.MaxValue ||
                    y is < int.MinValue or > int.MaxValue ||
                    z is < int.MinValue or > int.MaxValue)
                {
                    continue;
                }

                var key = new WorldCellKey(new WorldCellCoordinate((int)x, (int)y, (int)z), cell.Key.Layer);
                if (cellsByKey.TryGetValue(key, out WorldCellBuild? neighbor))
                {
                    cell.Neighbors.Add(neighbor.Id);
                }
            }
        }
    }

    private static WorldCellKey CreateKey(WorldCellDependencySource source)
    {
        if (source.Coordinate == null)
        {
            throw new InvalidDataException("World-cell dependency requires Coordinate and Layer.");
        }

        var coordinate = new WorldCellCoordinate(
            source.Coordinate.X,
            source.Coordinate.Y,
            source.Coordinate.Z);
        WorldCellIdentity.ValidateCoordinate(coordinate);
        return new WorldCellKey(coordinate, WorldCellIdentity.NormalizeLayer(source.Layer));
    }

    private static WorldCellKey CreateKey(WorldEntityReferenceTargetSource source)
    {
        if (source.Coordinate == null)
        {
            throw new InvalidDataException("World-entity cell target requires Coordinate and Layer.");
        }

        var coordinate = new WorldCellCoordinate(
            source.Coordinate.X,
            source.Coordinate.Y,
            source.Coordinate.Z);
        WorldCellIdentity.ValidateCoordinate(coordinate);
        return new WorldCellKey(coordinate, WorldCellIdentity.NormalizeLayer(source.Layer));
    }

    private static int CompareReferences(
        WorldEntityReferenceDescriptor left,
        WorldEntityReferenceDescriptor right)
    {
        int result = left.SourceCellId.CompareTo(right.SourceCellId);
        if (result != 0) return result;
        result = left.SourceEntityGuid.CompareTo(right.SourceEntityGuid);
        if (result != 0) return result;
        result = left.TargetScope.CompareTo(right.TargetScope);
        if (result != 0) return result;
        result = left.TargetCellId.CompareTo(right.TargetCellId);
        if (result != 0) return result;
        result = left.TargetEntityGuid.CompareTo(right.TargetEntityGuid);
        return result != 0 ? result : left.Required.CompareTo(right.Required);
    }

    private static bool Overlaps(WorldBounds left, WorldBounds right)
    {
        return left.Min.X < right.Max.X && left.Max.X > right.Min.X &&
               left.Min.Y < right.Max.Y && left.Max.Y > right.Min.Y &&
               left.Min.Z < right.Max.Z && left.Max.Z > right.Min.Z;
    }

    private static WorldDescriptorLoadResult Failure(string diagnostic)
    {
        return new WorldDescriptorLoadResult(false, null, diagnostic);
    }

    private static void ValidateSourceShape(YamlMappingNode root, string path)
    {
        ValidateMapping(root, path, "world", "Version", "WorldGuid", "Name", "PersistentScene", "Partition", "Policy", "Layers", "Cells");
        ValidateChildMapping(root, "PersistentScene", path, "persistent scene", "Guid", "PackageId");
        ValidateChildMapping(root, "Partition", path, "partition", "Origin", "CellSize", "LoadRadius", "UnloadHysteresis", "MaxActiveCells");
        ValidateNestedVector(root, "Partition", "Origin", path);
        ValidateNestedVector(root, "Partition", "CellSize", path);
        ValidateChildMapping(root, "Policy", path, "policy", "UnresolvedReferences", "UnloadedTargets", "DependencyCycles");
        ValidateSequence(root, "Layers", path, "layers", layer =>
            ValidateMapping(layer, path, "layer", "Id", "Priority"));
        ValidateSequence(root, "Cells", path, "cells", cell =>
        {
            ValidateMapping(cell, path, "cell", "Coordinate", "Layer", "Scene", "Bounds", "Dependencies", "References", "EstimatedCpuBytes", "EstimatedGpuBytes");
            ValidateChildMapping(cell, "Coordinate", path, "cell coordinate", "X", "Y", "Z");
            ValidateChildMapping(cell, "Scene", path, "cell scene", "Guid", "PackageId");
            ValidateChildMapping(cell, "Bounds", path, "cell bounds", "Min", "Max");
            ValidateNestedVector(cell, "Bounds", "Min", path);
            ValidateNestedVector(cell, "Bounds", "Max", path);
            ValidateOptionalSequence(cell, "Dependencies", path, "cell dependencies", dependency =>
            {
                ValidateMapping(dependency, path, "cell dependency", "Coordinate", "Layer");
                ValidateChildMapping(dependency, "Coordinate", path, "dependency coordinate", "X", "Y", "Z");
            });
            ValidateOptionalSequence(cell, "References", path, "cell references", reference =>
            {
                ValidateMapping(reference, path, "world entity reference", "SourceEntityGuid", "Target", "Required");
                ValidateChildMapping(reference, "Target", path, "world entity target", "Scope", "Coordinate", "Layer", "EntityGuid");
                if (TryGetMapping(reference, "Target", out YamlMappingNode target) && HasKey(target, "Coordinate"))
                {
                    ValidateChildMapping(target, "Coordinate", path, "target coordinate", "X", "Y", "Z");
                }
            });
        });
    }

    private static void ValidateNestedVector(YamlMappingNode root, string parentKey, string vectorKey, string path)
    {
        if (!TryGetMapping(root, parentKey, out YamlMappingNode parent))
        {
            throw new InvalidDataException($"World '{path}' requires mapping '{parentKey}'.");
        }

        ValidateChildMapping(parent, vectorKey, path, vectorKey, "X", "Y", "Z");
    }

    private static void ValidateChildMapping(
        YamlMappingNode root,
        string key,
        string path,
        string context,
        params string[] allowed)
    {
        if (!TryGetMapping(root, key, out YamlMappingNode child))
        {
            throw new InvalidDataException($"World '{path}' requires {context} mapping '{key}'.");
        }

        ValidateMapping(child, path, context, allowed);
    }

    private static void ValidateSequence(
        YamlMappingNode root,
        string key,
        string path,
        string context,
        Action<YamlMappingNode> validate)
    {
        if (!TryGetNode(root, key, out YamlNode node) || node is not YamlSequenceNode sequence)
        {
            throw new InvalidDataException($"World '{path}' requires {context} sequence '{key}'.");
        }

        foreach (YamlNode item in sequence.Children)
        {
            if (item is not YamlMappingNode mapping)
            {
                throw new InvalidDataException($"World '{path}' {context} entries must be mappings.");
            }

            validate(mapping);
        }
    }

    private static void ValidateOptionalSequence(
        YamlMappingNode root,
        string key,
        string path,
        string context,
        Action<YamlMappingNode> validate)
    {
        if (!TryGetNode(root, key, out _))
        {
            return;
        }

        ValidateSequence(root, key, path, context, validate);
    }

    private static void ValidateMapping(
        YamlMappingNode mapping,
        string path,
        string context,
        params string[] allowed)
    {
        var allowedKeys = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((YamlNode keyNode, _) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value))
            {
                throw new InvalidDataException($"World '{path}' {context} contains a non-scalar key.");
            }

            if (!observed.Add(scalar.Value))
            {
                throw new InvalidDataException($"World '{path}' {context} contains duplicate key '{scalar.Value}'.");
            }

            if (!allowedKeys.Contains(scalar.Value))
            {
                throw new InvalidDataException($"World '{path}' {context} contains unknown field '{scalar.Value}'.");
            }
        }
    }

    private static bool TryGetMapping(YamlMappingNode root, string key, out YamlMappingNode mapping)
    {
        if (TryGetNode(root, key, out YamlNode node) && node is YamlMappingNode found)
        {
            mapping = found;
            return true;
        }

        mapping = null!;
        return false;
    }

    private static bool HasKey(YamlMappingNode root, string key) => TryGetNode(root, key, out _);

    private static bool TryGetNode(YamlMappingNode root, string key, out YamlNode node)
    {
        foreach ((YamlNode keyNode, YamlNode value) in root.Children)
        {
            if (keyNode is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                node = value;
                return true;
            }
        }

        node = null!;
        return false;
    }

    private sealed class WorldCellBuild
    {
        public WorldCellBuild(
            WorldCellId id,
            WorldCellKey key,
            WorldSceneReference scene,
            WorldBounds bounds,
            long estimatedCpuBytes,
            long estimatedGpuBytes,
            IReadOnlyList<WorldCellDependencySource> dependencySources,
            IReadOnlyList<WorldEntityReferenceSource> referenceSources)
        {
            Id = id;
            Key = key;
            Scene = scene;
            Bounds = bounds;
            EstimatedCpuBytes = estimatedCpuBytes;
            EstimatedGpuBytes = estimatedGpuBytes;
            DependencySources = dependencySources;
            ReferenceSources = referenceSources;
        }

        public WorldCellId Id { get; }
        public WorldCellKey Key { get; }
        public WorldSceneReference Scene { get; }
        public WorldBounds Bounds { get; }
        public long EstimatedCpuBytes { get; }
        public long EstimatedGpuBytes { get; }
        public IReadOnlyList<WorldCellDependencySource> DependencySources { get; }
        public IReadOnlyList<WorldEntityReferenceSource> ReferenceSources { get; }
        public HashSet<Guid> EntityGuids { get; set; } = new();
        public SortedSet<WorldCellId> Dependencies { get; } = new();
        public SortedSet<WorldCellId> Neighbors { get; } = new();

        public WorldCellDescriptor ToDescriptor()
        {
            return new WorldCellDescriptor(
                Id,
                Key,
                Scene,
                Bounds,
                Array.Empty<byte>(),
                0,
                EstimatedCpuBytes,
                EstimatedGpuBytes,
                Neighbors.ToArray(),
                Dependencies.ToArray());
        }
    }

    private readonly record struct WorldEntityReferenceKey(
        WorldCellId SourceCellId,
        Guid SourceEntityGuid,
        WorldEntityReferenceScope TargetScope,
        WorldCellId TargetCellId,
        Guid TargetEntityGuid);
}

internal sealed class WorldSourceDocument
{
    public int Version { get; set; }
    public Guid WorldGuid { get; set; }
    public string Name { get; set; } = string.Empty;
    public WorldSceneReferenceSource? PersistentScene { get; set; }
    public WorldPartitionSource? Partition { get; set; }
    public WorldPolicySource? Policy { get; set; }
    public List<WorldLayerSource> Layers { get; set; } = new();
    public List<WorldCellSource> Cells { get; set; } = new();
}

internal sealed class WorldSceneReferenceSource
{
    public Guid Guid { get; set; }
    public string PackageId { get; set; } = string.Empty;
}

internal sealed class WorldPartitionSource
{
    public WorldVector3Source? Origin { get; set; }
    public WorldVector3Source? CellSize { get; set; }
    public int LoadRadius { get; set; } = 2;
    public int UnloadHysteresis { get; set; } = 1;
    public int MaxActiveCells { get; set; } = 64;
}

internal sealed class WorldPolicySource
{
    public string UnresolvedReferences { get; set; } = nameof(WorldUnresolvedReferencePolicy.KeepUnresolved);
    public string UnloadedTargets { get; set; } = nameof(WorldUnloadedTargetPolicy.ClearAndLateResolve);
    public string DependencyCycles { get; set; } = nameof(WorldDependencyCyclePolicy.Reject);
}

internal sealed class WorldLayerSource
{
    public string Id { get; set; } = string.Empty;
    public int Priority { get; set; }
}

internal sealed class WorldCellSource
{
    public WorldCoordinateSource? Coordinate { get; set; }
    public string Layer { get; set; } = string.Empty;
    public WorldSceneReferenceSource? Scene { get; set; }
    public WorldBoundsSource? Bounds { get; set; }
    public List<WorldCellDependencySource> Dependencies { get; set; } = new();
    public List<WorldEntityReferenceSource> References { get; set; } = new();
    public long EstimatedCpuBytes { get; set; }
    public long EstimatedGpuBytes { get; set; }
}

internal sealed class WorldCellDependencySource
{
    public WorldCoordinateSource? Coordinate { get; set; }
    public string Layer { get; set; } = string.Empty;
}

internal sealed class WorldEntityReferenceSource
{
    public Guid SourceEntityGuid { get; set; }
    public WorldEntityReferenceTargetSource? Target { get; set; }
    public bool Required { get; set; }
}

internal sealed class WorldEntityReferenceTargetSource
{
    public string Scope { get; set; } = string.Empty;
    public WorldCoordinateSource? Coordinate { get; set; }
    public string Layer { get; set; } = string.Empty;
    public Guid EntityGuid { get; set; }
}

internal sealed class WorldBoundsSource
{
    public WorldVector3Source? Min { get; set; }
    public WorldVector3Source? Max { get; set; }
}

internal sealed class WorldCoordinateSource
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
}

internal sealed class WorldVector3Source
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }

    public WorldPosition ToPosition() => new(X, Y, Z);
}
