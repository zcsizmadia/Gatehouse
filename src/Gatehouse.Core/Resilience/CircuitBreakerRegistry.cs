using System.Collections.Concurrent;
using Gatehouse.Configuration;
using Gatehouse.Routing;
using Microsoft.Extensions.Options;

namespace Gatehouse.Resilience;

/// <summary>
/// Supplies the circuit breaker guarding a given upstream.
/// </summary>
public interface ICircuitBreakerRegistry
{
    /// <summary>Whether breakers are enabled at all.</summary>
    bool Enabled { get; }

    /// <summary>Gets the breaker for one route's upstream, creating it on first use.</summary>
    /// <param name="route">The route whose upstream is about to be called.</param>
    CircuitBreaker GetFor(ModelRoute route);

    /// <summary>Every breaker created so far, for diagnostics.</summary>
    IReadOnlyCollection<CircuitBreaker> Breakers { get; }
}

/// <summary>
/// The default <see cref="ICircuitBreakerRegistry"/>: one breaker per upstream resource.
/// </summary>
/// <remarks>
/// <para>
/// Breakers are keyed on <em>provider plus upstream model</em>, not on provider alone. The
/// failure domain is the upstream resource, and on Azure OpenAI in particular it is the
/// deployment: quota is assigned per deployment, so a saturated <c>gpt-4o</c> deployment
/// throttles while the <c>gpt-4o-mini</c> deployment beside it is idle. Keying on the
/// provider would let the first one take out the second, which is the opposite of what a
/// breaker is for — and it would do so while the obvious fallback target was healthy.
/// </para>
/// <para>
/// Created lazily rather than up front from configuration. Routes are configuration-driven
/// and a deployment may configure many more aliases than it uses; there is no reason to hold
/// state for an upstream nobody has called.
/// </para>
/// </remarks>
public sealed class CircuitBreakerRegistry : ICircuitBreakerRegistry
{
    private readonly ConcurrentDictionary<string, CircuitBreaker> _breakers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ResilienceOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the registry.</summary>
    /// <param name="options">The bound Gatehouse configuration.</param>
    /// <param name="timeProvider">The clock breakers measure with.</param>
    public CircuitBreakerRegistry(IOptions<GatehouseOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _options = options.Value.Resilience;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public bool Enabled => _options.CircuitBreakerEnabled;

    /// <inheritdoc />
    public IReadOnlyCollection<CircuitBreaker> Breakers => _breakers.Values.ToArray();

    /// <inheritdoc />
    public CircuitBreaker GetFor(ModelRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);

        string key = KeyFor(route);

        // GetOrAdd's factory can run more than once under contention, so the loser's breaker
        // is discarded. That is harmless here — a breaker is cheap and carries no unmanaged
        // state — and cheaper than locking every lookup on the request path.
        return _breakers.GetOrAdd(key, static (k, state) => new CircuitBreaker(k, state._options, state._timeProvider), (_options, _timeProvider));
    }

    /// <summary>The breaker key for a route.</summary>
    public static string KeyFor(ModelRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return $"{route.Provider}/{route.UpstreamModel}";
    }
}
