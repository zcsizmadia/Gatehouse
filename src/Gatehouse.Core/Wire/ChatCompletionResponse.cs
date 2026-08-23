using System.Text.Json.Serialization;

namespace Gatehouse.Wire;

/// <summary>
/// A non-streamed OpenAI-compatible chat completion response.
/// </summary>
public sealed class ChatCompletionResponse
{
    /// <summary>A unique identifier for this completion.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Always <c>chat.completion</c>, for wire compatibility.</summary>
    [JsonPropertyName("object")]
    public string ObjectType { get; init; } = "chat.completion";

    /// <summary>Creation time as Unix seconds.</summary>
    [JsonPropertyName("created")]
    public required long Created { get; init; }

    /// <summary>
    /// The model that actually served the request. This is the upstream model name, not
    /// the alias the caller asked for: when a fallback chain redirects a request, the
    /// caller is told which model answered rather than being quietly misled.
    /// </summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    /// <summary>The generated choices.</summary>
    [JsonPropertyName("choices")]
    public required IReadOnlyList<ChatChoice> Choices { get; init; }

    /// <summary>Token counts for this completion.</summary>
    [JsonPropertyName("usage")]
    public TokenUsage? Usage { get; init; }

    /// <summary>
    /// The Gatehouse provider that served the request, exposed as a non-standard field so
    /// that a caller debugging a routing surprise does not have to read the server logs.
    /// </summary>
    [JsonPropertyName("gatehouse_provider")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GatehouseProvider { get; init; }
}

/// <summary>One generated alternative within a completion.</summary>
public sealed class ChatChoice
{
    /// <summary>Zero-based position of this choice.</summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>The generated message.</summary>
    [JsonPropertyName("message")]
    public required ChatMessage Message { get; init; }

    /// <summary>Why generation ended: <c>stop</c>, <c>length</c>, or <c>content_filter</c>.</summary>
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }
}

/// <summary>The reasons a provider may give for ending generation.</summary>
public static class FinishReasons
{
    /// <summary>The model reached a natural stopping point or a stop sequence.</summary>
    public const string Stop = "stop";

    /// <summary>Generation hit the token limit.</summary>
    public const string Length = "length";

    /// <summary>The provider content filter intervened.</summary>
    public const string ContentFilter = "content_filter";
}
