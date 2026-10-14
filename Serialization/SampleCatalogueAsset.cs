using System.Text.Json;
using ArisenEngine.Core.Assets;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ArisenEngine.Resources.Serialization;

/// <summary>
/// One asset a sample boots: the scene or world a catalogue entry names. The asset type travels
/// with the reference so a reader never has to guess which loader owns the target.
/// </summary>
public sealed record SampleTargetReference(Guid Guid, string PackageId, string AssetType)
{
    public bool IsValid => Guid != Guid.Empty && !string.IsNullOrWhiteSpace(PackageId);
}

/// <summary>
/// One catalogue entry. A sample is data: identity, a one-line description, the scene and/or world
/// it boots, the packages it needs, and the gate that proves its expected visual.
/// </summary>
public sealed record SampleDefinition(
    string Id,
    string Description,
    SampleTargetReference? Scene,
    SampleTargetReference? World,
    IReadOnlyList<string> Packages,
    string ExpectedVisual,
    string GateScenario,
    string GateArtifact);

public sealed record SampleCatalogueDefinition(
    int Version,
    IReadOnlyList<SampleDefinition> Samples);

public readonly record struct SampleCatalogueLoadResult(
    bool Success,
    SampleCatalogueDefinition? Catalogue,
    string Diagnostic);

/// <summary>
/// Reads the sample catalogue from its source YAML (editor and cook-stage access) or its cooked
/// payload (runtime read-only access). Both paths produce the same definition, so a gate resolves a
/// sample id identically in every profile.
/// </summary>
public static class SampleCatalogueLoader
{
    public const string AssetType = "SampleCatalogue";
    public const string SceneAssetType = "Scene";
    public const string WorldAssetType = "World";
    public const int CurrentSourceSchemaVersion = 1;

    private const int MaxSamples = 1024;
    private const int MaxTextLength = 4096;

    public static SampleCatalogueLoadResult LoadSource(
        IAssetDatabase assetDatabase,
        AssetRef<SampleCatalogueSourceAsset> catalogueRef)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        if (!catalogueRef.IsValid)
        {
            return Failure("[SampleCatalogueLoader] The catalogue reference is empty.");
        }

        if (!assetDatabase.TryGetAsset(catalogueRef, out AssetRecord? catalogueAsset) ||
            !string.Equals(catalogueAsset.AssetType, AssetType, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                $"[SampleCatalogueLoader] Asset '{catalogueRef.Guid:D}' is not indexed as '{AssetType}'.");
        }

        if (!string.IsNullOrWhiteSpace(catalogueRef.PackageId) &&
            !string.Equals(catalogueAsset.PackageId, catalogueRef.PackageId, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                $"[SampleCatalogueLoader] Catalogue '{catalogueRef.Guid:D}' belongs to package " +
                $"'{catalogueAsset.PackageId}', expected '{catalogueRef.PackageId}'.");
        }

        if (!File.Exists(catalogueAsset.SourcePath))
        {
            return Failure($"[SampleCatalogueLoader] Catalogue source is missing: {catalogueAsset.SourcePath}");
        }

