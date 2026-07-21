using ArisenEngine.Core.Assets;

namespace ArisenEngine.Resources.Serialization;

public sealed class WorldRuntimeAssetCooker : IRuntimeAssetCooker
{
    private const string WorldAssetType = "World";
    private readonly IAssetDatabase m_AssetDatabase;

    public WorldRuntimeAssetCooker(IAssetDatabase assetDatabase)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
    }

    public string ProviderId => "com.arisen.resources.world-cooker";

    public IReadOnlyCollection<string> AssetTypes { get; } = [WorldAssetType];

    public RuntimeAssetCookerOutput Cook(
        RuntimeAssetCookContext context,
        RuntimeAssetCookRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateRequest(request);
        CookedWorldArtifact cooked = WorldAssetCooker.Cook(
            m_AssetDatabase,
            new AssetRef<WorldSourceAsset>(request.Guid, WorldAssetType, request.PackageId));
        RuntimeAssetCookDependencyRequest[] dependencies = cooked.Dependencies
            .Select(dependency => new RuntimeAssetCookDependencyRequest(
                dependency.Guid,
                dependency.PackageId,
                dependency.AssetType,
                dependency.Variant,
                dependency.Required))
            .ToArray();
        return RuntimeAssetCookerOutput.FromFile(
            request,
            cooked.Variant,
            $"{request.PackageId}/{request.Guid:N}/{cooked.Variant}{WorldAssetCooker.CookedExtension}",
            cooked.Path,
            WorldAssetCooker.CookedFormatVersion,
            dependencies);
    }

    private void ValidateRequest(RuntimeAssetCookRequest request)
    {
        if (!string.Equals(request.AssetType, WorldAssetType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"[WorldRuntimeAssetCooker] Unsupported asset type '{request.AssetType}'.");
        }

        if (request.Variant.Length > 0 &&
            !string.Equals(request.Variant, WorldAssetCooker.RuntimeVariant, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"[WorldRuntimeAssetCooker] World variant '{request.Variant}' is unsupported.");
        }

        if (!m_AssetDatabase.TryGetAsset(request.Guid, out AssetRecord? sourceAsset) ||
            !string.Equals(sourceAsset.AssetType, WorldAssetType, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(sourceAsset.PackageId, request.PackageId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[WorldRuntimeAssetCooker] World '{request.Guid:D}' is not owned by package '{request.PackageId}'.");
        }
    }
}
