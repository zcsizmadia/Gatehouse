using Gatehouse.Providers;
using Gatehouse.Routing;
using Gatehouse.Wire;

namespace Gatehouse.Resilience;

/// <summary>
/// Why one attempt in a fallback chain did not produce an answer.
/// </summary>
public enum AttemptOutcome
{
    /// <summary>The upstream answered.</summary>
    Succeeded = 0,

    /// <summary>Not attempted: the upstream's circuit was open.</summary>
    CircuitOpen = 1,

    /// <summary>Attempted and failed in a way that permits trying the next route.</summary>
    RetryableFailure = 2,

    /// <summary>
    /// Attempted and failed in a way that does not. The chain stops here.
    /// </summary>
    TerminalFailure = 3,

    /// <summary>
    /// Not attempted: the route names a provider that is not registered.
    /// </summary>
    /// <remarks>
    /// Startup validation rejects this configuration, so it indicates a reload that landed
    /// inconsistently or a gap in the validator. Distinct from
    /// <see cref="CircuitOpen"/> because the remedies are opposites: an open circuit will
    /// close on its own, and this will not.
    /// </remarks>
    ProviderMissing = 4,
}

/// <summary>One attempt against one route.</summary>
/// <param name="Route">The route attempted.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Failure">The failure, when there was one.</param>
public sealed record RouteAttempt(ModelRoute Route, AttemptOutcome Outcome, ProviderException? Failure = null);

/// <summary>A non-streamed completion, and how many routes it took to get it.</summary>
/// <param name="Response">The upstream response.</param>
/// <param name="Route">The route that answered.</param>
/// <param name="Attempts">Every attempt made, in order, ending with the one that succeeded.</param>
public sealed record BufferedDispatch(
    ChatCompletionResponse Response,
    ModelRoute Route,
    IReadOnlyList<RouteAttempt> Attempts);

/// <summary>
/// A streamed completion whose first chunk has already been pulled.
/// </summary>
/// <remarks>
/// <para>
/// The first chunk is pulled inside the dispatcher deliberately, and the enumerator is handed
/// over mid-enumeration rather than as a fresh <see cref="IAsyncEnumerable{T}"/>. That is
/// slightly awkward to consume and it is the whole point: it makes the boundary between
/// "fallback is still possible" and "fallback is no longer possible" a property of the type
/// rather than a comment someone has to remember.
/// </para>
/// <para>
/// Once the first chunk exists, the response status is committed and bytes are about to go to
/// the client. Failing over to another provider after that would mean either replaying tokens
/// the caller already received or splicing two different completions together into one that
/// neither model produced. Both are worse than an honest mid-stream error, so mid-stream
/// failures do not fall back.
/// </para>
/// </remarks>
public sealed class StreamedDispatch : IAsyncDisposable
{
    private readonly IAsyncEnumerator<ChatCompletionChunk> _chunks;

    internal StreamedDispatch(
        IAsyncEnumerator<ChatCompletionChunk> chunks,
        bool hasFirstChunk,
        ModelRoute route,
        IReadOnlyList<RouteAttempt> attempts)
    {
        _chunks = chunks;
        HasFirstChunk = hasFirstChunk;
        Route = route;
        Attempts = attempts;
    }

    /// <summary>The route that answered.</summary>
    public ModelRoute Route { get; }

    /// <summary>Every attempt made, in order, ending with the one that succeeded.</summary>
    public IReadOnlyList<RouteAttempt> Attempts { get; }

    /// <summary>
    /// Whether the stream produced a chunk at all. False for an upstream that returned
    /// success and then closed without sending one.
    /// </summary>
    public bool HasFirstChunk { get; }

    /// <summary>
    /// The chunk enumerator, positioned on the first chunk when
    /// <see cref="HasFirstChunk"/> is true.
    /// </summary>
    /// <remarks>
    /// Consume it with a do/while, or a <c>while</c> that writes
    /// <see cref="IAsyncEnumerator{T}.Current"/> before advancing. Calling
    /// <see cref="IAsyncEnumerator{T}.MoveNextAsync"/> first discards the first chunk of every
    /// completion, which reads as a provider bug and is invisible in any test that only
    /// checks the concatenated text is non-empty.
    /// </remarks>
    public IAsyncEnumerator<ChatCompletionChunk> Chunks => _chunks;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _chunks.DisposeAsync();
}
