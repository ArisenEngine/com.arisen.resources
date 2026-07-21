using ArisenEngine.Core.Assets;

namespace ArisenEngine.Resources.Serialization;

public sealed class SceneRuntimeAssetCooker : IRuntimeAssetCooker
{
    private const string SceneAssetType = "Scene";
    private readonly IAssetDatabase m_AssetDatabase;

    public SceneRuntimeAssetCooker(IAssetDatabase assetDatabase)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
    }

    public string ProviderId => "com.arisen.resources.scene-cooker";

    public IReadOnlyCollection<string> AssetTypes { get; } = [SceneAssetType];

    public RuntimeAssetCookerOutput Cook(
        RuntimeAssetCookContext context,
        RuntimeAssetCookRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateRequest(request);

        CookedSceneArtifact cooked = SceneAssetCooker.Cook(
            m_AssetDatabase,
            new AssetRef<SceneSourceAsset>(request.Guid, SceneAssetType, request.PackageId));
        RuntimeAssetCookDependencyRequest[] dependencies = cooked.Dependencies
            .Select(dependency => new RuntimeAssetCookDependencyRequest(
                dependency.Guid,
                ResolvePackageId(dependency.Guid, dependency.PackageId),
                dependency.AssetType,
                Variant: string.Empty,
                dependency.Required))
            .ToArray();
        return RuntimeAssetCookerOutput.FromFile(
            request,
            cooked.Variant,
            BuildOutputRelativePath(
                request.PackageId,
                request.Guid,
                cooked.Variant,
                SceneAssetCooker.CookedExtension),
            cooked.Path,
            SceneAssetCooker.CookedFormatVersion,
            dependencies);
    }

    private void ValidateRequest(RuntimeAssetCookRequest request)
    {
        if (!string.Equals(request.AssetType, SceneAssetType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"[SceneRuntimeAssetCooker] Unsupported asset type '{request.AssetType}'.");
        }

        if (request.Variant.Length > 0 &&
            !string.Equals(request.Variant, SceneAssetCooker.RuntimeVariant, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"[SceneRuntimeAssetCooker] Scene variant '{request.Variant}' is unsupported.");
        }

        if (!m_AssetDatabase.TryGetAsset(request.Guid, out AssetRecord? sourceAsset) ||
            !string.Equals(sourceAsset.AssetType, SceneAssetType, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(sourceAsset.PackageId, request.PackageId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[SceneRuntimeAssetCooker] Scene '{request.Guid:D}' is not owned by package '{request.PackageId}'.");
        }
    }

    private string ResolvePackageId(Guid guid, string declaredPackageId)
    {
        if (!m_AssetDatabase.TryGetAsset(guid, out AssetRecord? sourceAsset))
        {
            throw new InvalidOperationException(
                $"[SceneRuntimeAssetCooker] Dependency '{guid:D}' is not indexed.");
        }

        if (!string.IsNullOrWhiteSpace(declaredPackageId) &&
            !string.Equals(
                sourceAsset.PackageId,
                declaredPackageId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[SceneRuntimeAssetCooker] Dependency '{guid:D}' belongs to package " +
                $"'{sourceAsset.PackageId}', not '{declaredPackageId}'.");
        }

        return sourceAsset.PackageId;
    }

    private static string BuildOutputRelativePath(
        string packageId,
        Guid guid,
        string variant,
        string extension)
    {
        return $"{packageId}/{guid:N}/{variant}{extension}";
    }
}
