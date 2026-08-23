using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gatehouse.Providers.Anthropic.Wire;

/// <summary>A request to the Anthropic Messages API.</summary>
internal sealed class AnthropicRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    /// <summary>
    /// Required by Anthropic, unlike OpenAI where it is optional.
    /// </summary>
    /// <remarks>
    /// A request without it is rejected outright, so the translator substitutes a configured
    /// default rather than forwarding a null and letting the caller see a confusing 400.
    /// </remarks>
    [JsonPropertyName("max_tokens")]
    public required int MaxTokens { get; init; }

    /// <summary>
    /// The system prompt, as a top-level field.
    /// </summary>
    /// <remarks>
    /// Not a message role. OpenAI carries the system prompt inside the messages array;
    /// Anthropic rejects <c>"role": "system"</c> there and expects it here instead.
    /// </remarks>
    [JsonPropertyName("system")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? System { get; init; }

    [JsonPropertyName("messages")]
    public required IReadOnlyList<AnthropicMessageParam> Messages { get; init; }

    [JsonPropertyName("stream")]
    public bool Stream { get; init; }

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Temperature { get; init; }

    [JsonPropertyName("top_p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? TopP { get; init; }

    [JsonPropertyName("stop_sequences")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? StopSequences { get; init; }
}

/// <summary>One message in an Anthropic request. Only <c>user</c> and <c>assistant</c> are valid.</summary>
internal sealed class AnthropicMessageParam
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }
}

/// <summary>A non-streamed Anthropic message response.</summary>
internal sealed class AnthropicMessage
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("content")]
    public IReadOnlyList<AnthropicContentBlock>? Content { get; init; }

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; init; }

    [JsonPropertyName("usage")]
    public AnthropicUsage? Usage { get; init; }
}

/// <summary>One content block. Phase 1 handles <c>text</c>; others are ignored.</summary>
internal sealed class AnthropicContentBlock
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

/// <summary>
/// Anthropic token usage.
/// </summary>
/// <remarks>
/// <see cref="InputTokens"/> counts only the tokens after the last cache breakpoint. The
/// billable prompt is the sum of all three input figures — see
/// <c>docs/providers/wire-formats.md</c>. Mapping <see cref="InputTokens"/> straight onto a
/// prompt count under-reports it by everything that was cached.
/// </remarks>
internal sealed class AnthropicUsage
{
    [JsonPropertyName("input_tokens")]
    public int? InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public int? OutputTokens { get; init; }

    [JsonPropertyName("cache_creation_input_tokens")]
    public int? CacheCreationInputTokens { get; init; }

    [JsonPropertyName("cache_read_input_tokens")]
    public int? CacheReadInputTokens { get; init; }
}

/// <summary>
/// One server-sent event from an Anthropic stream, as a union of every shape.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a single permissive type rather than a polymorphic hierarchy. Two reasons.
/// </para>
/// <para>
/// First, NativeAOT: source-generated serialization handles a flat type with optional members
/// without any of the type-discriminator machinery that polymorphic deserialization needs.
/// </para>
/// <para>
/// Second, and more importantly, Anthropic's documentation states that new event types may be
/// added and that clients must handle unknown ones gracefully. A closed hierarchy turns a
/// forward-compatible addition into a deserialization failure mid-stream — aborting a
/// generation the caller has already been billed for. A permissive shape just leaves the new
/// members null.
/// </para>
/// </remarks>
internal sealed class AnthropicStreamEvent
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("index")]
    public int? Index { get; init; }

    /// <summary>Present on <c>message_start</c>; carries the initial usage.</summary>
    [JsonPropertyName("message")]
    public AnthropicMessage? Message { get; init; }

    /// <summary>
    /// Present on <c>content_block_delta</c> (text) and <c>message_delta</c> (stop reason).
    /// </summary>
    [JsonPropertyName("delta")]
    public AnthropicEventDelta? Delta { get; init; }

    /// <summary>Present on <c>message_delta</c>, carrying <em>cumulative</em> counts.</summary>
    [JsonPropertyName("usage")]
    public AnthropicUsage? Usage { get; init; }

    /// <summary>Present on <c>error</c>.</summary>
    [JsonPropertyName("error")]
    public AnthropicError? Error { get; init; }
}

/// <summary>The delta payload, covering both text deltas and message-level changes.</summary>
internal sealed class AnthropicEventDelta
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; init; }
}

/// <summary>An in-band stream error.</summary>
internal sealed class AnthropicError
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>Event type names emitted by an Anthropic stream.</summary>
internal static class AnthropicEventTypes
{
    public const string MessageStart = "message_start";
    public const string ContentBlockDelta = "content_block_delta";
    public const string MessageDelta = "message_delta";
    public const string MessageStop = "message_stop";
    public const string Error = "error";

    /// <summary>Keep-alive. May appear anywhere and carries nothing.</summary>
    public const string Ping = "ping";
}

/// <summary>Delta type names within a <c>content_block_delta</c>.</summary>
internal static class AnthropicDeltaTypes
{
    public const string TextDelta = "text_delta";
}

/// <summary>Source-generated serialization for the Anthropic wire contract.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AnthropicRequest))]
[JsonSerializable(typeof(AnthropicMessage))]
[JsonSerializable(typeof(AnthropicStreamEvent))]
[JsonSerializable(typeof(AnthropicError))]
internal sealed partial class AnthropicJsonContext : JsonSerializerContext
{
}
