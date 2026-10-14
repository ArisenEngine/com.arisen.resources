using ArisenEngine.Core.Assets;
using ArisenKernel.Lifecycle;
using ArisenKernel.Packages;

namespace ArisenEngine.Resources.Serialization;

/// <summary>
/// Resolves the workspace's sample catalogue. The shell resolves a selected sample id through it,
/// and the hub browses the same definitions, so both read one declaration.
/// </summary>
public interface ISampleCatalogueService
{
    /// <summary>Entries in declaration order.</summary>
    IReadOnlyList<SampleDefinition> Samples { get; }

    /// <summary>
    /// The entry with <paramref name="sampleId"/>. Throws when the catalogue does not declare it,
    /// naming the ids it does declare, because a selector that cannot resolve must fail loudly.
    /// </summary>
    SampleDefinition GetSample(string sampleId);

    bool TryGetSample(string sampleId, out SampleDefinition? sample);
}

public sealed class SampleCatalogueService : ISampleCatalogueService
{
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly ProjectSubsystem m_ProjectSubsystem;
    private readonly object m_LoadGate = new();
    private SampleCatalogueDefinition? m_Catalogue;
    private string m_Diagnostic = string.Empty;

    public SampleCatalogueService(IAssetDatabase assetDatabase, ProjectSubsystem projectSubsystem)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_ProjectSubsystem = projectSubsystem ?? throw new ArgumentNullException(nameof(projectSubsystem));
    }

    public IReadOnlyList<SampleDefinition> Samples => Load().Samples;

    public SampleDefinition GetSample(string sampleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sampleId);
        SampleCatalogueDefinition catalogue = Load();
        foreach (SampleDefinition sample in catalogue.Samples)
        {
            if (string.Equals(sample.Id, sampleId, StringComparison.Ordinal))
            {
                return sample;
            }
        }

        throw new InvalidOperationException(
            $"[SampleCatalogueService] The catalogue declares no sample '{sampleId}'. Declared ids: " +
            $"{string.Join(", ", catalogue.Samples.Select(sample => sample.Id))}.");
    }

    public bool TryGetSample(string sampleId, out SampleDefinition? sample)
    {
        sample = null;
        if (string.IsNullOrWhiteSpace(sampleId))
        {
            return false;
        }

        foreach (SampleDefinition candidate in Load().Samples)
        {
            if (string.Equals(candidate.Id, sampleId, StringComparison.Ordinal))
            {
                sample = candidate;
                return true;
            }
        }

        return false;
    }

    private SampleCatalogueDefinition Load()
    {
        lock (m_LoadGate)
        {
            if (m_Catalogue != null)
            {
                return m_Catalogue;
            }

            if (!string.IsNullOrWhiteSpace(m_Diagnostic))
            {
                throw new InvalidOperationException(m_Diagnostic);
            }

            ProjectManifest? project = m_ProjectSubsystem.ActiveProject;
            if (project?.SampleCatalogue is not { IsValid: true } reference)
            {
                m_Diagnostic =
                    "[SampleCatalogueService] The workspace declares no SampleCatalogue asset, so no " +
                    "sample can be selected or browsed.";
                throw new InvalidOperationException(m_Diagnostic);
            }

            var catalogueRef = new AssetRef<SampleCatalogueSourceAsset>(
                reference.Guid,
                SampleCatalogueLoader.AssetType,
                reference.PackageId);
            SampleCatalogueLoadResult loaded = m_AssetDatabase.CanReadSourceAssets
                ? SampleCatalogueLoader.LoadSource(m_AssetDatabase, catalogueRef)
                : SampleCatalogueLoader.LoadCooked(m_AssetDatabase, catalogueRef,
                    SampleCatalogueCooker.RuntimeVariant);
            if (!loaded.Success || loaded.Catalogue == null)
            {
                m_Diagnostic = loaded.Diagnostic;
                throw new InvalidOperationException(m_Diagnostic);
            }

            m_Catalogue = loaded.Catalogue;
            return m_Catalogue;
        }
    }
}
