using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;

namespace ArisenEngine.Resources.Serialization;

public readonly record struct CookedWorldDependency(
    Guid Guid,
    string PackageId,
    string AssetType,
    string Variant,
    bool Required);

public sealed record CookedWorldArtifact(
    Guid WorldGuid,
    string Variant,
    string Path,
    int CellCount,
    int EntityReferenceCount,
    long SizeInBytes,
    IReadOnlyList<CookedWorldDependency> Dependencies);

public static class WorldAssetCooker
{
    public const string RuntimeVariant = "runtime.world.v1";
    public const string CookedExtension = ".ariworld";
    public const int CookedFormatVersion = 2;

    internal const int HeaderSize = 96;
    internal const int HashOffset = 64;
    internal const int HashSize = 32;

    private const string WorldAssetType = "World";
    private const string SceneAssetType = "Scene";
    private const uint EndianMarker = 0x01020304;
    private const int MaxCookedWorldBytes = 256 * 1024 * 1024;
    private const int MaxCellCount = 65_536;
    private const int MaxReferenceCount = 1_000_000;
    private const int MaxStringBytes = 16 * 1024;
    private static readonly byte[] s_Magic = Encoding.ASCII.GetBytes("ARIWORLD");
    private static readonly UTF8Encoding s_StrictUtf8 = new(false, true);

    public static CookedWorldArtifact Cook(
        IAssetDatabase assetDatabase,
        AssetRef<WorldSourceAsset> worldRef)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        if (!worldRef.IsValid)
        {
            throw new ArgumentException("World cooking requires a valid asset reference.", nameof(worldRef));
        }

        if (!assetDatabase.TryGetAsset(worldRef, out AssetRecord? worldAsset) ||
            !string.Equals(worldAsset.AssetType, WorldAssetType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[WorldAssetCooker] World asset '{worldRef.Guid:D}' is not indexed as '{WorldAssetType}'.");
        }

        WorldDescriptorLoadResult loaded = WorldDescriptorLoader.LoadSource(assetDatabase, worldRef);
        if (!loaded.Success || loaded.Descriptor == null)
        {
            throw new InvalidOperationException(loaded.Diagnostic);
        }

        using var _ = Profiler.Zone("WorldAssetCooker.Cook");
        var scenes = new Dictionary<Guid, CookedSceneInfo>();
        CookedSceneInfo persistentInfo = CookScene(assetDatabase, loaded.Descriptor.PersistentScene, scenes);
        WorldCellDescriptor[] cells = new WorldCellDescriptor[loaded.Descriptor.Cells.Count];
        for (int index = 0; index < cells.Length; index++)
        {
            WorldCellDescriptor source = loaded.Descriptor.Cells[index];
            CookedSceneInfo scene = CookScene(assetDatabase, source.Scene, scenes);
            cells[index] = source with
            {
                SceneContentHash = scene.Hash.ToArray(),
                ScenePayloadBytes = scene.SizeInBytes,
                EstimatedCpuBytes = Math.Max(source.EstimatedCpuBytes, scene.SizeInBytes)
            };
        }

        WorldDescriptor descriptor = loaded.Descriptor with
        {
            PersistentSceneContentHash = persistentInfo.Hash.ToArray(),
            PersistentScenePayloadBytes = persistentInfo.SizeInBytes,
            Cells = cells
        };
        byte[] payload = WritePayload(descriptor);
        string outputPath = assetDatabase.GetCookedArtifactPath(
            worldRef.Guid,
            RuntimeVariant,
            CookedExtension);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        WriteAtomically(outputPath, payload);

        var output = new FileInfo(outputPath);
        assetDatabase.RegisterCookedArtifact(new CookedAssetRecord(
            worldRef.Guid,
            WorldAssetType,
            RuntimeVariant,
            output.FullName,
            output.Length,
            output.LastWriteTimeUtc));

        CookedWorldDependency[] dependencies = scenes.Values
            .OrderBy(scene => scene.Reference.Guid)
            .ThenBy(scene => scene.Reference.PackageId, StringComparer.Ordinal)
            .Select(scene => new CookedWorldDependency(
                scene.Reference.Guid,
                scene.Reference.PackageId,
                SceneAssetType,
                scene.Reference.Variant,
                Required: true))
            .ToArray();
        Logger.Info(
            $"[WorldAssetCooker] Cooked world {worldRef.Guid:D} | Cells: {cells.Length} | " +
            $"Scene dependencies: {dependencies.Length} | Bytes: {output.Length} | Output: {output.FullName}");
        return new CookedWorldArtifact(
            worldRef.Guid,
            RuntimeVariant,
            output.FullName,
            cells.Length,
            descriptor.EntityReferences.Count,
            output.Length,
            dependencies);
    }

