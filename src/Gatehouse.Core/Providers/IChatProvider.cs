using Gatehouse.Routing;
using Gatehouse.Wire;

namespace Gatehouse.Providers;

/// <summary>
/// A provider that can serve chat completions for one or more model routes.
/// </summary>
/// <remarks>
/// <para>
/// This is the extension point Gatehouse guards most carefully. The provider count is
/// capped at seven by <see href="https://github.com/zcsizmadia/Gatehouse/blob/main/GOVERNANCE.md">
/// project governance</see>, because every provider is a standing maintenance liability
/// against an API that changes without notice, and provider breadth is the metric on which
/// comparable gateways have historically rotted.
/// </para>
/// <para>
/// Implementations must be thread-safe and are registered as singletons. They must not
/// buffer streamed responses: see <see cref="StreamAsync"/>.
/// </para>
/// </remarks>
public interface IChatProvider
{
    /// <summary>
    /// The stable identifier used in configuration to bind a route to this provider, for
    /// example <c>azure-openai</c>. Lower-case, hyphenated, and never localised.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Serves a non-streamed completion.
    /// </summary>
    /// <param name="request">The client request, already validated.</param>
    /// <param name="route">The resolved destination for <see cref="ChatCompletionRequest.Model"/>.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <exception cref="ProviderException">The upstream call failed.</exception>
    Task<ChatCompletionResponse> CompleteAsync(
        ChatCompletionRequest request,
        ModelRoute route,
        CancellationToken cancellationToken);

    /// <summary>
    /// Serves a streamed completion.
    /// </summary>
    /// <remarks>
    /// Implementations must yield each chunk as soon as the upstream produces it.
    /// Accumulating chunks — even a small fixed number, even to simplify usage accounting —
    /// converts a responsive stream into a stuttering one, and no test that asserts only on
    /// the concatenated text will catch it. Compute usage incrementally instead and attach
    /// it to the final chunk.
    /// </remarks>
    /// <param name="request">The client request, already validated.</param>
    /// <param name="route">The resolved destination for <see cref="ChatCompletionRequest.Model"/>.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <exception cref="ProviderException">The upstream call failed before the first chunk.</exception>
    IAsyncEnumerable<ChatCompletionChunk> StreamAsync(
        ChatCompletionRequest request,
        ModelRoute route,
        CancellationToken cancellationToken);
}
