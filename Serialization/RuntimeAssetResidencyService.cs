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
    public static RuntimeAssetResidencyOwnerId Persistent(Guid worldGuid, long generation) =>
        new(worldGuid, default, generation, RuntimeAssetResidencyOwnerKind.PersistentScene);

    public static RuntimeAssetResidencyOwnerId Cell(
        Guid worldGuid,
        WorldCellId cellId,
        long generation) =>
        new(worldGuid, cellId, generation, RuntimeAssetResidencyOwnerKind.WorldCell);

    public bool IsValid =>
        WorldGuid != Guid.Empty &&
        Enum.IsDefined(Kind) &&
        !CellId.IsValid == (Kind == RuntimeAssetResidencyOwnerKind.PersistentScene) &&
        Generation > 0;

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

public readonly record struct RuntimeAssetPreparationClaim(
    RuntimeAssetResidencyKey Key,
    long ResidencySequence,
    long OwnerPlanGeneration,
    CookedAssetHandle CookedHandle)
{
    public bool IsValid =>
        Key.IsValid &&
        ResidencySequence > 0 &&
        OwnerPlanGeneration > 0 &&
        CookedHandle.IsValid &&
        CookedHandle.Guid == Key.Guid &&
        string.Equals(CookedHandle.Variant, Key.Variant, StringComparison.OrdinalIgnoreCase);
}

public sealed class RuntimePreparedPublicationInvalidatedException : InvalidOperationException
{
    public RuntimePreparedPublicationInvalidatedException(string message)
        : base(message)
    {
    }
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
    string Diagnostic,
    IReadOnlyList<RuntimeAssetResidencyOwnerId> Owners);

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

    /// <summary>
    /// Returns true while the exact provider instance remains registered, including cleanup drain.
    /// </summary>
    bool IsPreparedProviderRegistered(IRuntimePreparedAssetProvider provider);
    bool UnregisterPreparedProvider(string providerId);
    bool InvalidatePreparedProvider(string providerId, string diagnostic);
    bool TryGetPreparationClaim(
        RuntimeAssetResidencyKey key,
        out RuntimeAssetPreparationClaim claim);
    bool TryBindPreparationDependencies(
        in RuntimeAssetPreparationClaim claim,
        IReadOnlyList<RuntimeAssetResidencyKey> canonicalRequiredKeys,
        out string diagnostic);
    bool TryCommitPreparedPublication(
        in RuntimeAssetPreparationClaim claim,
        IReadOnlyList<RuntimeAssetPreparationClaim> canonicalRequiredClaims,
        IReadOnlyList<RuntimeAssetResidencyKey> canonicalRequiredKeys,
        long estimatedGpuBytes,
        Action publish,
        out string diagnostic);
    void ProcessAtFrameBoundary();
    IReadOnlyList<RuntimeAssetResidencySnapshot> GetResources();
    RuntimeAssetResidencyMetrics GetMetrics();
}

public sealed class RuntimeAssetResidencyLease : IDisposable
{
    private const int ReleaseActive = 0;
    private const int ReleaseInProgress = 1;
    private const int ReleaseComplete = 2;

    private RuntimeAssetResidencyService? m_Service;
    private int m_ReleaseState;

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
        var spinner = new SpinWait();
        while (true)
        {
            int state = Volatile.Read(ref m_ReleaseState);
            if (state == ReleaseComplete)
            {
                return;
            }

            if (state == ReleaseActive &&
                Interlocked.CompareExchange(
                    ref m_ReleaseState,
                    ReleaseInProgress,
                    ReleaseActive) == ReleaseActive)
            {
                break;
            }

            spinner.SpinOnce();
        }

        RuntimeAssetResidencyService? service = Volatile.Read(ref m_Service);
        try
        {
            service?.ReleaseOwner(Owner);
            Volatile.Write(ref m_Service, null);
            Volatile.Write(ref m_ReleaseState, ReleaseComplete);
        }
        catch
        {
            Volatile.Write(ref m_ReleaseState, ReleaseActive);
            throw;
        }
    }
}

public sealed class RuntimeAssetResidencyService : IRuntimeAssetResidencyService, IDisposable
{
    private readonly object m_Gate = new();
    private readonly object m_ProviderLifecycleGate = new();
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly SortedDictionary<RuntimeAssetResidencyKey, ResourceEntry> m_Resources = new();
    private readonly SortedDictionary<RuntimeAssetResidencyOwnerId, OwnerEntry> m_Owners = new();
    private readonly SortedDictionary<string, ProviderRegistration> m_Providers =
        new(StringComparer.Ordinal);
    private readonly List<PendingResourceCleanup> m_PendingCleanups = new();
    private readonly HashSet<int> m_ProviderLifecycleCallbackThreadIds = new();
    private readonly HashSet<int> m_AcquisitionThreadIds = new();
    private readonly HashSet<int> m_PreparedPublicationThreadIds = new();
    private int m_InFlightAcquisitionCount;
    private int m_InFlightPreparedPublicationCount;
    private int m_FrameBoundaryActive;
    private long m_NextSequence;
    private long m_NextOwnerPlanGeneration;
    private long m_NextSetupPass;
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

    internal bool IsDisposed
    {
        get
        {
            lock (m_Gate) return m_Disposed;
        }
    }

    internal void EnsureWorldLifecycleMutationAllowed()
    {
        int threadId = Environment.CurrentManagedThreadId;
        lock (m_Gate)
        {
            bool isPrepareCallback = false;
            foreach (ProviderRegistration registration in m_Providers.Values)
            {
                if (registration.InFlightThreadIds.Contains(threadId))
                {
                    isPrepareCallback = true;
                    break;
                }
            }

            if (isPrepareCallback ||
                m_PreparedPublicationThreadIds.Contains(threadId) ||
                m_AcquisitionThreadIds.Contains(threadId) ||
                m_ProviderLifecycleCallbackThreadIds.Contains(threadId))
            {
                throw new InvalidOperationException(
                    "Runtime world lifecycle mutation cannot run reentrantly from an asset " +
                    "residency callback.");
            }
        }
    }

