using ArisenEngine.Core.Assets;

namespace ArisenEngine.Resources.Serialization;

public sealed class SampleCatalogueRuntimeAssetCooker : IRuntimeAssetCooker
{
    private readonly IAssetDatabase m_AssetDatabase;

    public SampleCatalogueRuntimeAssetCooker(IAssetDatabase assetDatabase)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
    }

    public string ProviderId => "com.arisen.resources.sample-catalogue-cooker";

    public IReadOnlyCollection<string> AssetTypes { get; } = [SampleCatalogueLoader.AssetType];

    public RuntimeAssetCookerOutput Cook(
        RuntimeAssetCookContext context,
        RuntimeAssetCookRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateRequest(request);
        CookedSampleCatalogue cooked = SampleCatalogueCooker.Cook(
            m_AssetDatabase,
            new AssetRef<SampleCatalogueSourceAsset>(
                request.Guid,
                SampleCatalogueLoader.AssetType,
                request.PackageId));
        return RuntimeAssetCookerOutput.FromFile(
            request,
            cooked.Variant,
            $"{request.PackageId}/{request.Guid:N}/{cooked.Variant}" +
            SampleCatalogueCooker.CookedExtension,
            cooked.Path,
            SampleCatalogueCooker.CookedFormatVersion,
            cooked.Dependencies);
    }

    private static void ValidateRequest(RuntimeAssetCookRequest request)
    {
        if (!string.Equals(request.AssetType, SampleCatalogueLoader.AssetType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"[SampleCatalogueRuntimeAssetCooker] Unsupported asset type '{request.AssetType}'.");
        }

        if (request.Variant.Length > 0 &&
            !string.Equals(request.Variant, SampleCatalogueCooker.RuntimeVariant, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"[SampleCatalogueRuntimeAssetCooker] Variant '{request.Variant}' is unsupported.");
        }
    }
}
