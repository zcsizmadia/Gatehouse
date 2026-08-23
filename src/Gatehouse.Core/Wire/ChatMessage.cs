using System.Text.Json.Serialization;

namespace Gatehouse.Wire;

/// <summary>
/// A single message in an OpenAI-compatible chat completion request or response.
/// </summary>
/// <remarks>
/// The OpenAI wire format allows <c>content</c> to be either a string or an array of
/// content parts. Gatehouse keeps it as a plain string for the
/// Phase 0 spike and normalises multi-part content in Phase 1, where the provider
/// translation layer needs a richer model anyway.
/// </remarks>
public sealed class ChatMessage
{
    /// <summary>The role of the author: <c>system</c>, <c>user</c>, <c>assistant</c>, or <c>tool</c>.</summary>
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    /// <summary>The message text. Null for assistant messages that carry only tool calls.</summary>
    [JsonPropertyName("content")]
    public string? Content { get; init; }

    /// <summary>Optional author name, used by some providers for multi-agent transcripts.</summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }
}
