using Gatehouse.Wire;

namespace Gatehouse.Caching;

/// <summary>
/// Turns a cached completion back into the shape the caller asked for.
/// </summary>
/// <remarks>
/// The cache stores one canonical <see cref="ChatCompletionResponse"/> regardless of how the
/// request that produced it was delivered, so a streamed request hitting an entry created by a
/// buffered one — and the reverse — both work. This is what makes it correct to leave
/// <c>stream</c> out of the cache key.
/// </remarks>
public static class CachedResponseReplay
{
    /// <summary>
    /// Rebuilds the chunk sequence for a streamed reply.
    /// </summary>
    /// <param name="response">The cached response.</param>
    /// <param name="id">The completion id to stamp on the chunks.</param>
    /// <param name="created">The creation timestamp to stamp on the chunks.</param>
    /// <remarks>
    /// <para>
    /// The original chunk boundaries are deliberately not stored. Server-sent events make no
    /// promise about how a message is divided, every OpenAI-compatible client reassembles by
    /// concatenating deltas, and storing the boundaries would mean holding a list per entry to
    /// reproduce a pause the caller is glad to be rid of.
    /// </para>
    /// <para>
    /// A fresh id and timestamp are stamped rather than replayed. Two callers receiving the
    /// same completion id would break any client that uses it as an idempotency key or a log
    /// correlator, and a cached response claiming to have been created an hour ago is a
    /// response that looks like a clock bug.
    /// </para>
    /// <para>
    /// Emitted as two chunks rather than one: content first, then an empty delta carrying the
    /// finish reason and usage. That is the shape real providers use, and a client that only
    /// reads usage from a final chunk with an empty delta — which some do — would otherwise
    /// miss it.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ChatCompletionChunk> ToChunks(
        ChatCompletionResponse response,
        string id,
        long created)
    {
        ArgumentNullException.ThrowIfNull(response);

        List<ChatCompletionChunk> chunks = [];

        foreach (ChatChoice choice in response.Choices)
        {
            chunks.Add(new ChatCompletionChunk
            {
                Id = id,
                Created = created,
                Model = response.Model,
                Choices =
                [
                    new ChatChunkChoice
                    {
                        Index = choice.Index,
                        Delta = new ChatDelta
                        {
                            Role = choice.Message.Role,
                            Content = choice.Message.Content,
                        },
                    },
                ],
            });
        }

        ChatChoice? last = response.Choices.Count > 0 ? response.Choices[^1] : null;

        chunks.Add(new ChatCompletionChunk
        {
            Id = id,
            Created = created,
            Model = response.Model,
            Choices =
            [
                new ChatChunkChoice
                {
                    Index = last?.Index ?? 0,
                    Delta = ChatDelta.Empty,
                    FinishReason = last?.FinishReason ?? FinishReasons.Stop,
                },
            ],
            Usage = response.Usage,
        });

        return chunks;
    }

    /// <summary>
    /// Rebuilds a buffered reply, restamped for this caller.
    /// </summary>
    /// <param name="response">The cached response.</param>
    /// <param name="id">The completion id to return.</param>
    /// <param name="created">The creation timestamp to return.</param>
    public static ChatCompletionResponse ToResponse(
        ChatCompletionResponse response,
        string id,
        long created)
    {
        ArgumentNullException.ThrowIfNull(response);

        return new ChatCompletionResponse
        {
            Id = id,
            Created = created,
            Model = response.Model,
            Choices = response.Choices,
            Usage = response.Usage,
            GatehouseProvider = response.GatehouseProvider,
        };
    }
}
