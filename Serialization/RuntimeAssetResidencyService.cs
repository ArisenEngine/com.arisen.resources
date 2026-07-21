using System.Diagnostics;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;

namespace ArisenEngine.Resources.Serialization;

public enum RuntimeAssetResidencyOwnerKind
{
    PersistentScene = 1,
    WorldCell = 2
}

public readonly record struct RuntimeAssetResidencyOwnerId(
    Guid WorldGuid,
    WorldCellId CellId,
    long Generation,
    RuntimeAssetResidencyOwnerKind Kind) : IComparable<RuntimeAssetResidencyOwnerId>
{
    public static RuntimeAssetResidencyOwnerId Persistent(Guid worldGuid) =>
        new(worldGuid, default, 0, RuntimeAssetResidencyOwnerKind.PersistentScene);

    public static RuntimeAssetResidencyOwnerId Cell(
        Guid worldGuid,
        WorldCellId cellId,
        long generation) =>
        new(worldGuid, cellId, generation, RuntimeAssetResidencyOwnerKind.WorldCell);

    public bool IsValid =>
        WorldGuid != Guid.Empty &&
        Enum.IsDefined(Kind) &&
        (Kind == RuntimeAssetResidencyOwnerKind.PersistentScene
            ? !CellId.IsValid && Generation == 0
            : CellId.IsValid && Generation > 0);

    public int CompareTo(RuntimeAssetResidencyOwnerId other)
    {
        int comparison = WorldGuid.CompareTo(other.WorldGuid);
        if (comparison != 0) return comparison;
        comparison = Kind.CompareTo(other.Kind);
        if (comparison != 0) return comparison;
        comparison = CellId.CompareTo(other.CellId);
        return comparison != 0 ? comparison : Generation.CompareTo(other.Generation);
    }
}

public readonly record struct RuntimeAssetResidencyKey(
    Guid Guid,
    string PackageId,
    string AssetType,
    string Variant) : IComparable<RuntimeAssetResidencyKey>
{
    public bool IsValid =>
        Guid != Guid.Empty &&
        !string.IsNullOrWhiteSpace(PackageId) &&
        !string.IsNullOrWhiteSpace(AssetType) &&
        !string.IsNullOrWhiteSpace(Variant);

    public int CompareTo(RuntimeAssetResidencyKey other)
    {
        int comparison = Guid.CompareTo(other.Guid);
        if (comparison != 0) return comparison;
        comparison = StringComparer.Ordinal.Compare(AssetType, other.AssetType);
        if (comparison != 0) return comparison;
        comparison = StringComparer.Ordinal.Compare(Variant, other.Variant);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(PackageId, other.PackageId);
    }

    public override string ToString() => $"{PackageId}:{AssetType}:{Guid:D}:{Variant}";
}

public static class RuntimeAssetVariantPolicy
{
    public const string StaticMesh = "staticmesh.uint32";
    public const string Material = "material.runtime";
    public const string EnvironmentTexture = "latlong.r16g16b16a16sfloat.nomips";

    public static bool TryResolve(string assetType, out string variant)
    {
        variant = assetType switch
        {
            "Mesh" => StaticMesh,
            "Material" => Material,
            "EnvironmentTexture" => EnvironmentTexture,
            _ => string.Empty
        };
        return variant.Length > 0;
    }
}

public enum RuntimePreparedAssetState
{
    Waiting,
    Ready,
    Failed
}

public readonly record struct RuntimePreparedAssetResult(
    RuntimePreparedAssetState State,
    long EstimatedGpuBytes,
    string Diagnostic)
{
    public static RuntimePreparedAssetResult Waiting(string diagnostic = "") =>
        new(RuntimePreparedAssetState.Waiting, 0, diagnostic);

    public static RuntimePreparedAssetResult Ready(long estimatedGpuBytes) =>
        new(RuntimePreparedAssetState.Ready, Math.Max(0, estimatedGpuBytes), string.Empty);

    public static RuntimePreparedAssetResult Failed(string diagnostic) =>
        new(RuntimePreparedAssetState.Failed, 0, diagnostic);
}

