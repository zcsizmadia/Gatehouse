using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gatehouse.Providers.Google.Wire;

/// <summary>A request to <c>generateContent</c> or <c>streamGenerateContent</c>.</summary>
internal sealed class GeminiRequest
{
    [JsonPropertyName("contents")]
    public required IReadOnlyList<GeminiContent> Contents { get; init; }

    /// <summary>
    /// The system prompt, as a separate object rather than a message role.
    /// </summary>
    /// <remarks>
    /// Same shape of problem as Anthropic: OpenAI carries the system prompt inside the
    /// messages array, Gemini expects it here. It also has no <c>role</c>.
    /// </remarks>
    [JsonPropertyName("systemInstruction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeminiContent? SystemInstruction { get; init; }

    [JsonPropertyName("generationConfig")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeminiGenerationConfig? GenerationConfig { get; init; }
}

/// <summary>One turn of conversation, or the system instruction.</summary>
internal sealed class GeminiContent
{
    /// <summary>
    /// <c>user</c> or <c>model</c> — never <c>assistant</c>. Omitted for the system instruction.
    /// </summary>
    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; init; }

    [JsonPropertyName("parts")]
    public required IReadOnlyList<GeminiPart> Parts { get; init; }
}

/// <summary>A fragment of content. Phase 1 handles text only.</summary>
internal sealed class GeminiPart
{
    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

/// <summary>Sampling configuration. Note the camelCase names.</summary>
internal sealed class GeminiGenerationConfig
{
    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Temperature { get; init; }

    [JsonPropertyName("topP")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? TopP { get; init; }

    [JsonPropertyName("maxOutputTokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxOutputTokens { get; init; }

    [JsonPropertyName("stopSequences")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? StopSequences { get; init; }
}

/// <summary>A response, or one chunk of a streamed response — the shape is the same.</summary>
internal sealed class GeminiResponse
{
    [JsonPropertyName("candidates")]
    public IReadOnlyList<GeminiCandidate>? Candidates { get; init; }

    [JsonPropertyName("usageMetadata")]
    public GeminiUsageMetadata? UsageMetadata { get; init; }

    [JsonPropertyName("modelVersion")]
    public string? ModelVersion { get; init; }

    [JsonPropertyName("responseId")]
    public string? ResponseId { get; init; }
}

/// <summary>One generated alternative.</summary>
internal sealed class GeminiCandidate
{
    [JsonPropertyName("content")]
    public GeminiContent? Content { get; init; }

    /// <summary>Upper-case: <c>STOP</c>, <c>MAX_TOKENS</c>, <c>SAFETY</c>, <c>RECITATION</c>.</summary>
    [JsonPropertyName("finishReason")]
    public string? FinishReason { get; init; }
}

/// <summary>
/// Gemini token accounting.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PromptTokenCount"/> is the whole prompt, with
/// <see cref="CachedContentTokenCount"/> a subset of it — the same relationship OpenAI uses,
/// and the opposite of Anthropic's additive one. The assumption is asserted rather than
/// trusted: <c>MeteringConsistency</c> flags a cached count that exceeds the prompt.
/// </para>
/// <para>
/// <see cref="ThoughtsTokenCount"/> matters for billing. Thinking tokens are charged as
/// output but are not included in <see cref="CandidatesTokenCount"/>, so a provider that
/// reports only the candidates count under-bills every request to a thinking model. They are
/// also why <see cref="TotalTokenCount"/> is not simply prompt plus candidates, and therefore
/// why Gatehouse derives its own total instead of forwarding this one.
/// </para>
/// </remarks>
internal sealed class GeminiUsageMetadata
{
    [JsonPropertyName("promptTokenCount")]
    public int? PromptTokenCount { get; init; }

    [JsonPropertyName("candidatesTokenCount")]
    public int? CandidatesTokenCount { get; init; }

    [JsonPropertyName("cachedContentTokenCount")]
    public int? CachedContentTokenCount { get; init; }

    [JsonPropertyName("thoughtsTokenCount")]
    public int? ThoughtsTokenCount { get; init; }

    [JsonPropertyName("totalTokenCount")]
    public int? TotalTokenCount { get; init; }
}

/// <summary>An error envelope returned by the Gemini API.</summary>
internal sealed class GeminiErrorEnvelope
{
    [JsonPropertyName("error")]
    public GeminiError? Error { get; init; }
}

/// <summary>The error detail.</summary>
internal sealed class GeminiError
{
    [JsonPropertyName("code")]
    public int? Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

/// <summary>Role values used by the Gemini API.</summary>
internal static class GeminiRoles
{
    public const string User = "user";

    /// <summary>Gemini's name for the assistant turn.</summary>
    public const string Model = "model";
}

/// <summary>Source-generated serialization for the Gemini wire contract.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GeminiRequest))]
[JsonSerializable(typeof(GeminiResponse))]
[JsonSerializable(typeof(GeminiErrorEnvelope))]
internal sealed partial class GeminiJsonContext : JsonSerializerContext
{
}
