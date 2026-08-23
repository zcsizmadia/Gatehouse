using System.Text.Json.Serialization;

namespace Gatehouse.Wire;

/// <summary>
/// An OpenAI-compatible chat completion request, as received from a client.
/// </summary>
/// <remarks>
/// Gatehouse speaks OpenAI-compatible in and provider-native out. Clients therefore never
/// need a Gatehouse SDK: any existing OpenAI client library works by changing a base URL.
/// This type models the subset of the request surface that Phase 0 routes on. Fields it
/// does not yet interpret are preserved and forwarded verbatim by the provider layer, so
/// interpreting one later is not a breaking change for callers already sending it.
/// </remarks>
public sealed class ChatCompletionRequest
{
    /// <summary>
    /// The requested model. This is the routing key: Gatehouse maps it to a provider and
    /// an upstream model name through configuration, so callers name a capability rather
    /// than a deployment.
    /// </summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    /// <summary>The conversation so far, in order.</summary>
    [JsonPropertyName("messages")]
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>
    /// Whether to stream the response as server-sent events. Streaming is the default mode
    /// for interactive callers and is what the Phase 0 gate exercises.
    /// </summary>
    [JsonPropertyName("stream")]
    public bool Stream { get; init; }

    /// <summary>Sampling temperature. Forwarded to the provider unchanged when set.</summary>
    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Temperature { get; init; }

    /// <summary>Nucleus sampling cutoff. Forwarded to the provider unchanged when set.</summary>
    [JsonPropertyName("top_p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? TopP { get; init; }

    /// <summary>Upper bound on generated tokens.</summary>
    [JsonPropertyName("max_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxTokens { get; init; }

    /// <summary>Sequences that stop generation.</summary>
    [JsonPropertyName("stop")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Stop { get; init; }

    /// <summary>
    /// An opaque end-user identifier. Gatehouse records it on the audit trail but never
    /// treats it as an authorisation input: it is client-supplied and therefore untrusted.
    /// </summary>
    [JsonPropertyName("user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? User { get; init; }
}