    public static WorldDescriptorLoadResult LoadCooked(
        IAssetDatabase assetDatabase,
        AssetRef<WorldSourceAsset> worldRef)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        if (!worldRef.IsValid)
        {
            return Failure("[WorldAssetCooker] Cooked world asset ref is empty.");
        }

        if (!assetDatabase.TryGetAssetDescriptor(worldRef.Guid, out AssetDescriptor descriptor) ||
            !string.Equals(descriptor.AssetType, WorldAssetType, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                $"[WorldAssetCooker] Cooked world '{worldRef.Guid:D}' has no cataloged World identity.");
        }

        if (!string.IsNullOrWhiteSpace(worldRef.PackageId) &&
            !string.Equals(descriptor.PackageId, worldRef.PackageId, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                $"[WorldAssetCooker] Cooked world '{worldRef.Guid:D}' belongs to package " +
                $"'{descriptor.PackageId}', expected '{worldRef.PackageId}'.");
        }

        if (!assetDatabase.TryLoadCookedAsset(
                worldRef.Guid,
                RuntimeVariant,
                WorldAssetType,
                out CookedAssetHandle handle))
        {
            return Failure(
                $"[WorldAssetCooker] Cooked world '{worldRef.Guid:D}' variant '{RuntimeVariant}' is unavailable.");
        }