    public RuntimeAssetResidencyLease AcquireSceneDependencies(
        RuntimeAssetResidencyOwnerId owner,
        IReadOnlyList<CookedSceneDependency> dependencies,
        bool pinned,
        CancellationToken cancellationToken = default)
    {
        if (!owner.IsValid)
        {
            throw new ArgumentException("Runtime asset residency owner identity is invalid.", nameof(owner));
        }

        ArgumentNullException.ThrowIfNull(dependencies);
        var plan = BuildDependencyPlan(dependencies);
        var ownerEntry = new OwnerEntry(
            plan.Where(pair => pair.Value).Select(pair => pair.Key).ToHashSet(),
            pinned);
        lock (m_Gate)
        {
            ThrowIfDisposed();
            if (m_PreparedPublicationThreadIds.Contains(Environment.CurrentManagedThreadId))
            {
                throw new InvalidOperationException(
                    "Runtime asset residency acquisition cannot run reentrantly from a prepared " +
                    "publication callback.");
            }

            BeginAcquisitionLocked();
            if (!m_Owners.TryAdd(owner, ownerEntry))
            {
                EndAcquisitionLocked();
                throw new InvalidOperationException($"Runtime asset residency owner '{owner}' is already acquired.");
            }
        }

        var acquired = new List<RuntimeAssetResidencyKey>(plan.Count);
        try
        {
            foreach ((RuntimeAssetResidencyKey key, bool required) in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryAcquireResource(owner, ownerEntry, key, out string diagnostic))
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
                ThrowIfDisposed();
                if (!m_Owners.TryGetValue(owner, out OwnerEntry? current) ||
                    !ReferenceEquals(current, ownerEntry))
                {
                    throw new InvalidOperationException(
                        $"Runtime asset residency owner '{owner}' changed during acquisition.");
                }

                ownerEntry.CompleteAcquisition(acquired.ToArray());
            }

            return new RuntimeAssetResidencyLease(this, owner);
        }
        catch
        {
            RollbackOwnerResources(owner, ownerEntry, acquired);
            throw;
        }
        finally
        {
            lock (m_Gate)
            {
                EndAcquisitionLocked();
            }
        }
    }

    public void RegisterPreparedProvider(IRuntimePreparedAssetProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(provider.ProviderId))
        {
            throw new ArgumentException("Prepared asset provider id cannot be empty.", nameof(provider));
        }

        EnsureNoProviderCallbackOnCurrentThread("register a prepared provider");
        lock (m_ProviderLifecycleGate)
        {
            ThrowIfDisposed();
            lock (m_Gate)
            {
                if (!m_Providers.TryAdd(provider.ProviderId, new ProviderRegistration(provider)))
                {
                    throw new InvalidOperationException(
                        $"Prepared asset provider '{provider.ProviderId}' is already registered.");
                }
            }
        }
    }

    public bool IsPreparedProviderRegistered(IRuntimePreparedAssetProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        lock (m_Gate)
        {
            foreach (ProviderRegistration registration in m_Providers.Values)
            {
                if (ReferenceEquals(registration.Provider, provider))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public bool UnregisterPreparedProvider(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return false;
        EnsureNoProviderCallbackOnCurrentThread("unregister a prepared provider");
        lock (m_ProviderLifecycleGate)
        {
            var cleanupFailures = new List<Exception>();
            ProviderReleaseTarget[] releases;
            ProviderRegistration registration;
            lock (m_Gate)
            {
                if (!m_Providers.TryGetValue(providerId, out registration!))
                {
                    return false;
                }

                EnsureProviderLifecycleCanDrainLocked(registration, "unregister");
                if (registration.AdmissionOpen)
                {
                    registration.AdmissionOpen = false;
                    registration.PendingOperation = ProviderLifecycleOperation.Unregister;
                    InvalidateProviderEntriesLocked(
                        registration.Provider,
                        "Prepared asset provider was unregistered.");
                }
                else if (registration.PendingOperation == ProviderLifecycleOperation.Invalidate)
                {
                    registration.PendingOperation = ProviderLifecycleOperation.Unregister;
                }
                else if (registration.PendingOperation != ProviderLifecycleOperation.Unregister)
                {
                    throw new InvalidOperationException(
                        $"Prepared asset provider '{providerId}' is already draining for " +
                        $"'{registration.PendingOperation}'.");
                }

                DrainProviderCallsLocked(registration);
                releases = GetProviderReleaseTargetsLocked(registration.Provider);
            }

            ReleaseProviderTargets(
                registration.Provider,
                releases,
                "unregister",
                cleanupFailures);
            TryRefreshPreparedGpuBytes("unregister", cleanupFailures);

            lock (m_Gate)
            {
                if (cleanupFailures.Count == 0 &&
                    !HasPendingProviderCleanupLocked(registration.Provider))
                {
                    if (!m_Providers.Remove(providerId, out ProviderRegistration? removed) ||
                        !ReferenceEquals(removed, registration))
                    {
                        cleanupFailures.Add(new InvalidOperationException(
                            $"Prepared asset provider '{providerId}' changed while it was draining."));
                    }
                }
            }

            ThrowCleanupFailures(
                $"Prepared asset provider '{providerId}' unregister cleanup failed.",
                cleanupFailures);
            return true;
        }
    }

    public bool InvalidatePreparedProvider(string providerId, string diagnostic)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return false;
        EnsureNoProviderCallbackOnCurrentThread("invalidate a prepared provider");
        lock (m_ProviderLifecycleGate)
        {
            var cleanupFailures = new List<Exception>();
            ProviderReleaseTarget[] releases;
            ProviderRegistration registration;
            lock (m_Gate)
            {
                if (!m_Providers.TryGetValue(providerId, out registration!))
                {
                    return false;
                }

                EnsureProviderLifecycleCanDrainLocked(registration, "invalidate");
                if (registration.AdmissionOpen)
                {
                    registration.AdmissionOpen = false;
                    registration.PendingOperation = ProviderLifecycleOperation.Invalidate;
                    InvalidateProviderEntriesLocked(
                        registration.Provider,
                        string.IsNullOrWhiteSpace(diagnostic)
                            ? "Prepared asset resources were invalidated."
                            : diagnostic.Trim());
                }
                else if (registration.PendingOperation != ProviderLifecycleOperation.Invalidate)
                {
                    throw new InvalidOperationException(
                        $"Prepared asset provider '{providerId}' is already draining for " +
                        $"'{registration.PendingOperation}'.");
                }

                DrainProviderCallsLocked(registration);
                releases = GetProviderReleaseTargetsLocked(registration.Provider);
            }

            ReleaseProviderTargets(
                registration.Provider,
                releases,
                "invalidate",
                cleanupFailures);
            TryRefreshPreparedGpuBytes("invalidate", cleanupFailures);

            lock (m_Gate)
            {
                if (cleanupFailures.Count == 0 &&
                    !HasPendingProviderCleanupLocked(registration.Provider) &&
                    !m_Disposed &&
                    m_Providers.TryGetValue(providerId, out ProviderRegistration? current) &&
                    ReferenceEquals(current, registration))
                {
                    registration.PendingOperation = ProviderLifecycleOperation.None;
                    registration.AdmissionOpen = true;
                }
            }

            ThrowCleanupFailures(
                $"Prepared asset provider '{providerId}' invalidation cleanup failed.",
                cleanupFailures);
            return true;
        }
    }

    public bool TryGetPreparationClaim(
        RuntimeAssetResidencyKey key,
        out RuntimeAssetPreparationClaim claim)
    {
        if (!key.IsValid)
        {
            claim = default;
            return false;
        }

        lock (m_Gate)
        {
            if (!m_Disposed &&
                m_Resources.TryGetValue(key, out ResourceEntry? resource) &&
                resource.Owners.Count > 0 &&
                HasOnlyCompleteOwnersLocked(resource) &&
                resource.LifecycleReleaseProvider == null &&
                resource.CpuHandle.IsValid)
            {
                claim = new RuntimeAssetPreparationClaim(
                    key,
                    resource.LastNeededSequence,
                    resource.OwnerPlanGeneration,
                    resource.CpuHandle);
                return true;
            }
        }

        claim = default;
        return false;
    }

    public bool TryBindPreparationDependencies(
        in RuntimeAssetPreparationClaim claim,
        IReadOnlyList<RuntimeAssetResidencyKey> canonicalRequiredKeys,
        out string diagnostic)
    {
        if (!claim.IsValid)
        {
            diagnostic = "Runtime asset preparation claim is invalid.";
            return false;
        }

        if (!TryValidateCanonicalRequiredKeys(
                claim.Key,
                canonicalRequiredKeys,
                out diagnostic))
        {
            return false;
        }

        lock (m_Gate)
        {
            if (m_Disposed ||
                !m_Resources.TryGetValue(claim.Key, out ResourceEntry? resource) ||
                resource.Owners.Count == 0 ||
                !HasOnlyCompleteOwnersLocked(resource) ||
                resource.LifecycleReleaseProvider != null ||
                resource.LastNeededSequence != claim.ResidencySequence ||
                resource.OwnerPlanGeneration != claim.OwnerPlanGeneration ||
                resource.CpuHandle != claim.CookedHandle)
            {
                diagnostic =
                    $"Runtime asset preparation claim for '{claim.Key}' is no longer current.";
                return false;
            }

            if (resource.BoundRequiredKeys != null &&
                !resource.BoundRequiredKeys.SequenceEqual(canonicalRequiredKeys))
            {
                diagnostic =
                    $"Runtime asset '{claim.Key}' exact cooked handle is already bound to a " +
                    "different decoded dependency set.";
                return false;
            }

            foreach (RuntimeAssetResidencyOwnerId owner in resource.Owners.Order())
            {
                OwnerEntry ownerEntry = m_Owners[owner];
                for (int index = 0; index < canonicalRequiredKeys.Count; index++)
                {
                    RuntimeAssetResidencyKey requiredKey = canonicalRequiredKeys[index];
                    if (!ownerEntry.RequiredKeys.Contains(requiredKey))
                    {
                        diagnostic =
                            $"Runtime asset residency owner '{owner}' does not require decoded " +
                            $"dependency '{requiredKey}' of '{claim.Key}'.";
                        return false;
                    }
                }
            }

            resource.BoundRequiredKeys ??= canonicalRequiredKeys.ToArray();
            diagnostic = string.Empty;
            return true;
        }
    }

    public bool TryCommitPreparedPublication(
        in RuntimeAssetPreparationClaim claim,
        IReadOnlyList<RuntimeAssetPreparationClaim> canonicalRequiredClaims,
        IReadOnlyList<RuntimeAssetResidencyKey> canonicalRequiredKeys,
        long estimatedGpuBytes,
        Action publish,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(publish);
        if (!claim.IsValid)
        {
            diagnostic = "Runtime asset preparation claim is invalid.";
            return false;
        }

        if (!TryValidateCanonicalRequiredKeys(
                claim.Key,
                canonicalRequiredKeys,
                out diagnostic))
        {
            return false;
        }

        if (canonicalRequiredClaims == null ||
            canonicalRequiredClaims.Count != canonicalRequiredKeys.Count)
        {
            diagnostic =
                "Prepared publication requires one generation-qualified claim for every " +
                "canonical decoded dependency.";
            return false;
        }

        ResourceEntry resource;
        IRuntimePreparedAssetProvider provider;
        long setupGeneration;
        lock (m_Gate)
        {
            if (!TryGetCurrentPreparationResourceLocked(claim, out ResourceEntry currentResource))
            {
                diagnostic =
                    $"Runtime asset preparation claim for '{claim.Key}' is no longer current.";
                return false;
            }

            resource = currentResource;
            provider = resource.PreparingProvider!;
            int threadId = Environment.CurrentManagedThreadId;
            if (resource.PreparingProvider == null ||
                !ReferenceEquals(resource.InFlightProvider, provider) ||
                resource.State != RuntimePreparedAssetState.Waiting ||
                resource.CommittedSetupGeneration != 0 ||
                !m_Providers.TryGetValue(provider.ProviderId, out ProviderRegistration? registration) ||
                !ReferenceEquals(registration.Provider, provider) ||
                !registration.AdmissionOpen ||
                registration.PendingOperation != ProviderLifecycleOperation.None ||
                !registration.InFlightThreadIds.Contains(threadId))
            {
                diagnostic =
                    $"Runtime asset '{claim.Key}' has no current provider preparation admission " +
                    "for publication.";
                return false;
            }

            if (resource.BoundRequiredKeys != null &&
                !resource.BoundRequiredKeys.SequenceEqual(canonicalRequiredKeys))
            {
                diagnostic =
                    $"Runtime asset '{claim.Key}' exact cooked handle is already bound to a " +
                    "different decoded dependency set.";
                return false;
            }

            foreach (RuntimeAssetResidencyOwnerId owner in resource.Owners.Order())
            {
                OwnerEntry ownerEntry = m_Owners[owner];
                for (int index = 0; index < canonicalRequiredKeys.Count; index++)
                {
                    RuntimeAssetResidencyKey requiredKey = canonicalRequiredKeys[index];
                    if (!ownerEntry.RequiredKeys.Contains(requiredKey))
                    {
                        diagnostic =
                            $"Runtime asset residency owner '{owner}' does not require decoded " +
                            $"dependency '{requiredKey}' of '{claim.Key}'.";
                        return false;
                    }
                }
            }

            for (int index = 0; index < canonicalRequiredClaims.Count; index++)
            {
                RuntimeAssetPreparationClaim requiredClaim = canonicalRequiredClaims[index];
                if (requiredClaim.Key != canonicalRequiredKeys[index])
                {
                    diagnostic =
                        "Prepared publication dependency claims must match the canonical decoded " +
                        "dependency order.";
                    return false;
                }

                if (!TryGetCurrentPreparationResourceLocked(requiredClaim, out _))
                {
                    diagnostic =
                        $"Prepared publication dependency claim for '{requiredClaim.Key}' is no " +
                        "longer current.";
                    return false;
                }
            }

            resource.BoundRequiredKeys ??= canonicalRequiredKeys.ToArray();
            setupGeneration = resource.SetupGeneration;
            BeginPreparedPublicationLocked();
        }

        try
        {
            publish();
        }
        catch
        {
            lock (m_Gate)
            {
                EndPreparedPublicationLocked();
            }

            throw;
        }

        lock (m_Gate)
        {
            try
            {
                if (!TryGetCurrentPreparationResourceLocked(claim, out ResourceEntry current) ||
                    !ReferenceEquals(current, resource) ||
                    current.SetupGeneration != setupGeneration ||
                    !ReferenceEquals(current.PreparingProvider, provider) ||
                    !ReferenceEquals(current.InFlightProvider, provider) ||
                    current.State != RuntimePreparedAssetState.Waiting ||
                    !m_Providers.TryGetValue(
                        provider.ProviderId,
                        out ProviderRegistration? registration) ||
                    !ReferenceEquals(registration.Provider, provider) ||
                    !registration.AdmissionOpen ||
                    registration.PendingOperation != ProviderLifecycleOperation.None)
                {
                    throw new RuntimePreparedPublicationInvalidatedException(
                        $"Runtime asset '{claim.Key}' publication admission changed while its " +
                        "provider callback was committing data.");
                }

                for (int index = 0; index < canonicalRequiredClaims.Count; index++)
                {
                    RuntimeAssetPreparationClaim requiredClaim =
                        canonicalRequiredClaims[index];
                    if (requiredClaim.Key != canonicalRequiredKeys[index] ||
                        !TryGetCurrentPreparationResourceLocked(requiredClaim, out _))
                    {
                        throw new RuntimePreparedPublicationInvalidatedException(
                            $"Runtime asset '{claim.Key}' dependency claim for " +
                            $"'{requiredClaim.Key}' changed while its provider callback was " +
                            "committing data.");
                    }
                }

                if (resource.BoundRequiredKeys == null ||
                    !resource.BoundRequiredKeys.SequenceEqual(canonicalRequiredKeys))
                {
                    throw new RuntimePreparedPublicationInvalidatedException(
                        $"Runtime asset '{claim.Key}' decoded dependency binding changed during " +
                        "prepared publication.");
                }

                resource.State = RuntimePreparedAssetState.Ready;
                resource.EstimatedGpuBytes = Math.Max(0, estimatedGpuBytes);
                resource.ProviderId = provider.ProviderId;
                resource.Diagnostic = string.Empty;
                resource.CommittedSetupGeneration = resource.SetupGeneration;
                m_PreparedGpuBytes = checked(m_PreparedGpuBytes + resource.EstimatedGpuBytes);
                m_PeakPreparedGpuBytes = Math.Max(m_PeakPreparedGpuBytes, m_PreparedGpuBytes);
                if (m_PreparedGpuBytes > Budgets.MaxPreparedGpuBytes)
                {
                    m_BudgetPressureCount++;
                }

                diagnostic = string.Empty;
                return true;
            }
            finally
            {
                EndPreparedPublicationLocked();
            }
        }
    }

    public void ProcessAtFrameBoundary()
    {
        if (Interlocked.CompareExchange(ref m_FrameBoundaryActive, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "Runtime asset residency frame-boundary processing is already active.");
        }

        try
        {
            EnsureNoProviderCallbackOnCurrentThread("process a residency frame boundary");
            ThrowIfDisposed();
            using var _ = Profiler.Zone("AssetResidency.FrameBoundary");
            ProcessSetups();
            EvictInactiveResources();
            PlotMetrics();
        }
        finally
        {
            Volatile.Write(ref m_FrameBoundaryActive, 0);
        }
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
        EnsureNoProviderCallbackOnCurrentThread("read residency provider metrics");
        lock (m_ProviderLifecycleGate)
        {
            RuntimePreparedAssetProviderMetrics[] providerMetrics =
                GetProviderMetricsSnapshot();
            lock (m_Gate)
            {
                UpdatePreparedGpuBytesLocked(providerMetrics);
                return BuildMetricsLocked(providerMetrics);
            }
        }
    }

    public void Dispose()
    {
        EnsureNoProviderCallbackOnCurrentThread("dispose runtime asset residency");
        EnsureNoAcquisitionOnCurrentThread("dispose runtime asset residency");
        lock (m_ProviderLifecycleGate)
        {
            PendingResourceCleanup[] cleanups;
            lock (m_Gate)
            {
                if (!m_Disposed)
                {
                    foreach (ProviderRegistration registration in m_Providers.Values)
                    {
                        EnsureProviderLifecycleCanDrainLocked(registration, "dispose");
                    }

                    m_Disposed = true;
                    foreach (ProviderRegistration registration in m_Providers.Values)
                    {
                        registration.AdmissionOpen = false;
                        registration.PendingOperation = ProviderLifecycleOperation.Dispose;
                    }

                    DrainAcquisitionsLocked();

                    TransferProviderReleasesToLifecycleLocked();
                    foreach (ProviderRegistration registration in m_Providers.Values)
                    {
                        DrainProviderCallsLocked(registration);
                    }

                    foreach (ResourceEntry entry in m_Resources.Values)
                    {
                        AddPendingCleanupLocked(
                            entry.Key,
                            entry.CpuHandle,
                            entry.CpuCookedBytes,
                            entry.EstimatedGpuBytes,
                            ResolveEntryProviderLocked(entry));
                    }

                    m_Resources.Clear();
                    m_Owners.Clear();
                }

                if (m_PendingCleanups.Count == 0)
                {
                    m_Providers.Clear();
                    m_CpuCookedBytes = 0;
                    m_PreparedGpuBytes = 0;
                    return;
                }

                cleanups = m_PendingCleanups.ToArray();
            }

            var cleanupFailures = new List<Exception>();
            ExecutePendingCleanups(cleanups, "dispose", cleanupFailures);
            lock (m_Gate)
            {
                RemoveCompletedCleanupsLocked();
                if (m_PendingCleanups.Count == 0)
                {
                    m_Providers.Clear();
                    m_CpuCookedBytes = 0;
                    m_PreparedGpuBytes = 0;
                }
            }

            ThrowCleanupFailures(
                "Runtime asset residency disposal cleanup failed.",
                cleanupFailures);
        }
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
            DrainPreparedPublicationsLocked();
            if (!m_Owners.Remove(owner, out OwnerEntry? ownerEntry)) return;
            foreach (RuntimeAssetResidencyKey key in ownerEntry.Keys)
            {
                if (!m_Resources.TryGetValue(key, out ResourceEntry? resource)) continue;
                if (!resource.Owners.Remove(owner)) continue;
                resource.OwnerPlanGeneration = NextOwnerPlanGenerationLocked();
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
            string variant = dependency.Variant;
            if (string.IsNullOrEmpty(variant) &&
                !RuntimeAssetVariantPolicy.TryResolve(dependency.AssetType, out variant))
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
        OwnerEntry ownerEntry,
        RuntimeAssetResidencyKey key,
        out string diagnostic)
    {
        lock (m_Gate)
        {
            EnsureOwnerAcquisitionActiveLocked(owner, ownerEntry);
            DrainPreparedPublicationsLocked();
            if (HasBlockingCleanupForKeyLocked(key))
            {
                diagnostic =
                    "A previous runtime asset generation is still completing deterministic cleanup.";
                return false;
            }

            if (m_Resources.TryGetValue(key, out ResourceEntry? existing))
            {
                return TryAddOwnerLocked(existing, owner, ownerEntry, out diagnostic);
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

        bool acquired = false;
        bool retainHandle = false;
        bool racedOwnerAdded = false;
        ResourceEntry? racedResource = null;
        diagnostic = string.Empty;
        try
        {
            lock (m_Gate)
            {
                EnsureOwnerAcquisitionActiveLocked(owner, ownerEntry);
                DrainPreparedPublicationsLocked();
                if (HasBlockingCleanupForKeyLocked(key))
                {
                    diagnostic =
                        "A previous runtime asset generation is still completing deterministic cleanup.";
                }
                else if (m_Resources.TryGetValue(key, out ResourceEntry? raced))
                {
                    racedResource = raced;
                    acquired = TryAddOwnerLocked(raced, owner, ownerEntry, out diagnostic);
                    racedOwnerAdded = acquired;
                }
                else if (cpuBytes > Budgets.MaxCpuCookedBytes ||
                         m_CpuCookedBytes > Budgets.MaxCpuCookedBytes - cpuBytes)
                {
                    m_BudgetPressureCount++;
                    diagnostic =
                        $"Acquiring {cpuBytes} bytes would exceed the CPU cooked residency budget " +
                        $"of {Budgets.MaxCpuCookedBytes} bytes.";
                }
                else
                {
                    var entry = new ResourceEntry(key, handle, cpuBytes, sourceBacked)
                    {
                        LastNeededSequence = ++m_NextSequence
                    };
                    acquired = TryAddOwnerLocked(entry, owner, ownerEntry, out diagnostic);
                    if (acquired)
                    {
                        m_Resources.Add(key, entry);
                        m_CpuCookedBytes = checked(m_CpuCookedBytes + cpuBytes);
                        m_PeakCpuCookedBytes = Math.Max(
                            m_PeakCpuCookedBytes,
                            m_CpuCookedBytes);
                        retainHandle = true;
                    }
                }
            }
        }
        catch (Exception acquisitionFailure)
        {
            Exception? cleanupFailure = null;
            try
            {
                ReleaseTemporaryCookedHandle(
                    key,
                    handle,
                    cpuBytes,
                    blocksKeyReuse: racedResource == null,
                    operation: "failed acquisition");
            }
            catch (Exception ex)
            {
                cleanupFailure = ex;
            }

            if (racedOwnerAdded && racedResource != null)
            {
                lock (m_Gate)
                {
                    RollbackRacedOwnerLocked(racedResource, owner, ownerEntry);
                }
            }

            if (cleanupFailure != null)
            {
                throw new AggregateException(
                    $"Runtime asset '{key}' acquisition and temporary-handle cleanup failed.",
                    acquisitionFailure,
                    cleanupFailure);
            }

            throw;
        }

        if (!retainHandle)
        {
            try
            {
                ReleaseTemporaryCookedHandle(
                    key,
                    handle,
                    cpuBytes,
                    blocksKeyReuse: racedResource == null,
                    operation: "temporary acquisition");
            }
            catch
            {
                if (racedOwnerAdded && racedResource != null)
                {
                    lock (m_Gate)
                    {
                        RollbackRacedOwnerLocked(racedResource, owner, ownerEntry);
                    }
                }

                throw;
            }
        }

        return acquired;
    }

    private void RollbackOwnerResources(
        RuntimeAssetResidencyOwnerId owner,
        OwnerEntry ownerEntry,
        IReadOnlyList<RuntimeAssetResidencyKey> acquired)
    {
        lock (m_Gate)
        {
            DrainPreparedPublicationsLocked();
            if (m_Owners.TryGetValue(owner, out OwnerEntry? current) &&
                ReferenceEquals(current, ownerEntry))
            {
                m_Owners.Remove(owner);
            }

            foreach (RuntimeAssetResidencyKey key in acquired)
            {
                if (!m_Resources.TryGetValue(key, out ResourceEntry? resource)) continue;
                if (!resource.Owners.Remove(owner)) continue;
                resource.OwnerPlanGeneration = NextOwnerPlanGenerationLocked();
                if (ownerEntry.Pinned) resource.PinnedOwnerCount--;
                if (resource.Owners.Count == 0) resource.LastNeededSequence = ++m_NextSequence;
            }
        }
    }

    private bool TryAddOwnerLocked(
        ResourceEntry resource,
        RuntimeAssetResidencyOwnerId owner,
        OwnerEntry ownerEntry,
        out string diagnostic)
    {
        if (resource.LifecycleReleaseProvider != null)
        {
            diagnostic =
                $"Runtime asset '{resource.Key}' is still releasing a stale prepared generation.";
            return false;
        }

        if (resource.BoundRequiredKeys != null)
        {
            for (int index = 0; index < resource.BoundRequiredKeys.Length; index++)
            {
                RuntimeAssetResidencyKey requiredKey = resource.BoundRequiredKeys[index];
                if (!ownerEntry.RequiredKeys.Contains(requiredKey))
                {
                    diagnostic =
                        $"Owner '{owner}' cannot share runtime asset '{resource.Key}' because its " +
                        $"required plan omits decoded dependency '{requiredKey}'.";
                    return false;
                }
            }
        }

        if (!resource.Owners.Add(owner))
        {
            throw new InvalidOperationException(
                $"Runtime asset '{resource.Key}' already belongs to residency owner '{owner}'.");
        }

        resource.OwnerPlanGeneration = NextOwnerPlanGenerationLocked();
        if (ownerEntry.Pinned) resource.PinnedOwnerCount++;
        diagnostic = string.Empty;
        return true;
    }

    private void EnsureOwnerAcquisitionActiveLocked(
        RuntimeAssetResidencyOwnerId owner,
        OwnerEntry ownerEntry)
    {
        ThrowIfDisposed();
        if (!m_Owners.TryGetValue(owner, out OwnerEntry? current) ||
            !ReferenceEquals(current, ownerEntry) ||
            ownerEntry.AcquisitionComplete)
        {
            throw new InvalidOperationException(
                $"Runtime asset residency owner '{owner}' is not actively acquiring resources.");
        }
    }

    private void BeginAcquisitionLocked()
    {
        int threadId = Environment.CurrentManagedThreadId;
        if (!m_AcquisitionThreadIds.Add(threadId))
        {
            throw new InvalidOperationException(
                "Runtime asset residency acquisition cannot run reentrantly on one thread.");
        }

        m_InFlightAcquisitionCount = checked(m_InFlightAcquisitionCount + 1);
    }

    private void EndAcquisitionLocked()
    {
        int threadId = Environment.CurrentManagedThreadId;
        if (m_InFlightAcquisitionCount <= 0 || !m_AcquisitionThreadIds.Remove(threadId))
        {
            throw new InvalidOperationException(
                "Runtime asset residency acquisition ownership was lost.");
        }

        m_InFlightAcquisitionCount--;
        Monitor.PulseAll(m_Gate);
    }

    private void EnsureNoAcquisitionOnCurrentThread(string operation)
    {
        int threadId = Environment.CurrentManagedThreadId;
        lock (m_Gate)
        {
            if (m_AcquisitionThreadIds.Contains(threadId))
            {
                throw new InvalidOperationException(
                    $"Runtime asset residency cannot {operation} reentrantly from an asset " +
                    "acquisition callback.");
            }
        }
    }

    private void DrainAcquisitionsLocked()
    {
        while (m_InFlightAcquisitionCount != 0)
        {
            Monitor.Wait(m_Gate);
        }
    }

    private void BeginPreparedPublicationLocked()
    {
        int threadId = Environment.CurrentManagedThreadId;
        if (m_InFlightPreparedPublicationCount != 0 ||
            !m_PreparedPublicationThreadIds.Add(threadId))
        {
            throw new InvalidOperationException(
                "Runtime prepared publication cannot run concurrently or reentrantly.");
        }

        m_InFlightPreparedPublicationCount = 1;
    }

    private void EndPreparedPublicationLocked()
    {
        int threadId = Environment.CurrentManagedThreadId;
        if (m_InFlightPreparedPublicationCount != 1 ||
            !m_PreparedPublicationThreadIds.Remove(threadId))
        {
            throw new InvalidOperationException(
                "Runtime prepared publication ownership was lost.");
        }

        m_InFlightPreparedPublicationCount = 0;
        Monitor.PulseAll(m_Gate);
    }

    private void DrainPreparedPublicationsLocked()
    {
        int threadId = Environment.CurrentManagedThreadId;
        if (m_PreparedPublicationThreadIds.Contains(threadId))
        {
            throw new InvalidOperationException(
                "Runtime asset residency ownership cannot mutate reentrantly from a prepared " +
                "publication callback.");
        }

        while (m_InFlightPreparedPublicationCount != 0)
        {
            Monitor.Wait(m_Gate);
        }
    }

    private void RollbackRacedOwnerLocked(
        ResourceEntry resource,
        RuntimeAssetResidencyOwnerId owner,
        OwnerEntry ownerEntry)
    {
        DrainPreparedPublicationsLocked();
        if (!m_Resources.TryGetValue(resource.Key, out ResourceEntry? current) ||
            !ReferenceEquals(current, resource) ||
            !resource.Owners.Remove(owner))
        {
            return;
        }

        resource.OwnerPlanGeneration = NextOwnerPlanGenerationLocked();
        if (ownerEntry.Pinned) resource.PinnedOwnerCount--;
        if (resource.Owners.Count == 0)
        {
            resource.LastNeededSequence = ++m_NextSequence;
        }
    }

    private bool HasOnlyCompleteOwnersLocked(ResourceEntry resource)
    {
        foreach (RuntimeAssetResidencyOwnerId owner in resource.Owners)
        {
            if (!m_Owners.TryGetValue(owner, out OwnerEntry? ownerEntry) ||
                !ownerEntry.AcquisitionComplete)
            {
                return false;
            }
        }

        return true;
    }

    private bool TryGetCurrentPreparationResourceLocked(
        in RuntimeAssetPreparationClaim claim,
        out ResourceEntry resource)
    {
        if (!m_Disposed &&
            m_Resources.TryGetValue(claim.Key, out ResourceEntry? current) &&
            current.Owners.Count > 0 &&
            HasOnlyCompleteOwnersLocked(current) &&
            current.LifecycleReleaseProvider == null &&
            current.LastNeededSequence == claim.ResidencySequence &&
            current.OwnerPlanGeneration == claim.OwnerPlanGeneration &&
            current.CpuHandle == claim.CookedHandle)
        {
            resource = current;
            return true;
        }

        resource = null!;
        return false;
    }

    private long NextOwnerPlanGenerationLocked() =>
        checked(++m_NextOwnerPlanGeneration);

    private static bool TryValidateCanonicalRequiredKeys(
        RuntimeAssetResidencyKey preparationKey,
        IReadOnlyList<RuntimeAssetResidencyKey>? canonicalRequiredKeys,
        out string diagnostic)
    {
        if (canonicalRequiredKeys == null)
        {
            diagnostic = "Decoded runtime asset dependency set is null.";
            return false;
        }

        RuntimeAssetResidencyKey previous = default;
        for (int index = 0; index < canonicalRequiredKeys.Count; index++)
        {
            RuntimeAssetResidencyKey key = canonicalRequiredKeys[index];
            if (!key.IsValid)
            {
                diagnostic =
                    $"Decoded runtime asset dependency at index {index} is invalid.";
                return false;
            }

            if (key == preparationKey)
            {
                diagnostic =
                    $"Runtime asset '{preparationKey}' cannot require itself during preparation.";
                return false;
            }

            if (index > 0 && previous.CompareTo(key) >= 0)
            {
                diagnostic =
                    "Decoded runtime asset dependencies must be unique and ordered by canonical " +
                    "runtime residency identity.";
                return false;
            }

            previous = key;
        }

        diagnostic = string.Empty;
        return true;
    }

    private void ProcessSetups()
    {
        long frameStarted = Stopwatch.GetTimestamp();
        int setupCount = 0;
        long setupPass;
        lock (m_Gate)
        {
            setupPass = checked(++m_NextSetupPass);
        }

        while (setupCount < Budgets.MaxSetupsPerFrame &&
               Stopwatch.GetElapsedTime(frameStarted).TotalMilliseconds < Budgets.MaxSetupMilliseconds)
        {
            if (!TryBeginProviderSetup(setupPass, out ProviderSetupAdmission admission))
            {
                break;
            }

            ResourceEntry entry = admission.Entry;
            ProviderRegistration registration = admission.Registration;
            IRuntimePreparedAssetProvider provider = registration.Provider;
            long setupGeneration = admission.SetupGeneration;
            long residencySequence = admission.ResidencySequence;
            long ownerPlanGeneration = admission.OwnerPlanGeneration;
            string providerId = provider.ProviderId;
            int providerCallThreadId = Environment.CurrentManagedThreadId;

            bool releaseStaleResult = false;
            try
            {
                long started = Stopwatch.GetTimestamp();
                RuntimePreparedAssetResult result;
                try
                {
                    using var _ = Profiler.Zone("AssetResidency.PrepareResource");
                    result = provider.Prepare(entry.Key);
                }
                catch (Exception ex)
                {
                    result = RuntimePreparedAssetResult.Failed(
                        $"Prepared provider '{provider.ProviderId}' threw for '{entry.Key}': {ex.Message}");
                }

                double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                lock (m_Gate)
                {
                    m_LastSetupMilliseconds = elapsed;
                    m_SetupCount++;
                    bool providerStillCurrent =
                        m_Providers.TryGetValue(providerId, out ProviderRegistration? registered) &&
                        ReferenceEquals(registered, registration) &&
                        registration.AdmissionOpen &&
                        registration.PendingOperation == ProviderLifecycleOperation.None;
                    bool publicationCommitted =
                        m_Resources.TryGetValue(entry.Key, out ResourceEntry? current) &&
                        ReferenceEquals(current, entry) &&
                        current.SetupGeneration == setupGeneration &&
                        current.CommittedSetupGeneration == setupGeneration &&
                        ReferenceEquals(current.PreparingProvider, provider) &&
                        current.State == RuntimePreparedAssetState.Ready &&
                        providerStillCurrent;
                    if (publicationCommitted)
                    {
                        current!.PreparingProvider = null;
                        current.InFlightProvider = null;
                        current.CommittedSetupGeneration = 0;
                        if (result.State != RuntimePreparedAssetState.Ready ||
                            result.EstimatedGpuBytes != current.EstimatedGpuBytes)
                        {
                            current.State = RuntimePreparedAssetState.Waiting;
                            current.Diagnostic =
                                $"Prepared provider '{providerId}' did not return the Ready result " +
                                "committed with its atomic publication.";
                            current.LifecycleReleaseProvider = provider;
                            releaseStaleResult = true;
                            m_SetupFailureCount++;
                        }
                    }
                    else if (!m_Resources.TryGetValue(entry.Key, out current) ||
                        !ReferenceEquals(current, entry) ||
                        current.SetupGeneration != setupGeneration ||
                        current.LastNeededSequence != residencySequence ||
                        current.OwnerPlanGeneration != ownerPlanGeneration ||
                        !ReferenceEquals(current.PreparingProvider, provider) ||
                        current.State != RuntimePreparedAssetState.Waiting ||
                        current.Owners.Count == 0 ||
                        !providerStillCurrent)
                    {
                        if (current != null && ReferenceEquals(current, entry))
                        {
                            if (ReferenceEquals(current.PreparingProvider, provider))
                            {
                                current.PreparingProvider = null;
                            }
                        }

                        releaseStaleResult =
                            !ReferenceEquals(entry.LifecycleReleaseProvider, provider);
                        if (releaseStaleResult &&
                            current != null &&
                            ReferenceEquals(current, entry))
                        {
                            bool estimateAlreadyAccounted =
                                current.State == RuntimePreparedAssetState.Ready;
                            long previousEstimate = current.EstimatedGpuBytes;
                            long retainedEstimate = Math.Max(
                                previousEstimate,
                                result.EstimatedGpuBytes);
                            if (estimateAlreadyAccounted)
                            {
                                m_PreparedGpuBytes = checked(
                                    m_PreparedGpuBytes + retainedEstimate - previousEstimate);
                            }
                            else
                            {
                                m_PreparedGpuBytes = checked(
                                    m_PreparedGpuBytes + retainedEstimate);
                            }

                            current.EstimatedGpuBytes = retainedEstimate;
                            if (current.State == RuntimePreparedAssetState.Ready)
                            {
                                current.State = RuntimePreparedAssetState.Waiting;
                            }

                            current.CommittedSetupGeneration = 0;
                            current.ProviderId = providerId;
                            current.LifecycleReleaseProvider = provider;
                        }
                    }
                    else
                    {
                        current.PreparingProvider = null;
                        current.InFlightProvider = null;
                        current.Diagnostic = result.Diagnostic;
                        current.ProviderId = providerId;
                        if (result.State == RuntimePreparedAssetState.Ready)
                        {
                            current.State = RuntimePreparedAssetState.Ready;
                            current.EstimatedGpuBytes = result.EstimatedGpuBytes;
                            m_PreparedGpuBytes += result.EstimatedGpuBytes;
                            m_PeakPreparedGpuBytes = Math.Max(
                                m_PeakPreparedGpuBytes,
                                m_PreparedGpuBytes);
                            if (m_PreparedGpuBytes > Budgets.MaxPreparedGpuBytes)
                            {
                                m_BudgetPressureCount++;
                            }
                        }
                        else if (result.State == RuntimePreparedAssetState.Failed)
                        {
                            current.State = RuntimePreparedAssetState.Failed;
                            m_SetupFailureCount++;
                        }
                        else if (result.State == RuntimePreparedAssetState.Waiting)
                        {
                            // Keep the resource Waiting. LastSetupPass prevents a
                            // retry until the next frame-boundary setup pass.
                        }
                    }
                }

                setupCount++;
            }
            finally
            {
                lock (m_Gate)
                {
                    if (m_Resources.TryGetValue(entry.Key, out ResourceEntry? current) &&
                        ReferenceEquals(current, entry) &&
                        ReferenceEquals(current.InFlightProvider, provider))
                    {
                        current.InFlightProvider = null;
                    }

                    EndProviderCallLocked(registration, providerCallThreadId);
                }
            }

            if (releaseStaleResult)
            {
                lock (m_ProviderLifecycleGate)
                {
                    ProviderReleaseTarget[] releases;
                    lock (m_Gate)
                    {
                        releases = m_Resources.TryGetValue(
                                entry.Key,
                                out ResourceEntry? current) &&
                            ReferenceEquals(current, entry) &&
                            ReferenceEquals(current.LifecycleReleaseProvider, provider)
                                ? [new ProviderReleaseTarget(entry.Key, current, null)]
                                : Array.Empty<ProviderReleaseTarget>();
                    }

                    var cleanupFailures = new List<Exception>();
                    ReleaseProviderTargets(
                        provider,
                        releases,
                        "stale preparation",
                        cleanupFailures);
                    TryRefreshPreparedGpuBytes(
                        "stale preparation",
                        cleanupFailures);

                    ThrowCleanupFailures(
                        $"Prepared provider '{provider.ProviderId}' stale-result cleanup failed.",
                        cleanupFailures);
                }
            }

            // The pass stamp keeps an explicit Waiting result from consuming
            // the remaining setup budget in this frame.
        }

        lock (m_ProviderLifecycleGate)
        {
            lock (m_Gate)
            {
                if (m_Disposed) return;
            }

            RefreshPreparedGpuBytes();
        }
    }

    private void EvictInactiveResources()
    {
        lock (m_ProviderLifecycleGate)
        {
            var cleanupFailures = new List<Exception>();
            ProviderReleaseBatch[] lifecycleReleases;
            lock (m_Gate)
            {
                if (m_Disposed) return;
                var releasesByProvider =
                    new Dictionary<IRuntimePreparedAssetProvider, List<ProviderReleaseTarget>>(
                        ReferenceEqualityComparer.Instance);
                foreach (ResourceEntry entry in m_Resources.Values)
                {
                    IRuntimePreparedAssetProvider? provider = entry.LifecycleReleaseProvider;
                    if (provider == null ||
                        !IsProviderActiveForFrameBoundaryRetryLocked(provider) ||
                        entry.PreparingProvider != null ||
                        entry.InFlightProvider != null)
                    {
                        continue;
                    }

                    if (!releasesByProvider.TryGetValue(
                            provider,
                            out List<ProviderReleaseTarget>? targets))
                    {
                        targets = new List<ProviderReleaseTarget>();
                        releasesByProvider.Add(provider, targets);
                    }

                    targets.Add(new ProviderReleaseTarget(entry.Key, entry, null));
                }

                lifecycleReleases = releasesByProvider
                    .OrderBy(pair => pair.Key.ProviderId, StringComparer.Ordinal)
                    .Select(pair => new ProviderReleaseBatch(
                        pair.Key,
                        pair.Value
                            .OrderBy(target => target.Key)
                            .ToArray()))
                    .ToArray();
            }

            for (int index = 0; index < lifecycleReleases.Length; index++)
            {
                ProviderReleaseBatch batch = lifecycleReleases[index];
                ReleaseProviderTargets(
                    batch.Provider,
                    batch.Targets,
                    "frame-boundary lifecycle retry",
                    cleanupFailures);
            }

            TryRefreshPreparedGpuBytes("evict", cleanupFailures);

            PendingResourceCleanup[] cleanups;
            lock (m_Gate)
            {
                RemoveCompletedCleanupsLocked();

                int remainingInactive = m_Resources.Values.Count(entry => entry.Owners.Count == 0);
                long projectedCpuCookedBytes = m_CpuCookedBytes;
                long projectedPreparedGpuBytes = m_PreparedGpuBytes;
                foreach (PendingResourceCleanup cleanup in m_PendingCleanups)
                {
                    if (cleanup.CookedHandleReleasePending &&
                        cleanup.CpuCookedBytesAccounted)
                    {
                        projectedCpuCookedBytes = Math.Max(
                            0,
                            projectedCpuCookedBytes - cleanup.CpuCookedBytes);
                    }

                    if (cleanup.ProviderReleasePending)
                    {
                        projectedPreparedGpuBytes = Math.Max(
                            0,
                            projectedPreparedGpuBytes - cleanup.EstimatedGpuBytes);
                    }
                }

                ResourceEntry[] inactive = m_Resources.Values
                    .Where(entry =>
                        entry.Owners.Count == 0 &&
                        entry.PreparingProvider == null &&
                        entry.InFlightProvider == null &&
                        entry.LifecycleReleaseProvider == null)
                    .OrderBy(entry => entry.LastNeededSequence)
                    .ThenBy(entry => entry.Key)
                    .ToArray();
                foreach (ResourceEntry entry in inactive)
                {
                    bool overCount = remainingInactive > Budgets.MaxInactiveResources;
                    bool overCpu = projectedCpuCookedBytes > Budgets.MaxCpuCookedBytes;
                    bool overGpu = projectedPreparedGpuBytes > Budgets.MaxPreparedGpuBytes;
                    if (!overCount && !overCpu && !overGpu) break;

                    m_Resources.Remove(entry.Key);
                    IRuntimePreparedAssetProvider? provider =
                        FindProviderByIdLocked(entry.ProviderId);
                    AddPendingCleanupLocked(
                        entry.Key,
                        entry.CpuHandle,
                        entry.CpuCookedBytes,
                        entry.EstimatedGpuBytes,
                        provider);
                    if (entry.CpuHandle.IsValid)
                    {
                        projectedCpuCookedBytes = Math.Max(
                            0,
                            projectedCpuCookedBytes - entry.CpuCookedBytes);
                    }

                    if (provider != null)
                    {
                        projectedPreparedGpuBytes = Math.Max(
                            0,
                            projectedPreparedGpuBytes - entry.EstimatedGpuBytes);
                    }

                    m_EvictionCount++;
                    remainingInactive--;
                }

                if (remainingInactive > Budgets.MaxInactiveResources ||
                    projectedCpuCookedBytes > Budgets.MaxCpuCookedBytes ||
                    projectedPreparedGpuBytes > Budgets.MaxPreparedGpuBytes)
                {
                    m_BudgetPressureCount++;
                }

                cleanups = m_PendingCleanups.ToArray();
            }

            ExecutePendingCleanups(cleanups, "evict", cleanupFailures);
            TryRefreshPreparedGpuBytes("evict", cleanupFailures);

            lock (m_Gate)
            {
                RemoveCompletedCleanupsLocked();
            }

            ThrowCleanupFailures(
                "Runtime asset residency eviction cleanup failed.",
                cleanupFailures);
        }
    }

    private bool TryBeginProviderSetup(
        long setupPass,
        out ProviderSetupAdmission admission)
    {
        lock (m_ProviderLifecycleGate)
        {
            ResourceEntry[] candidates;
            ProviderRegistration[] registrations;
            lock (m_Gate)
            {
                if (m_Disposed)
                {
                    admission = default;
                    return false;
                }

                candidates = m_Resources.Values
                    .Where(candidate => IsSetupCandidateLocked(candidate, setupPass))
                    .OrderByDescending(candidate => candidate.PinnedOwnerCount > 0)
                    .ThenBy(candidate => candidate.LastNeededSequence)
                    .ThenBy(candidate => candidate.Key)
                    .ToArray();
                registrations = m_Providers.Values
                    .Where(IsProviderPrepareAdmissionOpenLocked)
                    .ToArray();
            }

            foreach (ResourceEntry candidate in candidates)
            {
                foreach (ProviderRegistration registration in registrations)
                {
                    if (!ProviderSupportsAssetType(registration.Provider, candidate.Key.AssetType))
                    {
                        continue;
                    }

                    lock (m_Gate)
                    {
                        if (!IsSetupCandidateLocked(candidate, setupPass) ||
                            !m_Providers.TryGetValue(
                                registration.Provider.ProviderId,
                                out ProviderRegistration? current) ||
                            !ReferenceEquals(current, registration) ||
                            !IsProviderPrepareAdmissionOpenLocked(registration))
                        {
                            continue;
                        }

                        int threadId = Environment.CurrentManagedThreadId;
                        BeginProviderCallLocked(registration, threadId);
                        long setupGeneration = checked(++candidate.SetupGeneration);
                        candidate.LastSetupPass = setupPass;
                        candidate.PreparingProvider = registration.Provider;
                        candidate.InFlightProvider = registration.Provider;
                        admission = new ProviderSetupAdmission(
                            candidate,
                            registration,
                            setupGeneration,
                            candidate.LastNeededSequence,
                            candidate.OwnerPlanGeneration);
                        return true;
                    }
                }
            }

            admission = default;
            return false;
        }
    }

    private bool IsSetupCandidateLocked(ResourceEntry candidate, long setupPass) =>
        m_Resources.TryGetValue(candidate.Key, out ResourceEntry? current) &&
        ReferenceEquals(current, candidate) &&
        candidate.Owners.Count > 0 &&
        HasOnlyCompleteOwnersLocked(candidate) &&
        candidate.State == RuntimePreparedAssetState.Waiting &&
        candidate.PreparingProvider == null &&
        candidate.InFlightProvider == null &&
        candidate.LifecycleReleaseProvider == null &&
        candidate.LastSetupPass != setupPass;

    private bool ProviderSupportsAssetType(
        IRuntimePreparedAssetProvider provider,
        string assetType)
    {
        BeginProviderLifecycleCallback(provider);
        try
        {
            return provider.Supports(assetType);
        }
        finally
        {
            EndProviderLifecycleCallback();
        }
    }

    private IRuntimePreparedAssetProvider? FindProviderByIdLocked(string providerId)
    {
        return providerId.Length > 0 &&
            m_Providers.TryGetValue(providerId, out ProviderRegistration? registration)
            ? registration.Provider
            : null;
    }

    private bool IsProviderActiveForFrameBoundaryRetryLocked(
        IRuntimePreparedAssetProvider provider) =>
        m_Providers.TryGetValue(
            provider.ProviderId,
            out ProviderRegistration? registration) &&
        ReferenceEquals(registration.Provider, provider) &&
        registration.AdmissionOpen &&
        registration.PendingOperation == ProviderLifecycleOperation.None;

    private IRuntimePreparedAssetProvider? ResolveEntryProviderLocked(ResourceEntry entry)
    {
        return entry.LifecycleReleaseProvider ??
            FindProviderByIdLocked(entry.ProviderId) ??
            entry.PreparingProvider ??
            entry.InFlightProvider;
    }

    private void InvalidateProviderEntriesLocked(
        IRuntimePreparedAssetProvider provider,
        string diagnostic)
    {
        ResourceEntry[] affected = m_Resources.Values
            .Where(entry =>
                string.Equals(entry.ProviderId, provider.ProviderId, StringComparison.Ordinal) ||
                ReferenceEquals(entry.PreparingProvider, provider) ||
                ReferenceEquals(entry.InFlightProvider, provider))
            .ToArray();
        foreach (ResourceEntry entry in affected)
        {
            entry.LifecycleReleaseProvider = provider;
            // Keep InFlightProvider until the direct callback exits. The drain
            // waits for that callback before provider Release or owner disposal.
            entry.PreparingProvider = null;
            entry.State = RuntimePreparedAssetState.Waiting;
            entry.CommittedSetupGeneration = 0;
            entry.SetupGeneration = checked(entry.SetupGeneration + 1);
            entry.LastNeededSequence = ++m_NextSequence;
            entry.Diagnostic = diagnostic;
        }

    }

    private void TransferProviderReleasesToLifecycleLocked()
    {
        foreach (ResourceEntry entry in m_Resources.Values)
        {
            entry.LifecycleReleaseProvider ??=
                FindProviderByIdLocked(entry.ProviderId) ??
                entry.PreparingProvider ??
                entry.InFlightProvider;
        }
    }

    private ProviderReleaseTarget[] GetProviderReleaseTargetsLocked(
        IRuntimePreparedAssetProvider provider)
    {
        var targets = new List<ProviderReleaseTarget>();
        foreach (ResourceEntry entry in m_Resources.Values)
        {
            if (ReferenceEquals(entry.LifecycleReleaseProvider, provider))
            {
                targets.Add(new ProviderReleaseTarget(entry.Key, entry, null));
            }
        }

        foreach (PendingResourceCleanup cleanup in m_PendingCleanups)
        {
            if (cleanup.ProviderReleasePending &&
                ReferenceEquals(cleanup.Provider, provider))
            {
                targets.Add(new ProviderReleaseTarget(cleanup.Key, null, cleanup));
            }
        }

        targets.Sort(static (left, right) => left.Key.CompareTo(right.Key));
        return targets.ToArray();
    }

    private bool HasPendingProviderCleanupLocked(IRuntimePreparedAssetProvider provider) =>
        m_Resources.Values.Any(entry =>
            ReferenceEquals(entry.LifecycleReleaseProvider, provider)) ||
        m_PendingCleanups.Any(cleanup =>
            cleanup.ProviderReleasePending && ReferenceEquals(cleanup.Provider, provider));

    private bool HasBlockingCleanupForKeyLocked(RuntimeAssetResidencyKey key) =>
        m_PendingCleanups.Any(cleanup => cleanup.Key == key && cleanup.BlocksKeyReuse);

    private void ReleaseTemporaryCookedHandle(
        RuntimeAssetResidencyKey key,
        CookedAssetHandle handle,
        long cpuCookedBytes,
        bool blocksKeyReuse,
        string operation)
    {
        if (!handle.IsValid)
        {
            return;
        }

        try
        {
            m_AssetDatabase.Release(handle);
        }
        catch (Exception releaseFailure)
        {
            try
            {
                lock (m_Gate)
                {
                    bool cpuCookedBytesAccounted =
                        !m_Resources.Values.Any(entry => entry.CpuHandle == handle) &&
                        !m_PendingCleanups.Any(cleanup =>
                            cleanup.CookedHandleReleasePending &&
                            cleanup.CookedHandle == handle &&
                            cleanup.CpuCookedBytesAccounted);
                    long retainedCpuCookedBytes = cpuCookedBytesAccounted
                        ? checked(m_CpuCookedBytes + Math.Max(0, cpuCookedBytes))
                        : m_CpuCookedBytes;
                    AddPendingCleanupLocked(
                        key,
                        handle,
                        cpuCookedBytes,
                        estimatedGpuBytes: 0,
                        provider: null,
                        cpuCookedBytesAccounted: cpuCookedBytesAccounted,
                        blocksKeyReuse: blocksKeyReuse);
                    m_CpuCookedBytes = retainedCpuCookedBytes;
                    m_PeakCpuCookedBytes = Math.Max(
                        m_PeakCpuCookedBytes,
                        m_CpuCookedBytes);
                }
            }
            catch (Exception journalFailure)
            {
                throw new AggregateException(
                    $"Cooked asset handle {handle.Index}:{handle.Generation} failed to release " +
                    $"and retain cleanup ownership during {operation}.",
                    releaseFailure,
                    journalFailure);
            }

            throw new InvalidOperationException(
                $"Cooked asset handle {handle.Index}:{handle.Generation} failed to release " +
                $"during {operation}; deterministic cleanup ownership was retained.",
                releaseFailure);
        }
    }

    private void AddPendingCleanupLocked(
        RuntimeAssetResidencyKey key,
        CookedAssetHandle handle,
        long cpuCookedBytes,
        long estimatedGpuBytes,
        IRuntimePreparedAssetProvider? provider,
        bool cpuCookedBytesAccounted = true,
        bool blocksKeyReuse = true)
    {
        bool duplicateProviderRelease = provider != null && m_PendingCleanups.Any(cleanup =>
            cleanup.ProviderReleasePending &&
            cleanup.Key == key &&
            ReferenceEquals(cleanup.Provider, provider));
        if (duplicateProviderRelease)
        {
            throw new InvalidOperationException(
                $"Runtime asset '{key}' already has the same pending deterministic cleanup ownership.");
        }

        if (provider == null && !handle.IsValid)
        {
            return;
        }

        m_PendingCleanups.Add(new PendingResourceCleanup(
            key,
            handle,
            cpuCookedBytes,
            estimatedGpuBytes,
            provider,
            cpuCookedBytesAccounted,
            blocksKeyReuse));
    }

    private void ReleaseProviderTargets(
        IRuntimePreparedAssetProvider provider,
        IReadOnlyList<ProviderReleaseTarget> targets,
        string operation,
        List<Exception> failures)
    {
        for (int index = 0; index < targets.Count; index++)
        {
            ProviderReleaseTarget target = targets[index];
            try
            {
                BeginProviderLifecycleCallback(provider);
                try
                {
                    provider.Release(target.Key);
                }
                finally
                {
                    EndProviderLifecycleCallback();
                }

                lock (m_Gate)
                {
                    CompleteProviderReleaseTargetLocked(target, provider);
                    RemoveCompletedCleanupsLocked();
                }
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException(
                    $"Prepared asset provider '{provider.ProviderId}' failed to release " +
                    $"'{target.Key}' during {operation} cleanup.",
                    ex));
            }
        }
    }

    private void ExecutePendingCleanups(
        IReadOnlyList<PendingResourceCleanup> cleanups,
        string operation,
        List<Exception> failures)
    {
        for (int index = 0; index < cleanups.Count; index++)
        {
            PendingResourceCleanup cleanup = cleanups[index];
            if (cleanup.ProviderReleasePending && cleanup.Provider != null)
            {
                try
                {
                    BeginProviderLifecycleCallback(cleanup.Provider);
                    try
                    {
                        cleanup.Provider.Release(cleanup.Key);
                    }
                    finally
                    {
                        EndProviderLifecycleCallback();
                    }

                    lock (m_Gate)
                    {
                        cleanup.ProviderReleasePending = false;
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(new InvalidOperationException(
                        $"Prepared asset provider '{cleanup.Provider.ProviderId}' failed to " +
                        $"release '{cleanup.Key}' during {operation} cleanup.",
                        ex));
                }
            }

            if (cleanup.CookedHandleReleasePending)
            {
                try
                {
                    m_AssetDatabase.Release(cleanup.CookedHandle);
                    lock (m_Gate)
                    {
                        CompleteCookedHandleReleaseLocked(cleanup);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(new InvalidOperationException(
                        $"Cooked asset handle {cleanup.CookedHandle.Index}:" +
                        $"{cleanup.CookedHandle.Generation} failed to release during " +
                        $"{operation} cleanup.",
                        ex));
                }
            }
        }
    }

    private void CompleteCookedHandleReleaseLocked(PendingResourceCleanup cleanup)
    {
        cleanup.CookedHandleReleasePending = false;
        if (!cleanup.CpuCookedBytesAccounted)
        {
            return;
        }

        PendingResourceCleanup? successor = m_PendingCleanups.FirstOrDefault(candidate =>
            !ReferenceEquals(candidate, cleanup) &&
            candidate.CookedHandleReleasePending &&
            candidate.CookedHandle == cleanup.CookedHandle);
        if (successor != null)
        {
            successor.CpuCookedBytesAccounted = true;
            successor.BlocksKeyReuse |= cleanup.BlocksKeyReuse;
        }
        else
        {
            m_CpuCookedBytes = Math.Max(0, m_CpuCookedBytes - cleanup.CpuCookedBytes);
        }

        cleanup.CpuCookedBytesAccounted = false;
    }

    private void CompleteProviderReleaseTargetLocked(
        ProviderReleaseTarget target,
        IRuntimePreparedAssetProvider provider)
    {
        if (target.Resource != null &&
            m_Resources.TryGetValue(target.Key, out ResourceEntry? current) &&
            ReferenceEquals(current, target.Resource) &&
            ReferenceEquals(current.LifecycleReleaseProvider, provider))
        {
            current.LifecycleReleaseProvider = null;
            if (string.Equals(
                    current.ProviderId,
                    provider.ProviderId,
                    StringComparison.Ordinal))
            {
                m_PreparedGpuBytes = Math.Max(
                    0,
                    m_PreparedGpuBytes - current.EstimatedGpuBytes);
                current.EstimatedGpuBytes = 0;
                current.ProviderId = string.Empty;
            }
        }

        if (target.Cleanup != null &&
            target.Cleanup.ProviderReleasePending &&
            ReferenceEquals(target.Cleanup.Provider, provider))
        {
            target.Cleanup.ProviderReleasePending = false;
        }
    }

    private void RemoveCompletedCleanupsLocked()
    {
        m_PendingCleanups.RemoveAll(cleanup => cleanup.IsComplete);
    }

    private void TryRefreshPreparedGpuBytes(
        string operation,
        List<Exception> failures)
    {
        try
        {
            RefreshPreparedGpuBytes();
        }
        catch (Exception ex)
        {
            failures.Add(new InvalidOperationException(
                $"Prepared asset metrics failed to refresh during {operation} cleanup.",
                ex));
        }
    }

    private static void ThrowCleanupFailures(
        string diagnostic,
        List<Exception> failures)
    {
        if (failures.Count != 0)
        {
            throw new AggregateException(diagnostic, failures);
        }
    }

    private void EnsureNoProviderCallbackOnCurrentThread(string operation)
    {
        int threadId = Environment.CurrentManagedThreadId;
        lock (m_Gate)
        {
            if (m_ProviderLifecycleCallbackThreadIds.Contains(threadId))
            {
                throw new InvalidOperationException(
                    $"A prepared-provider lifecycle callback cannot {operation} reentrantly.");
            }

            foreach (ProviderRegistration registration in m_Providers.Values)
            {
                if (registration.InFlightThreadIds.Contains(threadId))
                {
                    throw new InvalidOperationException(
                        $"Prepared asset provider '{registration.Provider.ProviderId}' cannot " +
                        $"{operation} reentrantly from a Prepare callback.");
                }
            }
        }
    }

    private void BeginProviderLifecycleCallback(IRuntimePreparedAssetProvider provider)
    {
        int threadId = Environment.CurrentManagedThreadId;
        lock (m_Gate)
        {
            if (!m_ProviderLifecycleCallbackThreadIds.Add(threadId))
            {
                throw new InvalidOperationException(
                    $"Prepared asset provider '{provider.ProviderId}' entered a nested " +
                    "lifecycle callback on one thread.");
            }
        }
    }

    private void EndProviderLifecycleCallback()
    {
        int threadId = Environment.CurrentManagedThreadId;
        lock (m_Gate)
        {
            if (!m_ProviderLifecycleCallbackThreadIds.Remove(threadId))
            {
                throw new InvalidOperationException(
                    "Prepared-provider lifecycle callback ownership was lost.");
            }
        }
    }

    private static void EnsureProviderLifecycleCanDrainLocked(
        ProviderRegistration registration,
        string operation)
    {
        int threadId = Environment.CurrentManagedThreadId;
        if (registration.InFlightThreadIds.Contains(threadId))
        {
            throw new InvalidOperationException(
                $"Prepared asset provider '{registration.Provider.ProviderId}' cannot {operation} " +
                "reentrantly from its own Prepare callback.");
        }
    }

    private void DrainProviderCallsLocked(ProviderRegistration registration)
    {
        while (registration.InFlightCallCount != 0)
        {
            Monitor.Wait(m_Gate);
        }
    }

    private static void BeginProviderCallLocked(
        ProviderRegistration registration,
        int threadId)
    {
        if (!IsProviderPrepareAdmissionOpenLocked(registration) ||
            !registration.InFlightThreadIds.Add(threadId))
        {
            throw new InvalidOperationException(
                $"Prepared asset provider '{registration.Provider.ProviderId}' rejected " +
                "a reentrant or closed Prepare admission.");
        }

        registration.InFlightCallCount = checked(registration.InFlightCallCount + 1);
    }

    private void EndProviderCallLocked(
        ProviderRegistration registration,
        int threadId)
    {
        if (registration.InFlightCallCount <= 0 ||
            !registration.InFlightThreadIds.Remove(threadId))
        {
            throw new InvalidOperationException(
                $"Prepared asset provider '{registration.Provider.ProviderId}' lost " +
                "its in-flight callback ownership.");
        }

        registration.InFlightCallCount--;
        Monitor.PulseAll(m_Gate);
    }

    private static bool IsProviderPrepareAdmissionOpenLocked(
        ProviderRegistration registration) =>
        registration.AdmissionOpen &&
        !registration.MetricsSampling &&
        registration.PendingOperation == ProviderLifecycleOperation.None;

    private RuntimeAssetResidencyMetrics BuildMetricsLocked(
        IReadOnlyList<RuntimePreparedAssetProviderMetrics> providerMetrics)
    {
        int pendingDisposals = providerMetrics.Sum(metrics => metrics.PendingDisposalCount);
        int preparedDescriptors = providerMetrics.Sum(metrics => metrics.DescriptorCount);

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

    private void RefreshPreparedGpuBytes()
    {
        RuntimePreparedAssetProviderMetrics[] providerMetrics = GetProviderMetricsSnapshot();
        lock (m_Gate)
        {
            UpdatePreparedGpuBytesLocked(providerMetrics);
        }
    }

    private void UpdatePreparedGpuBytesLocked(
        IReadOnlyList<RuntimePreparedAssetProviderMetrics> providerMetrics)
    {
        long preparedGpuBytes = 0;
        for (int index = 0; index < providerMetrics.Count; index++)
        {
            preparedGpuBytes = checked(
                preparedGpuBytes + Math.Max(0, providerMetrics[index].EstimatedGpuBytes));
        }

        m_PreparedGpuBytes = preparedGpuBytes;
        m_PeakPreparedGpuBytes = Math.Max(m_PeakPreparedGpuBytes, preparedGpuBytes);
        if (preparedGpuBytes > Budgets.MaxPreparedGpuBytes) m_BudgetPressureCount++;
    }

    private RuntimePreparedAssetProviderMetrics[] GetProviderMetricsSnapshot()
    {
        ProviderRegistration[] registrations;
        lock (m_Gate)
        {
            registrations = m_Providers.Values.ToArray();
            foreach (ProviderRegistration registration in registrations)
            {
                EnsureProviderLifecycleCanDrainLocked(registration, "sample metrics");
                registration.MetricsSampling = true;
            }

            foreach (ProviderRegistration registration in registrations)
            {
                DrainProviderCallsLocked(registration);
            }
        }

        try
        {
            var metrics = new RuntimePreparedAssetProviderMetrics[registrations.Length];
            for (int index = 0; index < registrations.Length; index++)
            {
                metrics[index] = GetProviderMetrics(registrations[index].Provider);
            }

            return metrics;
        }
        finally
        {
            lock (m_Gate)
            {
                foreach (ProviderRegistration registration in registrations)
                {
                    registration.MetricsSampling = false;
                }

                Monitor.PulseAll(m_Gate);
            }
        }
    }

    private RuntimePreparedAssetProviderMetrics GetProviderMetrics(
        IRuntimePreparedAssetProvider provider)
    {
        BeginProviderLifecycleCallback(provider);
        try
        {
            return provider.GetMetrics();
        }
        finally
        {
            EndProviderLifecycleCallback();
        }
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
        lock (m_Gate)
        {
            if (m_Disposed)
            {
                throw new ObjectDisposedException(nameof(RuntimeAssetResidencyService));
            }
        }
    }

    private enum ProviderLifecycleOperation
    {
        None,
        Invalidate,
        Unregister,
        Dispose
    }

    private sealed class ProviderRegistration
    {
        public ProviderRegistration(IRuntimePreparedAssetProvider provider)
        {
            Provider = provider;
        }

        public IRuntimePreparedAssetProvider Provider { get; }
        public HashSet<int> InFlightThreadIds { get; } = new();
        public int InFlightCallCount { get; set; }
        public bool AdmissionOpen { get; set; } = true;
        public bool MetricsSampling { get; set; }
        public ProviderLifecycleOperation PendingOperation { get; set; }
    }

    private readonly record struct ProviderSetupAdmission(
        ResourceEntry Entry,
        ProviderRegistration Registration,
        long SetupGeneration,
        long ResidencySequence,
        long OwnerPlanGeneration);

    private sealed class PendingResourceCleanup
    {
        public PendingResourceCleanup(
            RuntimeAssetResidencyKey key,
            CookedAssetHandle cookedHandle,
            long cpuCookedBytes,
            long estimatedGpuBytes,
            IRuntimePreparedAssetProvider? provider,
            bool cpuCookedBytesAccounted,
            bool blocksKeyReuse)
        {
            Key = key;
            CookedHandle = cookedHandle;
            CpuCookedBytes = Math.Max(0, cpuCookedBytes);
            EstimatedGpuBytes = Math.Max(0, estimatedGpuBytes);
            Provider = provider;
            ProviderReleasePending = provider != null;
            CookedHandleReleasePending = cookedHandle.IsValid;
            CpuCookedBytesAccounted = cookedHandle.IsValid && cpuCookedBytesAccounted;
            BlocksKeyReuse = blocksKeyReuse;
        }

        public RuntimeAssetResidencyKey Key { get; }
        public CookedAssetHandle CookedHandle { get; }
        public long CpuCookedBytes { get; }
        public long EstimatedGpuBytes { get; }
        public IRuntimePreparedAssetProvider? Provider { get; }
        public bool ProviderReleasePending { get; set; }
        public bool CookedHandleReleasePending { get; set; }
        public bool CpuCookedBytesAccounted { get; set; }
        public bool BlocksKeyReuse { get; set; }
        public bool IsComplete => !ProviderReleasePending && !CookedHandleReleasePending;
    }

    private sealed record ProviderReleaseTarget(
        RuntimeAssetResidencyKey Key,
        ResourceEntry? Resource,
        PendingResourceCleanup? Cleanup);

    private sealed record ProviderReleaseBatch(
        IRuntimePreparedAssetProvider Provider,
        ProviderReleaseTarget[] Targets);

    private sealed class OwnerEntry
    {
        public OwnerEntry(
            HashSet<RuntimeAssetResidencyKey> requiredKeys,
            bool pinned)
        {
            RequiredKeys = requiredKeys;
            Pinned = pinned;
        }

        public RuntimeAssetResidencyKey[] Keys { get; private set; } =
            Array.Empty<RuntimeAssetResidencyKey>();
        public HashSet<RuntimeAssetResidencyKey> RequiredKeys { get; }
        public bool Pinned { get; set; }
        public bool AcquisitionComplete { get; private set; }

        public void CompleteAcquisition(RuntimeAssetResidencyKey[] keys)
        {
            if (AcquisitionComplete)
            {
                throw new InvalidOperationException(
                    "Runtime asset residency owner acquisition is already complete.");
            }

            Keys = keys;
            AcquisitionComplete = true;
        }
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
        public RuntimeAssetResidencyKey[]? BoundRequiredKeys { get; set; }
        public int PinnedOwnerCount { get; set; }
        public RuntimePreparedAssetState State { get; set; } = RuntimePreparedAssetState.Waiting;
        public long EstimatedGpuBytes { get; set; }
        public long LastNeededSequence { get; set; }
        public long OwnerPlanGeneration { get; set; }
        public long LastSetupPass { get; set; }
        public long SetupGeneration { get; set; }
        public long CommittedSetupGeneration { get; set; }
        public IRuntimePreparedAssetProvider? PreparingProvider { get; set; }
        public IRuntimePreparedAssetProvider? InFlightProvider { get; set; }
        public IRuntimePreparedAssetProvider? LifecycleReleaseProvider { get; set; }
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
            Diagnostic,
            Owners.Order().ToArray());
    }
}