        try
        {
            return LoadSourceText(catalogueAsset.SourcePath, File.ReadAllText(catalogueAsset.SourcePath));
        }
        catch (Exception ex)
        {
            return Failure(
                $"[SampleCatalogueLoader] Failed to read catalogue '{catalogueAsset.SourcePath}': {ex.Message}");
        }
    }

    public static SampleCatalogueLoadResult LoadSourceText(string diagnosticPath, string sourceText)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        if (!TryDeserializeSource(diagnosticPath, sourceText, out SampleCatalogueSourceDocument? source,
                out string diagnostic))
        {
            return Failure(diagnostic);
        }

        if (source!.Version is < 1 or > CurrentSourceSchemaVersion)
        {
            return Failure(
                $"[SampleCatalogueLoader] Catalogue '{diagnosticPath}' schema version '{source.Version}' " +
                $"is unsupported; expected 1..{CurrentSourceSchemaVersion}.");
        }

        return Build(
            diagnosticPath,
            source.Version,
            source.Samples.Select(entry => new RawSampleEntry(
                entry.Id,
                entry.Scene,
                entry.World,
                entry.Packages,
                entry.Description,
                entry.ExpectedVisual,
                entry.Gate,
                entry.Artifact)).ToList());
    }

    public static SampleCatalogueLoadResult LoadCooked(
        IAssetDatabase assetDatabase,
        AssetRef<SampleCatalogueSourceAsset> catalogueRef,
        string variant)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        if (!catalogueRef.IsValid || string.IsNullOrWhiteSpace(catalogueRef.PackageId))
        {
            return Failure("[SampleCatalogueLoader] A cooked catalogue requires a valid reference.");
        }

        if (!assetDatabase.TryGetAssetDescriptor(catalogueRef.Guid, out AssetDescriptor descriptor) ||
            !string.Equals(descriptor.AssetType, AssetType, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                $"[SampleCatalogueLoader] Cooked catalogue '{catalogueRef.Guid:D}' has no cataloged " +
                $"{AssetType} identity.");
        }

        if (!string.Equals(descriptor.PackageId, catalogueRef.PackageId, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                $"[SampleCatalogueLoader] Cooked catalogue '{catalogueRef.Guid:D}' belongs to package " +
                $"'{descriptor.PackageId}', expected '{catalogueRef.PackageId}'.");
        }

        if (!assetDatabase.TryLoadCookedAsset(
                catalogueRef.Guid,
                variant,
                AssetType,
                out CookedAssetHandle handle))
        {
            return Failure(
                $"[SampleCatalogueLoader] Cooked catalogue '{catalogueRef.Guid:D}' variant " +
                $"'{variant}' is unavailable.");
        }

        try
        {
            CookedSampleCatalogueDocument? document = JsonSerializer.Deserialize<CookedSampleCatalogueDocument>(
                assetDatabase.GetCookedAssetBytes(handle).Span);
            if (document == null)
            {
                return Failure(
                    $"[SampleCatalogueLoader] Cooked catalogue '{catalogueRef.Guid:D}' is empty.");
            }

            if (document.Version is < 1 or > CurrentSourceSchemaVersion)
            {
                return Failure(
                    $"[SampleCatalogueLoader] Cooked catalogue '{catalogueRef.Guid:D}' schema version " +
                    $"'{document.Version}' is unsupported; expected 1..{CurrentSourceSchemaVersion}.");
            }

            return Build(
                $"{catalogueRef.Guid:D}:{variant}",
                document.Version,
                document.Samples.Select(entry => new RawSampleEntry(
                    entry.Id ?? string.Empty,
                    ToSource(entry.Scene),
                    ToSource(entry.World),
                    entry.Packages,
                    entry.Description,
                    entry.ExpectedVisual,
                    entry.Gate,
                    entry.Artifact)).ToList());
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return Failure(
                $"[SampleCatalogueLoader] Cooked catalogue '{catalogueRef.Guid:D}' is invalid: {ex.Message}");
        }
        finally
        {
            assetDatabase.Release(handle);
        }
    }

    internal static byte[] WriteCookedPayload(SampleCatalogueDefinition catalogue)
    {
        var document = new CookedSampleCatalogueDocument
        {
            Version = catalogue.Version,
            Samples = catalogue.Samples.Select(sample => new CookedSampleEntry
            {
                Id = sample.Id,
                Description = sample.Description,
                Scene = ToCooked(sample.Scene),
                World = ToCooked(sample.World),
                Packages = sample.Packages.ToList(),
                ExpectedVisual = sample.ExpectedVisual,
                Gate = sample.GateScenario,
                Artifact = sample.GateArtifact
            }).ToList()
        };
        return JsonSerializer.SerializeToUtf8Bytes(document);
    }

    private static SampleCatalogueLoadResult Build(
        string diagnosticPath,
        int version,
        List<RawSampleEntry> entries)
    {
        if (entries.Count == 0)
        {
            return Failure($"[SampleCatalogueLoader] Catalogue '{diagnosticPath}' declares no samples.");
        }

        if (entries.Count > MaxSamples)
        {
            return Failure(
                $"[SampleCatalogueLoader] Catalogue '{diagnosticPath}' declares {entries.Count} samples; " +
                $"the limit is {MaxSamples}.");
        }

        var samples = new List<SampleDefinition>(entries.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < entries.Count; index++)
        {
            if (!TryBuildSample(
                    entries[index],
                    $"samples[{index}]",
                    out SampleDefinition? sample,
                    out string error))
            {
                return Failure($"[SampleCatalogueLoader] Catalogue '{diagnosticPath}': {error}");
            }

            if (!ids.Add(sample!.Id))
            {
                return Failure(
                    $"[SampleCatalogueLoader] Catalogue '{diagnosticPath}' declares sample id " +
                    $"'{sample.Id}' more than once.");
            }

            samples.Add(sample);
        }

        return new SampleCatalogueLoadResult(
            true,
            new SampleCatalogueDefinition(version, samples),
            string.Empty);
    }

    private static bool TryBuildSample(
        RawSampleEntry entry,
        string position,
        out SampleDefinition? sample,
        out string error)
    {
        sample = null;
        error = string.Empty;

        string normalizedId = (entry.Id ?? string.Empty).Trim();
        if (normalizedId.Length == 0)
        {
            error = $"{position} has an empty id.";
            return false;
        }

        if (!TryBuildReference(entry.Scene, SceneAssetType, position, normalizedId,
                out SampleTargetReference? sceneRef, out error) ||
            !TryBuildReference(entry.World, WorldAssetType, position, normalizedId,
                out SampleTargetReference? worldRef, out error))
        {
            return false;
        }

        if (sceneRef == null && worldRef == null)
        {
            error = $"{position} ('{normalizedId}') declares neither a scene nor a world.";
            return false;
        }

        if (!TryNormalizeText(entry.Description, $"{position} description", out string description, out error) ||
            !TryNormalizeText(entry.ExpectedVisual, $"{position} expected visual", out string expectedVisual,
                out error) ||
            !TryNormalizeText(entry.Gate, $"{position} gate", out string gate, out error) ||
            !TryNormalizeText(entry.Artifact, $"{position} artifact", out string artifact, out error))
        {
            return false;
        }

        List<string> packages = entry.Packages ?? new List<string>();
        var normalizedPackages = new List<string>(packages.Count);
        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string package in packages)
        {
            string normalizedPackage = (package ?? string.Empty).Trim();
            if (normalizedPackage.Length == 0)
            {
                error = $"{position} declares an empty package id.";
                return false;
            }

            if (!packageIds.Add(normalizedPackage))
            {
                error = $"{position} declares package '{normalizedPackage}' more than once.";
                return false;
            }

            normalizedPackages.Add(normalizedPackage);
        }

        sample = new SampleDefinition(
            normalizedId,
            description,
            sceneRef,
            worldRef,
            normalizedPackages,
            expectedVisual,
            gate,
            artifact);
        return true;
    }

    private static bool TryBuildReference(
        SampleSourceReference? source,
        string assetType,
        string position,
        string sampleId,
        out SampleTargetReference? reference,
        out string error)
    {
        reference = null;
        error = string.Empty;
        if (source == null)
        {
            return true;
        }

        var candidate = new SampleTargetReference(source.Guid, source.PackageId?.Trim() ?? string.Empty, assetType);
        if (!candidate.IsValid)
        {
            error = $"{position} ('{sampleId}') {assetType.ToLowerInvariant()} reference requires both " +
                    "a valid Guid and PackageId.";
            return false;
        }

        reference = candidate;
        return true;
    }

    private static bool TryNormalizeText(
        string? source,
        string field,
        out string normalized,
        out string error)
    {
        normalized = (source ?? string.Empty).Trim();
        error = string.Empty;
        if (normalized.Length > MaxTextLength)
        {
            error = $"{field} exceeds {MaxTextLength} characters.";
            return false;
        }

        return true;
    }

    private static bool TryDeserializeSource(
        string path,
        string sourceText,
        out SampleCatalogueSourceDocument? source,
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
                diagnostic =
                    $"[SampleCatalogueLoader] Catalogue '{path}' must contain one YAML mapping document.";
                return false;
            }

            ValidateSourceShape(root, path);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(PascalCaseNamingConvention.Instance)
                .Build();
            source = deserializer.Deserialize<SampleCatalogueSourceDocument>(sourceText);
            if (source == null)
            {
                diagnostic =
                    $"[SampleCatalogueLoader] Catalogue '{path}' deserialized to an empty document.";
                return false;
            }

            diagnostic = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            source = null;
            diagnostic = $"[SampleCatalogueLoader] Failed to parse catalogue '{path}': {ex.Message}";
            return false;
        }
    }

    private static void ValidateSourceShape(YamlMappingNode root, string path)
    {
        foreach (var entry in root.Children)
        {
            if (entry.Key is not YamlScalarNode key)
            {
                continue;
            }

            if (string.Equals(key.Value, nameof(SampleCatalogueSourceDocument.Samples), StringComparison.Ordinal) &&
                entry.Value is not YamlSequenceNode)
            {
                throw new InvalidDataException(
                    $"Catalogue '{path}' field 'Samples' must be a sequence.");
            }
        }
    }

    private static SampleCatalogueLoadResult Failure(string diagnostic) =>
        new(false, null, diagnostic);

    private static CookedSampleReference? ToCooked(SampleTargetReference? reference) =>
        reference == null
            ? null
            : new CookedSampleReference
            {
                Guid = reference.Guid,
                PackageId = reference.PackageId,
                AssetType = reference.AssetType
            };

    private static SampleSourceReference? ToSource(CookedSampleReference? reference) =>
        reference == null
            ? null
            : new SampleSourceReference
            {
                Guid = reference.Guid,
                PackageId = reference.PackageId ?? string.Empty
            };

    internal sealed class SampleCatalogueSourceDocument
    {
        public int Version { get; set; } = 1;
        public List<SampleSourceEntry> Samples { get; set; } = new();
    }

    internal sealed class SampleSourceEntry
    {
        public string Id { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public SampleSourceReference? Scene { get; set; }
        public SampleSourceReference? World { get; set; }
        public List<string> Packages { get; set; } = new();
        public string ExpectedVisual { get; set; } = string.Empty;
        public string Gate { get; set; } = string.Empty;
        public string Artifact { get; set; } = string.Empty;
    }

    internal sealed class SampleSourceReference
    {
        public Guid Guid { get; set; }
        public string PackageId { get; set; } = string.Empty;
    }

    private sealed class CookedSampleCatalogueDocument
    {
        public int Version { get; set; }
        public List<CookedSampleEntry> Samples { get; set; } = new();
    }

    private sealed class CookedSampleEntry
    {
        public string? Id { get; set; }
        public string? Description { get; set; }
        public CookedSampleReference? Scene { get; set; }
        public CookedSampleReference? World { get; set; }
        public List<string>? Packages { get; set; }
        public string? ExpectedVisual { get; set; }
        public string? Gate { get; set; }
        public string? Artifact { get; set; }
    }

    private sealed class CookedSampleReference
    {
        public Guid Guid { get; set; }
        public string? PackageId { get; set; }
        public string? AssetType { get; set; }
    }

    private sealed record RawSampleEntry(
        string? Id,
        SampleSourceReference? Scene,
        SampleSourceReference? World,
        List<string>? Packages,
        string? Description,
        string? ExpectedVisual,
        string? Gate,
        string? Artifact);
}
