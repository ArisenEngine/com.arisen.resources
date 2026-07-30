using ArisenKernel.Lifecycle;

namespace ArisenEngine.Resources.Serialization;

internal sealed class RuntimeSmokeScenarioRegistry :
    IRuntimeSmokeScenarioRegistry,
    IRuntimeSmokeScenarioProvider
{
    private readonly object m_Gate = new();
    private readonly Dictionary<string, IRuntimeSmokeScenarioProvider> m_Providers =
        new(StringComparer.Ordinal);

    public void Register(string modeName, IRuntimeSmokeScenarioProvider provider)
    {
        string canonicalMode = ValidateModeName(modeName);
        ArgumentNullException.ThrowIfNull(provider);
        lock (m_Gate)
        {
            if (!m_Providers.TryAdd(canonicalMode, provider))
            {
                throw new InvalidOperationException(
                    $"Runtime smoke mode '{canonicalMode}' is already registered.");
            }
        }
    }

    public bool Unregister(string modeName, IRuntimeSmokeScenarioProvider provider)
    {
        string canonicalMode = ValidateModeName(modeName);
        ArgumentNullException.ThrowIfNull(provider);
        lock (m_Gate)
        {
            if (!m_Providers.TryGetValue(canonicalMode, out IRuntimeSmokeScenarioProvider? current) ||
                !ReferenceEquals(current, provider))
            {
                return false;
            }

            return m_Providers.Remove(canonicalMode);
        }
    }

    public IReadOnlyList<string> GetRegisteredModes()
    {
        lock (m_Gate)
        {
            return m_Providers.Keys.Order(StringComparer.Ordinal).ToArray();
        }
    }

    public bool TryCreateScenario(
        RuntimeSmokeScenarioContext context,
        out IRuntimeSmokeScenario scenario,
        out string diagnostic)
    {
        IRuntimeSmokeScenarioProvider? provider;
        lock (m_Gate)
        {
            m_Providers.TryGetValue(context.ModeName, out provider);
        }

        if (provider != null)
        {
            return provider.TryCreateScenario(context, out scenario, out diagnostic);
        }

        string available = string.Join(", ", GetRegisteredModes());
        scenario = null!;
        diagnostic = available.Length == 0
            ? $"No package registered runtime smoke mode '{context.ModeName}'."
            : $"No package registered runtime smoke mode '{context.ModeName}'. " +
              $"Available modes: {available}.";
        return false;
    }

    private static string ValidateModeName(string modeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modeName);
        string canonical = modeName.Trim().ToLowerInvariant();
        if (canonical.Length > 64 || canonical.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character == '-')))
        {
            throw new ArgumentException(
                "Runtime smoke mode names may contain only ASCII letters, digits, and '-'.",
                nameof(modeName));
        }

        return canonical;
    }
}
