using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Gatehouse.Providers;

/// <summary>
/// Looks up a configured provider by the name routes refer to it by.
/// </summary>
public interface IProviderRegistry
{
    /// <summary>Attempts to find a provider.</summary>
    /// <param name="name">The configuration key.</param>
    /// <param name="provider">The provider, when one is registered under that key.</param>
    bool TryGet(string name, [NotNullWhen(true)] out IChatProvider? provider);

    /// <summary>Every registered provider name.</summary>
    IReadOnlyCollection<string> Names { get; }
}

/// <summary>
/// The default <see cref="IProviderRegistry"/>, built once from the registered providers.
/// </summary>
/// <remarks>
/// Providers are singletons resolved at startup rather than per request. They hold pooled
/// HTTP connections and credentials, and rebuilding them per request would defeat connection
/// reuse — which for a gateway is not a micro-optimisation but the difference between
/// reusing a TLS session and renegotiating one on every completion.
/// </remarks>
public sealed class ProviderRegistry : IProviderRegistry
{
    private readonly FrozenDictionary<string, IChatProvider> _providers;

    /// <summary>Creates a registry over the supplied providers.</summary>
    /// <param name="providers">Every registered provider.</param>
    /// <exception cref="ArgumentException">Two providers share a name.</exception>
    public ProviderRegistry(IEnumerable<IChatProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        Dictionary<string, IChatProvider> byName = new(StringComparer.OrdinalIgnoreCase);

        foreach (IChatProvider provider in providers)
        {
            if (!byName.TryAdd(provider.Name, provider))
            {
                // Two providers under one name means traffic silently goes to whichever won
                // the race. Failing at startup is the only honest option.
                throw new ArgumentException(
                    $"More than one provider is registered under the name '{provider.Name}'.",
                    nameof(providers));
            }
        }

        _providers = byName.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> Names => _providers.Keys;

    /// <inheritdoc />
    public bool TryGet(string name, [NotNullWhen(true)] out IChatProvider? provider)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            provider = null;
            return false;
        }

        return _providers.TryGetValue(name, out provider);
    }
}
