using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;

namespace ArisenEngine.Resources.Serialization;

public sealed record CookedSampleCatalogue(
    Guid Guid,
    string Variant,
    string Path,
    int SampleCount,
    IReadOnlyList<RuntimeAssetCookDependencyRequest> Dependencies);

/// <summary>
/// Cooks the workspace's sample catalogue. The catalogue is what the shell resolves
/// <c>--startup-sample</c> through and what the hub browses, so the cook closes over every entry's
/// content: a cooked-only profile can boot any declared sample, which is what makes in-process
/// switching and direct gated launches work without a source fallback.
/// </summary>
public static class SampleCatalogueCooker
{
    public const string RuntimeVariant = "runtime.sample-catalogue.v1";
    public const string CookedExtension = ".arisamples";
    public const int CookedFormatVersion = 1;

    public static CookedSampleCatalogue Cook(
        IAssetDatabase assetDatabase,
        AssetRef<SampleCatalogueSourceAsset> catalogueRef)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        SampleCatalogueLoadResult loaded = SampleCatalogueLoader.LoadSource(assetDatabase, catalogueRef);
        if (!loaded.Success || loaded.Catalogue == null)
        {
            throw new InvalidOperationException(loaded.Diagnostic);
        }

        using var _ = Profiler.Zone("SampleCatalogueCooker.Cook");
        SampleCatalogueDefinition catalogue = loaded.Catalogue;
        var dependencies = new List<RuntimeAssetCookDependencyRequest>();
        foreach (SampleDefinition sample in catalogue.Samples)
        {
            AddDependency(dependencies, sample.Scene, SampleCatalogueLoader.SceneAssetType, sample.Id);
            AddDependency(dependencies, sample.World, SampleCatalogueLoader.WorldAssetType, sample.Id);
        }

        byte[] payload = SampleCatalogueLoader.WriteCookedPayload(catalogue);
        using CookedArtifactWrite write = assetDatabase.BeginCookedArtifactWrite(
            catalogueRef.Guid,
            RuntimeVariant,
            CookedExtension);
        WriteAtomically(write.OutputPath, payload);
        CookedAssetRecord output = write.Commit(SampleCatalogueLoader.AssetType);
        Logger.Info(
            $"[SampleCatalogueCooker] Cooked catalogue {catalogueRef.Guid:D} | Samples: " +
            $"{catalogue.Samples.Count} | Dependencies: {dependencies.Count} | Bytes: " +
            $"{output.SizeInBytes} | Output: {output.Path}");

        return new CookedSampleCatalogue(catalogueRef.Guid, RuntimeVariant, output.Path,
            catalogue.Samples.Count, dependencies);
    }

    private static void AddDependency(
        List<RuntimeAssetCookDependencyRequest> dependencies,
        SampleTargetReference? reference,
        string assetType,
        string sampleId)
    {
        if (reference == null)
        {
            return;
        }

        if (!string.Equals(reference.AssetType, assetType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"[SampleCatalogueCooker] Sample '{sampleId}' reference declares asset type " +
                $"'{reference.AssetType}', expected '{assetType}'.");
        }

        dependencies.Add(new RuntimeAssetCookDependencyRequest(
            reference.Guid,
            reference.PackageId,
            assetType,
            Variant: string.Empty,
            Required: true));
    }

    private static void WriteAtomically(string path, byte[] payload)
    {
        string temporaryPath = path + ".tmp";
        File.WriteAllBytes(temporaryPath, payload);
        File.Move(temporaryPath, path, overwrite: true);
    }
}