public readonly record struct RuntimePreparedAssetProviderMetrics(
    int PreparedResourceCount,
    long EstimatedGpuBytes,
    int PendingDisposalCount,
    int DescriptorCount = 0);

public interface IRuntimePreparedAssetProvider
{
    string ProviderId { get; }
    bool Supports(string assetType);
    RuntimePreparedAssetResult Prepare(RuntimeAssetResidencyKey key);
    void Release(RuntimeAssetResidencyKey key);
    RuntimePreparedAssetProviderMetrics GetMetrics();
}

public sealed record RuntimeAssetResidencyBudgets(
    long MaxCpuCookedBytes,
    long MaxPreparedGpuBytes,
    int MaxSetupsPerFrame,
    double MaxSetupMilliseconds,
    int MaxInactiveResources)
{
    public static RuntimeAssetResidencyBudgets Default { get; } = new(
        MaxCpuCookedBytes: 512L * 1024 * 1024,
        MaxPreparedGpuBytes: 1024L * 1024 * 1024,
        MaxSetupsPerFrame: 4,
        MaxSetupMilliseconds: 3.0,
        MaxInactiveResources: 0);

    public void Validate()
    {
        if (MaxCpuCookedBytes <= 0 ||
            MaxPreparedGpuBytes <= 0 ||
            MaxSetupsPerFrame <= 0 ||
            !double.IsFinite(MaxSetupMilliseconds) || MaxSetupMilliseconds <= 0 ||
            MaxInactiveResources < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RuntimeAssetResidencyBudgets),
                "Runtime asset residency budgets must be positive and finite.");
        }
    }
}

public sealed record RuntimeAssetResidencySnapshot(
    RuntimeAssetResidencyKey Key,
    RuntimePreparedAssetState PreparedState,
    int OwnerCount,
    int PinnedOwnerCount,
    long CpuCookedBytes,
    long EstimatedGpuBytes,
    long LastNeededSequence,
    bool SourceBacked,
    string ProviderId,
    string Diagnostic);

public sealed record RuntimeAssetResidencyMetrics(
    int ResidentAssetCount,
    int ActiveOwnerCount,
    int WaitingAssetCount,
    int ReadyAssetCount,
    int FailedAssetCount,
    int InactiveAssetCount,
    int PinnedAssetCount,
    long CpuCookedBytes,
    long PreparedGpuBytes,
    long PeakCpuCookedBytes,
    long PeakPreparedGpuBytes,
    int PreparedDescriptorCount,
    int PendingDisposalCount,
    long SetupCount,
    long SetupFailureCount,
    long EvictionCount,
    long BudgetPressureCount,
    double LastSetupMilliseconds);

public interface IRuntimeAssetResidencyService
{
    RuntimeAssetResidencyBudgets Budgets { get; }
    RuntimeAssetResidencyLease AcquireSceneDependencies(
        RuntimeAssetResidencyOwnerId owner,
        IReadOnlyList<CookedSceneDependency> dependencies,
        bool pinned,
        CancellationToken cancellationToken = default);
    void RegisterPreparedProvider(IRuntimePreparedAssetProvider provider);
    bool UnregisterPreparedProvider(string providerId);
    void ProcessAtFrameBoundary();
    IReadOnlyList<RuntimeAssetResidencySnapshot> GetResources();
    RuntimeAssetResidencyMetrics GetMetrics();
}

public sealed class RuntimeAssetResidencyLease : IDisposable
{
    private RuntimeAssetResidencyService? m_Service;

    internal RuntimeAssetResidencyLease(
        RuntimeAssetResidencyService service,
        RuntimeAssetResidencyOwnerId owner)
    {
        m_Service = service;
        Owner = owner;
    }

    public RuntimeAssetResidencyOwnerId Owner { get; }

    public RuntimePreparedAssetState State =>
        Volatile.Read(ref m_Service)?.GetOwnerState(Owner, out _) ?? RuntimePreparedAssetState.Failed;

    public string Diagnostic
    {
        get
        {
            RuntimeAssetResidencyService? service = Volatile.Read(ref m_Service);
            if (service == null) return "Runtime asset residency lease has been released.";
            service.GetOwnerState(Owner, out string diagnostic);
            return diagnostic;
        }
    }

