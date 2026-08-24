using System.Diagnostics.CodeAnalysis;
using System.Net;
using Gatehouse.Configuration;
using Gatehouse.Diagnostics;
using Gatehouse.Providers;
using Gatehouse.Routing;
using Gatehouse.Wire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gatehouse.Resilience;

/// <summary>
/// Sends a request to the first route in its fallback chain that will take it.
/// </summary>
public interface IChatDispatcher
{
    /// <summary>Serves a non-streamed completion, falling back on retryable failure.</summary>
    /// <param name="request">The client request.</param>
    /// <param name="primary">The route the caller's model resolved to.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <exception cref="ProviderException">Every route in the chain failed or was unavailable.</exception>
    Task<BufferedDispatch> CompleteAsync(
        ChatCompletionRequest request,
        ModelRoute primary,
        CancellationToken cancellationToken);

    /// <summary>
    /// Serves a streamed completion, falling back on retryable failure up to the point where
    /// the first chunk arrives.
    /// </summary>
    /// <param name="request">The client request.</param>
    /// <param name="primary">The route the caller's model resolved to.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <exception cref="ProviderException">Every route in the chain failed or was unavailable.</exception>
    Task<StreamedDispatch> StreamAsync(
        ChatCompletionRequest request,
        ModelRoute primary,
        CancellationToken cancellationToken);
}

/// <summary>
/// The default <see cref="IChatDispatcher"/>: fallback chains guarded by circuit breakers.
/// </summary>
/// <remarks>
/// <para>
/// These two features are one component because separately they are each half a feature. A
/// fallback chain without breakers pays the full upstream timeout on every dead link, so a
/// three-link chain against a hard outage turns a 100-second failure into a 300-second one —
/// slower than having no fallback at all. Breakers without a fallback chain fail fast to
/// nowhere. Together they do what an operator actually wants: notice a provider is down, stop
/// asking it, and send the traffic somewhere that works.
/// </para>
/// <para>
/// What counts as retryable is decided at the throw site, in
/// <see cref="ProviderException.IsRetryable"/>, and this class does not second-guess it.
/// A malformed request will be malformed at the next provider too, and trying it there bills
/// a second account to produce the same rejection.
/// </para>
/// </remarks>
public sealed class ResilientChatDispatcher : IChatDispatcher
{
    private readonly IModelRouter _router;
    private readonly IProviderRegistry _providers;
    private readonly ICircuitBreakerRegistry _breakers;
    private readonly ResilienceOptions _options;
    private readonly ILogger<ResilientChatDispatcher> _logger;

