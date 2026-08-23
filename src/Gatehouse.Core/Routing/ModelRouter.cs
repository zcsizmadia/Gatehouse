using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using Gatehouse.Configuration;
using Microsoft.Extensions.Options;

namespace Gatehouse.Routing;

/// <summary>
/// The configuration-driven <see cref="IModelRouter"/>.
/// </summary>
/// <remarks>
/// Routes are resolved into a <see cref="FrozenDictionary{TKey,TValue}"/> once at
/// construction. Every inference request performs this lookup, so it is worth the one-time
/// cost: a frozen dictionary trades slower construction for faster reads, which is exactly
/// the shape of this workload.
/// </remarks>
public sealed class ModelRouter : IModelRouter
{
    private readonly FrozenDictionary<string, ModelRoute> _routes;

    /// <summary>Creates a router over the supplied configuration.</summary>
    /// <param name="options">The bound Gatehouse configuration.</param>
    public ModelRouter(IOptions<GatehouseOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _routes = options.Value.Models.ToFrozenDictionary(
            static entry => entry.Key,
            static entry => new ModelRoute
            {
                Alias = entry.Key,
                Provider = entry.Value.Provider,

                // An alias that names a real upstream model is the common case, so
                // omitting UpstreamModel means "same name upstream" rather than being an
                // error. Operators only spell it out when the two genuinely differ, which
                // for Azure OpenAI deployments they usually do.
                UpstreamModel = string.IsNullOrWhiteSpace(entry.Value.UpstreamModel)
                    ? entry.Key
                    : entry.Value.UpstreamModel,

                Fallbacks = [.. entry.Value.Fallbacks],
            },
            StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> Aliases => _routes.Keys;

    /// <inheritdoc />
    public bool TryResolve(string model, [NotNullWhen(true)] out ModelRoute? route)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            route = null;
            return false;
        }

        return _routes.TryGetValue(model, out route);
    }
}