    public void SetPinned(bool pinned)
    {
        Volatile.Read(ref m_Service)?.SetOwnerPinned(Owner, pinned);
    }

    public void Dispose()
    {
        RuntimeAssetResidencyService? service = Interlocked.Exchange(ref m_Service, null);
        service?.ReleaseOwner(Owner);
    }
}

public sealed class RuntimeAssetResidencyService : IRuntimeAssetResidencyService, IDisposable
{
    private readonly object m_Gate = new();
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly SortedDictionary<RuntimeAssetResidencyKey, ResourceEntry> m_Resources = new();
    private readonly SortedDictionary<RuntimeAssetResidencyOwnerId, OwnerEntry> m_Owners = new();
    private readonly SortedDictionary<string, IRuntimePreparedAssetProvider> m_Providers =
        new(StringComparer.Ordinal);
    private long m_NextSequence;
    private long m_CpuCookedBytes;
    private long m_PreparedGpuBytes;
    private long m_PeakCpuCookedBytes;
    private long m_PeakPreparedGpuBytes;
    private long m_SetupCount;
    private long m_SetupFailureCount;
    private long m_EvictionCount;
    private long m_BudgetPressureCount;
    private double m_LastSetupMilliseconds;
    private bool m_Disposed;

    public RuntimeAssetResidencyService(
        IAssetDatabase assetDatabase,
        RuntimeAssetResidencyBudgets? budgets = null)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        Budgets = budgets ?? RuntimeAssetResidencyBudgets.Default;
        Budgets.Validate();
    }

    public RuntimeAssetResidencyBudgets Budgets { get; }

    public RuntimeAssetResidencyLease AcquireSceneDependencies(
        RuntimeAssetResidencyOwnerId owner,
        IReadOnlyList<CookedSceneDependency> dependencies,
        bool pinned,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!owner.IsValid)
        {
            throw new ArgumentException("Runtime asset residency owner identity is invalid.", nameof(owner));
        }

        ArgumentNullException.ThrowIfNull(dependencies);
        var plan = BuildDependencyPlan(dependencies);
        lock (m_Gate)
        {
            if (m_Owners.ContainsKey(owner))
            {
                throw new InvalidOperationException($"Runtime asset residency owner '{owner}' is already acquired.");
            }
        }

        var acquired = new List<RuntimeAssetResidencyKey>(plan.Count);
        try
        {
            foreach ((RuntimeAssetResidencyKey key, bool required) in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryAcquireResource(owner, key, pinned, out string diagnostic))
                {
                    if (required)
                    {
                        throw new InvalidDataException(
                            $"Required runtime asset '{key}' could not be acquired. {diagnostic}");
                    }

                    Logger.Warning($"[RuntimeAssetResidency] Optional runtime asset '{key}' was skipped. {diagnostic}");
                    continue;
                }

                acquired.Add(key);
            }

            lock (m_Gate)
            {
                m_Owners.Add(owner, new OwnerEntry(
                    acquired.ToArray(),
                    plan.Where(pair => pair.Value).Select(pair => pair.Key).ToHashSet(),
                    pinned));
            }

            return new RuntimeAssetResidencyLease(this, owner);
        }
        catch
        {
            RollbackOwnerResources(owner, acquired, pinned);
            throw;
        }
    }

    public void RegisterPreparedProvider(IRuntimePreparedAssetProvider provider)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(provider.ProviderId))
        {
            throw new ArgumentException("Prepared asset provider id cannot be empty.", nameof(provider));
        }

        lock (m_Gate)
        {
            if (!m_Providers.TryAdd(provider.ProviderId, provider))
            {
                throw new InvalidOperationException(
                    $"Prepared asset provider '{provider.ProviderId}' is already registered.");
            }
        }
    }

    public bool UnregisterPreparedProvider(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return false;
        List<RuntimeAssetResidencyKey> releases;
        IRuntimePreparedAssetProvider? provider;
        lock (m_Gate)
        {
            if (!m_Providers.Remove(providerId, out provider)) return false;
            releases = m_Resources.Values
                .Where(entry => string.Equals(entry.ProviderId, providerId, StringComparison.Ordinal))
                .Select(entry => entry.Key)
                .ToList();
            foreach (RuntimeAssetResidencyKey key in releases)
            {
                ResourceEntry entry = m_Resources[key];
                m_PreparedGpuBytes -= entry.EstimatedGpuBytes;
                entry.EstimatedGpuBytes = 0;
                entry.ProviderId = string.Empty;
                entry.State = RuntimePreparedAssetState.Waiting;
                entry.Diagnostic = "Prepared asset provider was unregistered.";
            }
        }

        foreach (RuntimeAssetResidencyKey key in releases) provider.Release(key);
        lock (m_Gate) RefreshPreparedGpuBytesLocked();
        return true;
    }

    public void ProcessAtFrameBoundary()
    {
        ThrowIfDisposed();
        using var _ = Profiler.Zone("AssetResidency.FrameBoundary");
        ProcessSetups();
        EvictInactiveResources();
        PlotMetrics();
    }

    public IReadOnlyList<RuntimeAssetResidencySnapshot> GetResources()
    {
        lock (m_Gate)
        {
            return m_Resources.Values.Select(entry => entry.Snapshot()).ToArray();
        }
    }

    public RuntimeAssetResidencyMetrics GetMetrics()
    {
        lock (m_Gate) return BuildMetricsLocked();
    }

    public void Dispose()
    {
        List<(RuntimeAssetResidencyKey Key, IRuntimePreparedAssetProvider? Provider)> releases;
        CookedAssetHandle[] handles;
        lock (m_Gate)
        {
            if (m_Disposed) return;
            m_Disposed = true;
            releases = m_Resources.Values
                .Select(entry => (
                    entry.Key,
                    FindProviderByIdLocked(entry.ProviderId)))
                .ToList();
            handles = m_Resources.Values
                .Where(entry => entry.CpuHandle.IsValid)
                .Select(entry => entry.CpuHandle)
                .ToArray();
            m_Resources.Clear();
            m_Owners.Clear();
            m_Providers.Clear();
            m_CpuCookedBytes = 0;
            m_PreparedGpuBytes = 0;
        }

        foreach ((RuntimeAssetResidencyKey key, IRuntimePreparedAssetProvider? provider) in releases)
        {
            provider?.Release(key);
        }

        foreach (CookedAssetHandle handle in handles) m_AssetDatabase.Release(handle);
    }

    internal RuntimePreparedAssetState GetOwnerState(
        RuntimeAssetResidencyOwnerId owner,
        out string diagnostic)
    {
        lock (m_Gate)
        {
            if (!m_Owners.TryGetValue(owner, out OwnerEntry? ownerEntry))
            {
                diagnostic = "Runtime asset residency owner is no longer acquired.";
                return RuntimePreparedAssetState.Failed;
            }

            foreach (RuntimeAssetResidencyKey key in ownerEntry.RequiredKeys.Order())
            {
                if (!m_Resources.TryGetValue(key, out ResourceEntry? resource))
                {
                    diagnostic = $"Required runtime asset '{key}' is no longer resident.";
                    return RuntimePreparedAssetState.Failed;
                }

                if (resource.State == RuntimePreparedAssetState.Failed)
                {
                    diagnostic = resource.Diagnostic.Length > 0
                        ? resource.Diagnostic
                        : $"Required runtime asset '{key}' preparation failed.";
                    return RuntimePreparedAssetState.Failed;
                }

                if (resource.State != RuntimePreparedAssetState.Ready)
                {
                    diagnostic = $"Required runtime asset '{key}' is waiting for prepared resources.";
                    return RuntimePreparedAssetState.Waiting;
                }
            }

            diagnostic = string.Empty;
            return RuntimePreparedAssetState.Ready;
        }
    }

    internal void SetOwnerPinned(RuntimeAssetResidencyOwnerId owner, bool pinned)
    {
        lock (m_Gate)
        {
            if (!m_Owners.TryGetValue(owner, out OwnerEntry? ownerEntry) || ownerEntry.Pinned == pinned)
            {
                return;
            }

            ownerEntry.Pinned = pinned;
            foreach (RuntimeAssetResidencyKey key in ownerEntry.Keys)
            {
                if (m_Resources.TryGetValue(key, out ResourceEntry? resource))
                {
                    resource.PinnedOwnerCount += pinned ? 1 : -1;
                }
            }
        }
    }

    internal void ReleaseOwner(RuntimeAssetResidencyOwnerId owner)
    {
        lock (m_Gate)
        {
            if (!m_Owners.Remove(owner, out OwnerEntry? ownerEntry)) return;
            foreach (RuntimeAssetResidencyKey key in ownerEntry.Keys)
            {
                if (!m_Resources.TryGetValue(key, out ResourceEntry? resource)) continue;
                resource.Owners.Remove(owner);
                if (ownerEntry.Pinned) resource.PinnedOwnerCount--;
                if (resource.Owners.Count == 0)
                {
                    resource.LastNeededSequence = ++m_NextSequence;
                }
            }
        }
    }

    private SortedDictionary<RuntimeAssetResidencyKey, bool> BuildDependencyPlan(
        IReadOnlyList<CookedSceneDependency> dependencies)
    {
        var plan = new SortedDictionary<RuntimeAssetResidencyKey, bool>();
        for (int index = 0; index < dependencies.Count; index++)
        {
            CookedSceneDependency dependency = dependencies[index];
            if (!RuntimeAssetVariantPolicy.TryResolve(dependency.AssetType, out string variant))
            {
                if (dependency.Required)
                {
                    throw new InvalidDataException(
                        $"Required scene dependency '{dependency.Guid:D}' has unsupported asset type " +
                        $"'{dependency.AssetType}' and no deterministic runtime variant policy.");
                }

                continue;
            }

            var key = new RuntimeAssetResidencyKey(
                dependency.Guid,
                dependency.PackageId.Trim(),
                dependency.AssetType,
                variant);
            if (!key.IsValid)
            {
                throw new InvalidDataException(
                    $"Scene dependency at index {index} does not define a complete runtime identity.");
            }

            plan[key] = plan.TryGetValue(key, out bool existingRequired)
                ? existingRequired || dependency.Required
                : dependency.Required;
        }

        return plan;
    }

    private bool TryAcquireResource(
        RuntimeAssetResidencyOwnerId owner,
        RuntimeAssetResidencyKey key,
        bool pinned,
        out string diagnostic)
    {
        lock (m_Gate)
        {
            if (m_Resources.TryGetValue(key, out ResourceEntry? existing))
            {
                AddOwnerLocked(existing, owner, pinned);
                diagnostic = string.Empty;
                return true;
            }
        }

        if (!m_AssetDatabase.TryGetAssetDescriptor(key.Guid, out AssetDescriptor descriptor) ||
            !string.Equals(descriptor.AssetType, key.AssetType, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(descriptor.PackageId, key.PackageId, StringComparison.OrdinalIgnoreCase))
        {
            diagnostic = "Asset descriptor identity or package ownership does not match the scene metadata.";
            return false;
        }

        CookedAssetHandle handle = CookedAssetHandle.Invalid;
        long cpuBytes = 0;
        bool sourceBacked = false;
        if (m_AssetDatabase.TryGetCookedArtifact(key.Guid, key.Variant, out CookedAssetRecord? artifact))
        {
            if (!m_AssetDatabase.TryLoadCookedAsset(
                    key.Guid,
                    key.Variant,
                    key.AssetType,
                    out handle))
            {
                diagnostic = "The declared cooked artifact could not be loaded or validated.";
                return false;
            }

            cpuBytes = artifact.SizeInBytes;
        }
        else if (m_AssetDatabase.CanReadSourceAssets)
        {
            sourceBacked = true;
        }
        else
        {
            diagnostic = "The cooked artifact is unavailable and source fallback is disabled.";
            return false;
        }

        lock (m_Gate)
        {
            if (m_Resources.TryGetValue(key, out ResourceEntry? raced))
            {
                if (handle.IsValid) m_AssetDatabase.Release(handle);
                AddOwnerLocked(raced, owner, pinned);
                diagnostic = string.Empty;
                return true;
            }

            if (cpuBytes > Budgets.MaxCpuCookedBytes ||
                m_CpuCookedBytes > Budgets.MaxCpuCookedBytes - cpuBytes)
            {
                if (handle.IsValid) m_AssetDatabase.Release(handle);
                m_BudgetPressureCount++;
                diagnostic =
                    $"Acquiring {cpuBytes} bytes would exceed the CPU cooked residency budget " +
                    $"of {Budgets.MaxCpuCookedBytes} bytes.";
                return false;
            }

            var entry = new ResourceEntry(key, handle, cpuBytes, sourceBacked)
            {
                LastNeededSequence = ++m_NextSequence
            };
            AddOwnerLocked(entry, owner, pinned);
            m_Resources.Add(key, entry);
            m_CpuCookedBytes += cpuBytes;
            m_PeakCpuCookedBytes = Math.Max(m_PeakCpuCookedBytes, m_CpuCookedBytes);
        }

        diagnostic = string.Empty;
        return true;
    }

    private void RollbackOwnerResources(
        RuntimeAssetResidencyOwnerId owner,
        IReadOnlyList<RuntimeAssetResidencyKey> acquired,
        bool pinned)
    {
        lock (m_Gate)
        {
            foreach (RuntimeAssetResidencyKey key in acquired)
            {
                if (!m_Resources.TryGetValue(key, out ResourceEntry? resource)) continue;
                if (!resource.Owners.Remove(owner)) continue;
                if (pinned) resource.PinnedOwnerCount--;
                if (resource.Owners.Count == 0) resource.LastNeededSequence = ++m_NextSequence;
            }
        }
    }

    private static void AddOwnerLocked(
        ResourceEntry resource,
        RuntimeAssetResidencyOwnerId owner,
        bool pinned)
    {
        if (!resource.Owners.Add(owner))
        {
            throw new InvalidOperationException(
                $"Runtime asset '{resource.Key}' already belongs to residency owner '{owner}'.");
        }

        if (pinned) resource.PinnedOwnerCount++;
    }

    private void ProcessSetups()
    {
        long frameStarted = Stopwatch.GetTimestamp();
        int setupCount = 0;
        while (setupCount < Budgets.MaxSetupsPerFrame &&
               Stopwatch.GetElapsedTime(frameStarted).TotalMilliseconds < Budgets.MaxSetupMilliseconds)
        {
            ResourceEntry? entry;
            IRuntimePreparedAssetProvider? provider;
            lock (m_Gate)
            {
                entry = m_Resources.Values
                    .Where(candidate =>
                        candidate.Owners.Count > 0 &&
                        candidate.State == RuntimePreparedAssetState.Waiting)
                    .OrderByDescending(candidate => candidate.PinnedOwnerCount > 0)
                    .ThenBy(candidate => candidate.LastNeededSequence)
                    .ThenBy(candidate => candidate.Key)
                    .FirstOrDefault(candidate => FindProviderLocked(candidate.Key.AssetType) != null);
                if (entry == null) break;
                provider = FindProviderLocked(entry.Key.AssetType);
            }

            long started = Stopwatch.GetTimestamp();
            RuntimePreparedAssetResult result;
            try
            {
                using var _ = Profiler.Zone("AssetResidency.PrepareResource");
                result = provider!.Prepare(entry.Key);
            }
            catch (Exception ex)
            {
                result = RuntimePreparedAssetResult.Failed(
                    $"Prepared provider '{provider!.ProviderId}' threw for '{entry.Key}': {ex.Message}");
            }

            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            lock (m_Gate)
            {
                m_LastSetupMilliseconds = elapsed;
                m_SetupCount++;
                if (!m_Resources.TryGetValue(entry.Key, out ResourceEntry? current) ||
                    !ReferenceEquals(current, entry))
                {
                    provider!.Release(entry.Key);
                    continue;
                }

                current.Diagnostic = result.Diagnostic;
                if (result.State == RuntimePreparedAssetState.Ready)
                {
                    current.State = RuntimePreparedAssetState.Ready;
                    current.ProviderId = provider!.ProviderId;
                    current.EstimatedGpuBytes = result.EstimatedGpuBytes;
                    m_PreparedGpuBytes += result.EstimatedGpuBytes;
                    m_PeakPreparedGpuBytes = Math.Max(m_PeakPreparedGpuBytes, m_PreparedGpuBytes);
                    if (m_PreparedGpuBytes > Budgets.MaxPreparedGpuBytes) m_BudgetPressureCount++;
                }
                else if (result.State == RuntimePreparedAssetState.Failed)
                {
                    current.State = RuntimePreparedAssetState.Failed;
                    current.ProviderId = provider!.ProviderId;
                    m_SetupFailureCount++;
                }
            }

            setupCount++;
            if (result.State == RuntimePreparedAssetState.Waiting) break;
        }

        lock (m_Gate) RefreshPreparedGpuBytesLocked();
    }

    private void EvictInactiveResources()
    {
        var evictions = new List<(RuntimeAssetResidencyKey Key, CookedAssetHandle Handle, IRuntimePreparedAssetProvider? Provider)>();
        lock (m_Gate)
        {
            ResourceEntry[] inactive = m_Resources.Values
                .Where(entry => entry.Owners.Count == 0)
                .OrderBy(entry => entry.LastNeededSequence)
                .ThenBy(entry => entry.Key)
                .ToArray();
            int remainingInactive = inactive.Length;
            foreach (ResourceEntry entry in inactive)
            {
                bool overCount = remainingInactive > Budgets.MaxInactiveResources;
                bool overCpu = m_CpuCookedBytes > Budgets.MaxCpuCookedBytes;
                bool overGpu = m_PreparedGpuBytes > Budgets.MaxPreparedGpuBytes;
                if (!overCount && !overCpu && !overGpu) break;

                m_Resources.Remove(entry.Key);
                m_CpuCookedBytes -= entry.CpuCookedBytes;
                m_PreparedGpuBytes -= entry.EstimatedGpuBytes;
                evictions.Add((entry.Key, entry.CpuHandle, FindProviderByIdLocked(entry.ProviderId)));
                m_EvictionCount++;
                remainingInactive--;
            }

            if ((m_CpuCookedBytes > Budgets.MaxCpuCookedBytes ||
                 m_PreparedGpuBytes > Budgets.MaxPreparedGpuBytes) &&
                evictions.Count == 0)
            {
                m_BudgetPressureCount++;
            }
        }

        foreach ((RuntimeAssetResidencyKey key, CookedAssetHandle handle, IRuntimePreparedAssetProvider? provider) in evictions)
        {
            provider?.Release(key);
            if (handle.IsValid) m_AssetDatabase.Release(handle);
        }

        lock (m_Gate) RefreshPreparedGpuBytesLocked();
    }

    private IRuntimePreparedAssetProvider? FindProviderLocked(string assetType)
    {
        foreach (IRuntimePreparedAssetProvider provider in m_Providers.Values)
        {
            if (provider.Supports(assetType)) return provider;
        }

        return null;
    }

    private IRuntimePreparedAssetProvider? FindProviderByIdLocked(string providerId)
    {
        return providerId.Length > 0 && m_Providers.TryGetValue(providerId, out var provider)
            ? provider
            : null;
    }

    private RuntimeAssetResidencyMetrics BuildMetricsLocked()
    {
        int pendingDisposals = 0;
        int preparedDescriptors = 0;
        foreach (IRuntimePreparedAssetProvider provider in m_Providers.Values)
        {
            RuntimePreparedAssetProviderMetrics providerMetrics = provider.GetMetrics();
            pendingDisposals += providerMetrics.PendingDisposalCount;
            preparedDescriptors += providerMetrics.DescriptorCount;
        }

        return new RuntimeAssetResidencyMetrics(
            m_Resources.Count,
            m_Owners.Count,
            m_Resources.Values.Count(entry => entry.State == RuntimePreparedAssetState.Waiting),
            m_Resources.Values.Count(entry => entry.State == RuntimePreparedAssetState.Ready),
            m_Resources.Values.Count(entry => entry.State == RuntimePreparedAssetState.Failed),
            m_Resources.Values.Count(entry => entry.Owners.Count == 0),
            m_Resources.Values.Count(entry => entry.PinnedOwnerCount > 0),
            m_CpuCookedBytes,
            m_PreparedGpuBytes,
            m_PeakCpuCookedBytes,
            m_PeakPreparedGpuBytes,
            preparedDescriptors,
            pendingDisposals,
            m_SetupCount,
            m_SetupFailureCount,
            m_EvictionCount,
            m_BudgetPressureCount,
            m_LastSetupMilliseconds);
    }

    private void RefreshPreparedGpuBytesLocked()
    {
        long preparedGpuBytes = 0;
        foreach (IRuntimePreparedAssetProvider provider in m_Providers.Values)
        {
            preparedGpuBytes = checked(
                preparedGpuBytes + Math.Max(0, provider.GetMetrics().EstimatedGpuBytes));
        }

        m_PreparedGpuBytes = preparedGpuBytes;
        m_PeakPreparedGpuBytes = Math.Max(m_PeakPreparedGpuBytes, preparedGpuBytes);
        if (preparedGpuBytes > Budgets.MaxPreparedGpuBytes) m_BudgetPressureCount++;
    }

    private void PlotMetrics()
    {
        RuntimeAssetResidencyMetrics metrics = GetMetrics();
        Profiler.PlotValue("AssetResidency.Resources", metrics.ResidentAssetCount);
        Profiler.PlotValue("AssetResidency.Owners", metrics.ActiveOwnerCount);
        Profiler.PlotValue("AssetResidency.Waiting", metrics.WaitingAssetCount);
        Profiler.PlotValue("AssetResidency.Ready", metrics.ReadyAssetCount);
        Profiler.PlotValue("AssetResidency.Failed", metrics.FailedAssetCount);
        Profiler.PlotValue("AssetResidency.CpuCookedBytes", metrics.CpuCookedBytes);
        Profiler.PlotValue("AssetResidency.PreparedGpuBytes", metrics.PreparedGpuBytes);
        Profiler.PlotValue("AssetResidency.PreparedDescriptors", metrics.PreparedDescriptorCount);
        Profiler.PlotValue("AssetResidency.PendingDisposal", metrics.PendingDisposalCount);
        Profiler.PlotValue("AssetResidency.BudgetPressure", metrics.BudgetPressureCount);
        Profiler.PlotValue("AssetResidency.LastSetupMs", metrics.LastSetupMilliseconds);
    }

    private void ThrowIfDisposed()
    {
        if (m_Disposed) throw new ObjectDisposedException(nameof(RuntimeAssetResidencyService));
    }

    private sealed class OwnerEntry
    {
        public OwnerEntry(
            RuntimeAssetResidencyKey[] keys,
            HashSet<RuntimeAssetResidencyKey> requiredKeys,
            bool pinned)
        {
            Keys = keys;
            RequiredKeys = requiredKeys;
            Pinned = pinned;
        }

        public RuntimeAssetResidencyKey[] Keys { get; }
        public HashSet<RuntimeAssetResidencyKey> RequiredKeys { get; }
        public bool Pinned { get; set; }
    }

    private sealed class ResourceEntry
    {
        public ResourceEntry(
            RuntimeAssetResidencyKey key,
            CookedAssetHandle cpuHandle,
            long cpuCookedBytes,
            bool sourceBacked)
        {
            Key = key;
            CpuHandle = cpuHandle;
            CpuCookedBytes = cpuCookedBytes;
            SourceBacked = sourceBacked;
        }

        public RuntimeAssetResidencyKey Key { get; }
        public CookedAssetHandle CpuHandle { get; }
        public long CpuCookedBytes { get; }
        public bool SourceBacked { get; }
        public HashSet<RuntimeAssetResidencyOwnerId> Owners { get; } = new();
        public int PinnedOwnerCount { get; set; }
        public RuntimePreparedAssetState State { get; set; } = RuntimePreparedAssetState.Waiting;
        public long EstimatedGpuBytes { get; set; }
        public long LastNeededSequence { get; set; }
        public string ProviderId { get; set; } = string.Empty;
        public string Diagnostic { get; set; } = string.Empty;

        public RuntimeAssetResidencySnapshot Snapshot() => new(
            Key,
            State,
            Owners.Count,
            PinnedOwnerCount,
            CpuCookedBytes,
            EstimatedGpuBytes,
            LastNeededSequence,
            SourceBacked,
            ProviderId,
            Diagnostic);
    }
}