    /// <summary>Creates the dispatcher.</summary>
    /// <param name="router">Resolves fallback aliases.</param>
    /// <param name="providers">Resolves provider names to implementations.</param>
    /// <param name="breakers">Supplies the breaker per upstream.</param>
    /// <param name="options">The bound Gatehouse configuration.</param>
    /// <param name="logger">The log.</param>
    public ResilientChatDispatcher(
        IModelRouter router,
        IProviderRegistry providers,
        ICircuitBreakerRegistry breakers,
        IOptions<GatehouseOptions> options,
        ILogger<ResilientChatDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(breakers);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _router = router;
        _providers = providers;
        _breakers = breakers;
        _options = options.Value.Resilience;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<BufferedDispatch> CompleteAsync(
        ChatCompletionRequest request,
        ModelRoute primary,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(primary);

        List<RouteAttempt> attempts = [];

        foreach (ModelRoute route in Chain(primary))
        {
            if (!TryBegin(route, attempts, out IChatProvider? provider, out CircuitBreaker? breaker))
            {
                continue;
            }

            try
            {
                ChatCompletionResponse response = await provider.CompleteAsync(request, route, cancellationToken)
                    .ConfigureAwait(false);

                breaker?.RecordSuccess();
                attempts.Add(new RouteAttempt(route, AttemptOutcome.Succeeded));
                RecordAttemptMetrics(attempts, primary);

                return new BufferedDispatch(response, route, attempts);
            }
            catch (ProviderException ex)
            {
                if (!Failed(route, breaker, ex, attempts, primary))
                {
                    throw;
                }
            }
            catch (OperationCanceledException)
            {
                // The caller hung up, or the call timed out under a token we do not own.
                // Either way it says nothing about upstream health, and the chain stops: a
                // caller who has gone away is not waiting for a second opinion.
                breaker?.RecordAbandoned();
                throw;
            }
        }

        throw Exhausted(primary, attempts);
    }

    /// <inheritdoc />
    public async Task<StreamedDispatch> StreamAsync(
        ChatCompletionRequest request,
        ModelRoute primary,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(primary);

        List<RouteAttempt> attempts = [];

        foreach (ModelRoute route in Chain(primary))
        {
            if (!TryBegin(route, attempts, out IChatProvider? provider, out CircuitBreaker? breaker))
            {
                continue;
            }

            IAsyncEnumerator<ChatCompletionChunk> chunks =
                provider.StreamAsync(request, route, cancellationToken).GetAsyncEnumerator(cancellationToken);

            bool handedOver = false;
            try
            {
                // The first MoveNextAsync is where an upstream rejection surfaces, and it is
                // the last moment at which failing over is honest — nothing has been written
                // to the client yet, so the status line is still ours to choose.
                bool hasFirst = await chunks.MoveNextAsync().ConfigureAwait(false);

                breaker?.RecordSuccess();
                attempts.Add(new RouteAttempt(route, AttemptOutcome.Succeeded));
                RecordAttemptMetrics(attempts, primary);

                handedOver = true;
                return new StreamedDispatch(chunks, hasFirst, route, attempts);
            }
            catch (ProviderException ex)
            {
                if (!Failed(route, breaker, ex, attempts, primary))
                {
                    throw;
                }
            }
            catch (OperationCanceledException)
            {
                breaker?.RecordAbandoned();
                throw;
            }
            finally
            {
                // Disposed here on every path that does not hand it to the caller. Missing
                // this leaks the upstream response stream — and therefore a pooled connection —
                // once per failed attempt, which is worst precisely during an outage.
                if (!handedOver)
                {
                    await chunks.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        throw Exhausted(primary, attempts);
    }

    private IReadOnlyList<ModelRoute> Chain(ModelRoute primary) =>
        _options.FallbacksEnabled
            ? FallbackChain.Resolve(_router, primary, _options.MaxAttempts)
            : [primary];

    /// <summary>
    /// Resolves the provider for a route and asks its breaker for permission.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when this route must be skipped, having recorded why.
    /// </returns>
    private bool TryBegin(
        ModelRoute route,
        List<RouteAttempt> attempts,
        [NotNullWhen(true)] out IChatProvider? provider,
        out CircuitBreaker? breaker)
    {
        breaker = null;

        if (!_providers.TryGet(route.Provider, out provider))
        {
            // Startup validation rejects a route naming an unconfigured provider, so this is
            // reachable only through a bad hot reload. Skipping keeps the other links usable.
            _logger.RouteProviderMissing(route.Alias, route.Provider);
            attempts.Add(new RouteAttempt(route, AttemptOutcome.ProviderMissing));
            return false;
        }

        if (!_breakers.Enabled)
        {
            return true;
        }

        breaker = _breakers.GetFor(route);

        if (breaker.TryAcquire())
        {
            return true;
        }

        _logger.CircuitOpenSkippingRoute(route.Alias, breaker.Key);

        GatehouseTelemetry.CircuitBreakerRejections.Add(
            1,
            new KeyValuePair<string, object?>(GatehouseTelemetry.Attributes.GatehouseProvider, route.Provider),
            new KeyValuePair<string, object?>(GatehouseTelemetry.Attributes.GatehouseUpstreamModel, route.UpstreamModel));

        attempts.Add(new RouteAttempt(route, AttemptOutcome.CircuitOpen));
        breaker = null;
        return false;
    }

    /// <summary>
    /// Records a failed attempt.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the chain may continue to the next route.
    /// </returns>
    private bool Failed(
        ModelRoute route,
        CircuitBreaker? breaker,
        ProviderException failure,
        List<RouteAttempt> attempts,
        ModelRoute primary)
    {
        if (failure.IsRetryable)
        {
            breaker?.RecordFailure();
            attempts.Add(new RouteAttempt(route, AttemptOutcome.RetryableFailure, failure));
            _logger.RouteFailedRetryably(route.Alias, route.Provider, failure.Message);
            return true;
        }

        // A rejection is still an answer. The upstream was reachable and responsive; it
        // simply refused this request, which tells us nothing bad about its health and must
        // not push it towards being taken out of rotation. It also resolves a half-open
        // probe, which is why this is a success rather than nothing at all.
        breaker?.RecordSuccess();
        attempts.Add(new RouteAttempt(route, AttemptOutcome.TerminalFailure, failure));
        RecordAttemptMetrics(attempts, primary);
        return false;
    }

    /// <summary>
    /// Emits the fallback counter once per request, after the outcome is known.
    /// </summary>
    /// <remarks>
    /// Counted per request rather than per attempt, and only when a fallback actually
    /// happened. An operator watching this wants to know how often the primary route is
    /// letting them down, and a counter that also ticks on every healthy request buries that
    /// in the noise.
    /// </remarks>
    private static void RecordAttemptMetrics(List<RouteAttempt> attempts, ModelRoute primary)
    {
        if (attempts.Count <= 1)
        {
            return;
        }

        RouteAttempt last = attempts[^1];

        GatehouseTelemetry.RouteFallbacks.Add(
            1,
            new KeyValuePair<string, object?>(GatehouseTelemetry.Attributes.GatehouseRouteAlias, primary.Alias),
            new KeyValuePair<string, object?>(GatehouseTelemetry.Attributes.GatehouseProvider, last.Route.Provider),
            new KeyValuePair<string, object?>(
                GatehouseTelemetry.Attributes.GatehouseFallbackDepth,
                attempts.Count - 1));
    }

    /// <summary>
    /// Builds the failure for a request that ran out of routes.
    /// </summary>
    /// <remarks>
    /// The last real upstream failure is preferred over a synthesized one: it is the message
    /// that tells whoever is reading the log what actually went wrong. A "no routes available"
    /// error is only correct when every link really was skipped without being called.
    /// </remarks>
    private static ProviderException Exhausted(ModelRoute primary, List<RouteAttempt> attempts)
    {
        for (int i = attempts.Count - 1; i >= 0; i--)
        {
            if (attempts[i].Failure is { } failure)
            {
                return attempts.Count > 1
                    ? new ProviderException(
                        failure.ProviderName,
                        $"All {attempts.Count} routes for '{primary.Alias}' failed. The last error was: {failure.Message}",
                        failure.StatusCode,
                        isRetryable: false,
                        failure)
                    : failure;
            }
        }

        return new ProviderException(
            primary.Provider,
            $"Every upstream for '{primary.Alias}' is currently unavailable and was not "
            + "called. This is a circuit breaker rejecting traffic to a provider that has "
            + "been failing, not a rejection of your request; retry shortly.",
            HttpStatusCode.ServiceUnavailable,
            isRetryable: false);
    }
}