        string diagnosticPath = assetDatabase.TryGetCookedArtifact(worldRef.Guid, RuntimeVariant, out CookedAssetRecord? artifact)
            ? artifact.Path
            : $"{worldRef.Guid:D}:{RuntimeVariant}";
        try
        {
            using var _ = Profiler.Zone("WorldAssetCooker.LoadCooked");
            WorldDescriptorLoadResult loaded = TryReadPayload(
                worldRef.Guid,
                assetDatabase.GetCookedAssetBytes(handle).Span,
                diagnosticPath);
            if (!loaded.Success || loaded.Descriptor == null)
            {
                return loaded;
            }

            if (!TryValidateSceneArtifacts(assetDatabase, loaded.Descriptor, out string diagnostic))
            {
                return Failure(diagnostic);
            }

            return loaded;
        }
        finally
        {
            assetDatabase.Release(handle);
        }
    }

    internal static byte[] WritePayload(WorldDescriptor descriptor)
    {
        ValidateDescriptorForWrite(descriptor);
        byte[] payload;
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, s_StrictUtf8, leaveOpen: true))
        {
            WriteString(writer, descriptor.Name);
            WriteSceneReference(writer, descriptor.PersistentScene);
            writer.Write(descriptor.PersistentSceneContentHash);
            writer.Write(descriptor.PersistentScenePayloadBytes);
            WritePosition(writer, descriptor.Partition.Origin);
            WritePosition(writer, descriptor.Partition.CellSize);
            writer.Write(descriptor.Partition.LoadRadius);
            writer.Write(descriptor.Partition.UnloadHysteresis);
            writer.Write(descriptor.Partition.MaxActiveCells);
            writer.Write((int)descriptor.Policy.UnresolvedReferences);
            writer.Write((int)descriptor.Policy.UnloadedTargets);
            writer.Write((int)descriptor.Policy.DependencyCycles);

            foreach (WorldLayerDescriptor layer in descriptor.Layers)
            {
                WriteString(writer, layer.Id);
                writer.Write(layer.Priority);
            }

            foreach (WorldCellDescriptor cell in descriptor.Cells)
            {
                WriteGuid(writer, cell.Id.Value);
                writer.Write(cell.Key.Coordinate.X);
                writer.Write(cell.Key.Coordinate.Y);
                writer.Write(cell.Key.Coordinate.Z);
                WriteString(writer, cell.Key.Layer);
                WriteSceneReference(writer, cell.Scene);
                WritePosition(writer, cell.Bounds.Min);
                WritePosition(writer, cell.Bounds.Max);
                writer.Write(cell.FocusBounds.HasValue);
                if (cell.FocusBounds is WorldBounds focusBounds)
                {
                    WritePosition(writer, focusBounds.Min);
                    WritePosition(writer, focusBounds.Max);
                }
                writer.Write(cell.SceneContentHash);
                writer.Write(cell.ScenePayloadBytes);
                writer.Write(cell.EstimatedCpuBytes);
                writer.Write(cell.EstimatedGpuBytes);
                writer.Write(cell.Neighbors.Count);
                foreach (WorldCellId neighbor in cell.Neighbors)
                {
                    WriteGuid(writer, neighbor.Value);
                }

                writer.Write(cell.Dependencies.Count);
                foreach (WorldCellId dependency in cell.Dependencies)
                {
                    WriteGuid(writer, dependency.Value);
                }
            }

            foreach (WorldEntityReferenceDescriptor reference in descriptor.EntityReferences)
            {
                WriteGuid(writer, reference.SourceCellId.Value);
                WriteGuid(writer, reference.SourceEntityGuid);
                writer.Write((byte)reference.TargetScope);
                WriteGuid(writer, reference.TargetCellId.Value);
                WriteGuid(writer, reference.TargetEntityGuid);
                writer.Write(reference.Required);
            }

            writer.Flush();
            payload = stream.ToArray();
        }

        int totalSize = checked(HeaderSize + payload.Length);
        if (totalSize > MaxCookedWorldBytes)
        {
            throw new InvalidOperationException(
                $"[WorldAssetCooker] Cooked world size '{totalSize}' exceeds {MaxCookedWorldBytes} bytes.");
        }

        byte[] output = new byte[totalSize];
        Span<byte> header = output.AsSpan(0, HeaderSize);
        s_Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(8, 4), EndianMarker);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(12, 4), CookedFormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(16, 4), descriptor.SourceSchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(20, 4), HeaderSize);
        WriteGuid(header.Slice(24, 16), descriptor.WorldGuid);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(40, 4), descriptor.Layers.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(44, 4), descriptor.Cells.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(48, 4), descriptor.EntityReferences.Count);
        int dependencyCount = descriptor.Cells.Sum(cell => cell.Dependencies.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(52, 4), dependencyCount);
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(56, 8), totalSize);
        payload.CopyTo(output, HeaderSize);
        SHA256.HashData(output.AsSpan(HeaderSize)).CopyTo(header.Slice(HashOffset, HashSize));
        return output;
    }

    internal static WorldDescriptorLoadResult TryReadPayload(
        Guid expectedWorldGuid,
        ReadOnlySpan<byte> bytes,
        string diagnosticPath)
    {
        try
        {
            if (bytes.Length < HeaderSize || bytes.Length > MaxCookedWorldBytes)
            {
                throw Invalid(diagnosticPath, $"byte length '{bytes.Length}' is outside supported bounds");
            }

            if (!bytes.Slice(0, 8).SequenceEqual(s_Magic))
            {
                throw Invalid(diagnosticPath, "magic is not ARIWORLD");
            }

            uint endian = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(8, 4));
            int formatVersion = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(12, 4));
            int sourceVersion = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(16, 4));
            int headerSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(20, 4));
            Guid worldGuid = ReadGuid(bytes.Slice(24, 16));
            int layerCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(40, 4));
            int cellCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(44, 4));
            int referenceCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(48, 4));
            int declaredDependencyCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(52, 4));
            long declaredSize = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(56, 8));
            if (endian != EndianMarker ||
                formatVersion != CookedFormatVersion ||
                sourceVersion != WorldDescriptorLoader.CurrentSourceSchemaVersion ||
                headerSize != HeaderSize ||
                worldGuid == Guid.Empty || worldGuid != expectedWorldGuid ||
                layerCount is <= 0 or > 256 ||
                cellCount is <= 0 or > MaxCellCount ||
                referenceCount is < 0 or > MaxReferenceCount ||
                declaredDependencyCount < 0 ||
                declaredSize != bytes.Length)
            {
                throw Invalid(
                    diagnosticPath,
                    "header contains an unsupported version, identity, count, byte order, or size");
            }

            Span<byte> actualHash = stackalloc byte[HashSize];
            SHA256.HashData(bytes.Slice(HeaderSize), actualHash);
            if (!actualHash.SequenceEqual(bytes.Slice(HashOffset, HashSize)))
            {
                throw Invalid(diagnosticPath, "payload SHA-256 does not match the header");
            }

            using var stream = new MemoryStream(bytes.Slice(HeaderSize).ToArray(), writable: false);
            using var reader = new BinaryReader(stream, s_StrictUtf8, leaveOpen: false);
            string name = ReadString(reader, stream, diagnosticPath);
            WorldSceneReference persistentScene = ReadSceneReference(reader, stream, diagnosticPath);
            byte[] persistentHash = ReadExact(reader, stream, HashSize, diagnosticPath);
            long persistentBytes = reader.ReadInt64();
            WorldPartitionSettings partition = new(
                ReadPosition(reader),
                ReadPosition(reader),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32());
            WorldStreamingPolicy policy = new(
                (WorldUnresolvedReferencePolicy)reader.ReadInt32(),
                (WorldUnloadedTargetPolicy)reader.ReadInt32(),
                (WorldDependencyCyclePolicy)reader.ReadInt32());

            var layers = new WorldLayerDescriptor[layerCount];
            for (int index = 0; index < layers.Length; index++)
            {
                layers[index] = new WorldLayerDescriptor(
                    ReadString(reader, stream, diagnosticPath),
                    reader.ReadInt32());
            }

            var cells = new WorldCellDescriptor[cellCount];
            int dependencyCount = 0;
            for (int index = 0; index < cells.Length; index++)
            {
                WorldCellId id = new(ReadGuid(reader));
                var coordinate = new WorldCellCoordinate(
                    reader.ReadInt32(),
                    reader.ReadInt32(),
                    reader.ReadInt32());
                string layer = ReadString(reader, stream, diagnosticPath);
                WorldSceneReference scene = ReadSceneReference(reader, stream, diagnosticPath);
                var bounds = new WorldBounds(ReadPosition(reader), ReadPosition(reader));
                WorldBounds? focusBounds = reader.ReadBoolean()
                    ? new WorldBounds(ReadPosition(reader), ReadPosition(reader))
                    : null;
                byte[] sceneHash = ReadExact(reader, stream, HashSize, diagnosticPath);
                long sceneBytes = reader.ReadInt64();
                long cpuBytes = reader.ReadInt64();
                long gpuBytes = reader.ReadInt64();
                int neighborCount = ReadBoundedCount(reader, 6, "neighbor", diagnosticPath);
                var neighbors = new WorldCellId[neighborCount];
                for (int neighborIndex = 0; neighborIndex < neighborCount; neighborIndex++)
                {
                    neighbors[neighborIndex] = new WorldCellId(ReadGuid(reader));
                }

                int cellDependencyCount = ReadBoundedCount(reader, cellCount - 1, "dependency", diagnosticPath);
                dependencyCount = checked(dependencyCount + cellDependencyCount);
                var dependencies = new WorldCellId[cellDependencyCount];
                for (int dependencyIndex = 0; dependencyIndex < cellDependencyCount; dependencyIndex++)
                {
                    dependencies[dependencyIndex] = new WorldCellId(ReadGuid(reader));
                }

                cells[index] = new WorldCellDescriptor(
                    id,
                    new WorldCellKey(coordinate, layer),
                    scene,
                    bounds,
                    sceneHash,
                    sceneBytes,
                    cpuBytes,
                    gpuBytes,
                    neighbors,
                    dependencies,
                    focusBounds);
            }

            var references = new WorldEntityReferenceDescriptor[referenceCount];
            for (int index = 0; index < references.Length; index++)
            {
                references[index] = new WorldEntityReferenceDescriptor(
                    new WorldCellId(ReadGuid(reader)),
                    ReadGuid(reader),
                    (WorldEntityReferenceScope)reader.ReadByte(),
                    new WorldCellId(ReadGuid(reader)),
                    ReadGuid(reader),
                    reader.ReadBoolean());
            }

            if (stream.Position != stream.Length || dependencyCount != declaredDependencyCount)
            {
                throw Invalid(diagnosticPath, "payload contains trailing bytes or a dependency-count mismatch");
            }

            var descriptor = new WorldDescriptor(
                worldGuid,
                sourceVersion,
                name,
                persistentScene,
                persistentHash,
                persistentBytes,
                partition,
                policy,
                layers,
                cells,
                references);
            ValidateDescriptorForWrite(descriptor);
            return new WorldDescriptorLoadResult(true, descriptor, string.Empty);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException or OverflowException or ArgumentException)
        {
            return Failure(
                ex is InvalidDataException
                    ? ex.Message
                    : $"[WorldAssetCooker] Cooked world '{diagnosticPath}' is invalid: {ex.Message}");
        }
    }

    private static CookedSceneInfo CookScene(
        IAssetDatabase assetDatabase,
        WorldSceneReference scene,
        IDictionary<Guid, CookedSceneInfo> cache)
    {
        if (cache.TryGetValue(scene.Guid, out CookedSceneInfo? existing))
        {
            if (!string.Equals(existing.Reference.PackageId, scene.PackageId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Scene '{scene.Guid:D}' is referenced through conflicting package identities.");
            }

            return existing;
        }

        CookedSceneArtifact cooked = SceneAssetCooker.Cook(
            assetDatabase,
            new AssetRef<SceneSourceAsset>(scene.Guid, SceneAssetType, scene.PackageId));
        byte[] hash;
        using (var stream = new FileStream(
                   cooked.Path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   128 * 1024,
                   FileOptions.SequentialScan))
        {
            hash = SHA256.HashData(stream);
        }

        var result = new CookedSceneInfo(scene, cooked.SizeInBytes, hash);
        cache.Add(scene.Guid, result);
        return result;
    }

    private static bool TryValidateSceneArtifacts(
        IAssetDatabase assetDatabase,
        WorldDescriptor descriptor,
        out string diagnostic)
    {
        var scenes = new Dictionary<Guid, (WorldSceneReference Reference, byte[] Hash, long Size)>();
        scenes.Add(
            descriptor.PersistentScene.Guid,
            (descriptor.PersistentScene, descriptor.PersistentSceneContentHash, descriptor.PersistentScenePayloadBytes));
        foreach (WorldCellDescriptor cell in descriptor.Cells)
        {
            if (scenes.TryGetValue(cell.Scene.Guid, out var existing))
            {
                if (!string.Equals(existing.Reference.PackageId, cell.Scene.PackageId, StringComparison.Ordinal) ||
                    existing.Size != cell.ScenePayloadBytes ||
                    !existing.Hash.AsSpan().SequenceEqual(cell.SceneContentHash))
                {
                    diagnostic =
                        $"[WorldAssetCooker] World '{descriptor.WorldGuid:D}' contains conflicting metadata for scene '{cell.Scene.Guid:D}'.";
                    return false;
                }

                continue;
            }

            scenes.Add(cell.Scene.Guid, (cell.Scene, cell.SceneContentHash, cell.ScenePayloadBytes));
        }

        foreach (var scene in scenes.Values.OrderBy(value => value.Reference.Guid))
        {
            if (!assetDatabase.TryGetAssetDescriptor(scene.Reference.Guid, out AssetDescriptor identity) ||
                !string.Equals(identity.AssetType, SceneAssetType, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(identity.PackageId, scene.Reference.PackageId, StringComparison.OrdinalIgnoreCase) ||
                !assetDatabase.TryGetCookedArtifact(scene.Reference.Guid, scene.Reference.Variant, out CookedAssetRecord? artifact) ||
                artifact.SizeInBytes != scene.Size ||
                !File.Exists(artifact.Path))
            {
                diagnostic =
                    $"[WorldAssetCooker] World '{descriptor.WorldGuid:D}' scene dependency " +
                    $"'{scene.Reference.PackageId}:{scene.Reference.Guid:D}:{scene.Reference.Variant}' is undeclared, missing, or size-mismatched.";
                return false;
            }

            using var stream = new FileStream(
                artifact.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.SequentialScan);
            byte[] actualHash = SHA256.HashData(stream);
            if (!actualHash.AsSpan().SequenceEqual(scene.Hash))
            {
                diagnostic =
                    $"[WorldAssetCooker] World '{descriptor.WorldGuid:D}' scene dependency " +
                    $"'{scene.Reference.Guid:D}' content hash does not match the cooked world catalog.";
                return false;
            }
        }

        diagnostic = string.Empty;
        return true;
    }

    private static void ValidateDescriptorForWrite(WorldDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.WorldGuid == Guid.Empty ||
            descriptor.SourceSchemaVersion != WorldDescriptorLoader.CurrentSourceSchemaVersion ||
            string.IsNullOrWhiteSpace(descriptor.Name) ||
            descriptor.PersistentScene.Guid == Guid.Empty ||
            descriptor.PersistentSceneContentHash.Length != HashSize ||
            descriptor.PersistentScenePayloadBytes <= 0 ||
            !descriptor.Partition.Origin.IsFinite ||
            !descriptor.Partition.CellSize.IsFinite ||
            descriptor.Partition.CellSize.X <= 0 ||
            descriptor.Partition.CellSize.Y <= 0 ||
            descriptor.Partition.CellSize.Z <= 0 ||
            descriptor.Partition.LoadRadius < 0 ||
            descriptor.Partition.UnloadHysteresis < 0 ||
            descriptor.Partition.MaxActiveCells <= 0 ||
            descriptor.Policy.UnresolvedReferences != WorldUnresolvedReferencePolicy.KeepUnresolved ||
            descriptor.Policy.UnloadedTargets != WorldUnloadedTargetPolicy.ClearAndLateResolve ||
            descriptor.Policy.DependencyCycles != WorldDependencyCyclePolicy.Reject ||
            descriptor.Layers.Count is <= 0 or > 256 ||
            descriptor.Cells.Count is <= 0 or > MaxCellCount ||
            descriptor.EntityReferences.Count > MaxReferenceCount)
        {
            throw new InvalidDataException("[WorldAssetCooker] World descriptor contains invalid root metadata.");
        }

        var layers = new HashSet<string>(StringComparer.Ordinal);
        string? previousLayer = null;
        foreach (WorldLayerDescriptor layer in descriptor.Layers)
        {
            string canonical = WorldCellIdentity.NormalizeLayer(layer.Id);
            if (!string.Equals(canonical, layer.Id, StringComparison.Ordinal) ||
                (previousLayer != null && StringComparer.Ordinal.Compare(previousLayer, layer.Id) >= 0) ||
                !layers.Add(layer.Id))
            {
                throw new InvalidDataException("[WorldAssetCooker] World layers are not unique canonical order.");
            }

            previousLayer = layer.Id;
        }

        var cellIds = descriptor.Cells.Select(cell => cell.Id).ToHashSet();
        WorldCellId previousCell = default;
        for (int index = 0; index < descriptor.Cells.Count; index++)
        {
            WorldCellDescriptor cell = descriptor.Cells[index];
            if (!cell.Id.IsValid ||
                cell.Id != WorldCellIdentity.Create(descriptor.WorldGuid, cell.Key.Coordinate, cell.Key.Layer) ||
                !layers.Contains(cell.Key.Layer) ||
                !cell.Bounds.IsValid ||
                (cell.FocusBounds is WorldBounds focusBounds &&
                    (!focusBounds.IsValid || !Contains(cell.Bounds, focusBounds))) ||
                cell.Scene.Guid == Guid.Empty ||
                !string.Equals(cell.Scene.Variant, SceneAssetCooker.RuntimeVariant, StringComparison.Ordinal) ||
                cell.SceneContentHash.Length != HashSize ||
                cell.ScenePayloadBytes <= 0 ||
                cell.EstimatedCpuBytes < cell.ScenePayloadBytes ||
                cell.EstimatedGpuBytes < 0 ||
                (index > 0 && previousCell.CompareTo(cell.Id) >= 0) ||
                !IsStrictlySortedUnique(cell.Neighbors) ||
                !IsStrictlySortedUnique(cell.Dependencies) ||
                cell.Neighbors.Any(neighbor => neighbor == cell.Id || !cellIds.Contains(neighbor)) ||
                cell.Dependencies.Any(dependency => dependency == cell.Id || !cellIds.Contains(dependency)))
            {
                throw new InvalidDataException(
                    $"[WorldAssetCooker] World cell '{cell.Id}' contains invalid or non-canonical metadata.");
            }

            previousCell = cell.Id;
        }

        WorldEntityReferenceDescriptor? previousReference = null;
        foreach (WorldEntityReferenceDescriptor reference in descriptor.EntityReferences)
        {
            bool validTarget = reference.TargetScope switch
            {
                WorldEntityReferenceScope.Persistent => !reference.TargetCellId.IsValid,
                WorldEntityReferenceScope.Cell => reference.TargetCellId.IsValid && cellIds.Contains(reference.TargetCellId),
                _ => false
            };
            if (!cellIds.Contains(reference.SourceCellId) ||
                reference.SourceEntityGuid == Guid.Empty ||
                reference.TargetEntityGuid == Guid.Empty ||
                !validTarget ||
                (previousReference != null && CompareReferences(previousReference, reference) >= 0))
            {
                throw new InvalidDataException("[WorldAssetCooker] World entity references are invalid or not in canonical order.");
            }

            previousReference = reference;
        }
    }

    private static bool IsStrictlySortedUnique(IReadOnlyList<WorldCellId> values)
    {
        for (int index = 0; index < values.Count; index++)
        {
            if (!values[index].IsValid ||
                (index > 0 && values[index - 1].CompareTo(values[index]) >= 0))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Contains(WorldBounds container, WorldBounds candidate)
    {
        return candidate.Min.X >= container.Min.X &&
               candidate.Min.Y >= container.Min.Y &&
               candidate.Min.Z >= container.Min.Z &&
               candidate.Max.X <= container.Max.X &&
               candidate.Max.Y <= container.Max.Y &&
               candidate.Max.Z <= container.Max.Z;
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

    private static int ReadBoundedCount(BinaryReader reader, int maximum, string context, string path)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > maximum)
        {
            throw Invalid(path, $"{context} count '{count}' exceeds '{maximum}'");
        }

        return count;
    }

    private static byte[] ReadExact(
        BinaryReader reader,
        Stream stream,
        int count,
        string path)
    {
        if (stream.Length - stream.Position < count)
        {
            throw Invalid(path, $"payload is truncated while reading {count} bytes");
        }

        byte[] bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
        {
            throw Invalid(path, $"payload is truncated while reading {count} bytes");
        }

        return bytes;
    }

    private static string ReadString(BinaryReader reader, Stream stream, string path)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > MaxStringBytes || stream.Length - stream.Position < length)
        {
            throw Invalid(path, $"string byte length '{length}' is invalid");
        }

        string value = s_StrictUtf8.GetString(ReadExact(reader, stream, length, path));
        if (value.Any(char.IsControl))
        {
            throw Invalid(path, "string contains control characters");
        }

        return value;
    }

    private static WorldSceneReference ReadSceneReference(
        BinaryReader reader,
        Stream stream,
        string path)
    {
        Guid guid = ReadGuid(reader);
        string packageId = ReadString(reader, stream, path);
        string variant = ReadString(reader, stream, path);
        if (guid == Guid.Empty || string.IsNullOrWhiteSpace(packageId) ||
            !string.Equals(variant, SceneAssetCooker.RuntimeVariant, StringComparison.Ordinal))
        {
            throw Invalid(path, "scene reference contains invalid identity, package, or variant");
        }

        return new WorldSceneReference(guid, packageId, variant);
    }

    private static WorldPosition ReadPosition(BinaryReader reader)
    {
        return new WorldPosition(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
    }

    private static Guid ReadGuid(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(16);
        if (bytes.Length != 16)
        {
            throw new EndOfStreamException("Cooked world is truncated while reading a GUID.");
        }

        return new Guid(bytes, bigEndian: true);
    }

    private static Guid ReadGuid(ReadOnlySpan<byte> bytes)
    {
        return new Guid(bytes, bigEndian: true);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidDataException("[WorldAssetCooker] Canonical strings must be non-empty and trimmed.");
        }

        byte[] bytes = s_StrictUtf8.GetBytes(value);
        if (bytes.Length > MaxStringBytes)
        {
            throw new InvalidDataException(
                $"[WorldAssetCooker] String byte length '{bytes.Length}' exceeds {MaxStringBytes}.");
        }

        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteSceneReference(BinaryWriter writer, WorldSceneReference scene)
    {
        WriteGuid(writer, scene.Guid);
        WriteString(writer, scene.PackageId);
        WriteString(writer, scene.Variant);
    }

    private static void WritePosition(BinaryWriter writer, WorldPosition position)
    {
        writer.Write(position.X);
        writer.Write(position.Y);
        writer.Write(position.Z);
    }

    private static void WriteGuid(BinaryWriter writer, Guid guid)
    {
        Span<byte> bytes = stackalloc byte[16];
        guid.TryWriteBytes(bytes, bigEndian: true, out int written);
        if (written != bytes.Length)
        {
            throw new InvalidOperationException("Failed to serialize world GUID.");
        }

        writer.Write(bytes);
    }

    private static void WriteGuid(Span<byte> destination, Guid guid)
    {
        if (!guid.TryWriteBytes(destination, bigEndian: true, out int written) || written != 16)
        {
            throw new InvalidOperationException("Failed to serialize world GUID.");
        }
    }

    private static void WriteAtomically(string outputPath, byte[] bytes)
    {
        string temporaryPath = outputPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static InvalidDataException Invalid(string path, string reason)
    {
        return new InvalidDataException(
            $"[WorldAssetCooker] Cooked world '{path}' is invalid: {reason}.");
    }

    private static WorldDescriptorLoadResult Failure(string diagnostic)
    {
        return new WorldDescriptorLoadResult(false, null, diagnostic);
    }

    private sealed record CookedSceneInfo(
        WorldSceneReference Reference,
        long SizeInBytes,
        byte[] Hash);
}
