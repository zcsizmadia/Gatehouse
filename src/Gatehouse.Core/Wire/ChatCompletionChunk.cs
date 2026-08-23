using System.Text.Json.Serialization;

namespace Gatehouse.Wire;

/// <summary>
/// One server-sent event in a streamed chat completion.
/// </summary>
/// <remarks>
/// Streaming is the reason a proxy path has to be written carefully rather than generated.
/// A chunk must reach the client as soon as the provider emits it: buffering even a few
/// chunks to simplify the code turns a responsive stream into a stuttering one, and the
/// effect is invisible to every test that only asserts on the concatenated final text.
/// The Phase 0 gate exists specifically to prove this path end to end, and
/// <c>StreamingProxyTests</c> asserts on inter-chunk timing rather than on content alone.
/// </remarks>
public sealed class ChatCompletionChunk
{
    /// <summary>The completion identifier, stable across every chunk of one response.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Always <c>chat.completion.chunk</c>, for wire compatibility.</summary>
    [JsonPropertyName("object")]
    public string ObjectType { get; init; } = "chat.completion.chunk";

    /// <summary>Creation time as Unix seconds.</summary>
    [JsonPropertyName("created")]
    public required long Created { get; init; }

    /// <summary>The upstream model serving this stream.</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    /// <summary>The incremental choices carried by this chunk.</summary>
    [JsonPropertyName("choices")]
    public required IReadOnlyList<ChatChunkChoice> Choices { get; init; }

    /// <summary>
    /// Token counts, present only on the final chunk. Providers that omit streamed usage
    /// force local estimation, which is recorded as estimated rather than presented as
    /// billable truth.
    /// </summary>
    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TokenUsage? Usage { get; init; }
}

/// <summary>An incremental choice within a streamed chunk.</summary>
public sealed class ChatChunkChoice
{
    /// <summary>Zero-based position of this choice.</summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>The incremental content for this choice.</summary>
    [JsonPropertyName("delta")]
    public required ChatDelta Delta { get; init; }

    /// <summary>Set on the last chunk for this choice; null on every earlier chunk.</summary>
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }
}

/// <summary>The incremental payload of a streamed choice.</summary>
public sealed class ChatDelta
{
    /// <summary>Present only on the first chunk of a choice.</summary>
    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; init; }

    /// <summary>The text fragment produced since the previous chunk.</summary>
    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; init; }

    /// <summary>An empty delta, used by the terminal chunk that carries only a finish reason.</summary>
    public static ChatDelta Empty { get; } = new();
}
