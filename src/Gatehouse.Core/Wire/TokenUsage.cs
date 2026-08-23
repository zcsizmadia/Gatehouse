using System.Text.Json.Serialization;

namespace Gatehouse.Wire;

/// <summary>
/// Token counts for one completion.
/// </summary>
/// <remarks>
/// <para>
/// Metering accuracy is a first-order concern rather than a reporting detail: an
/// undercounted token is an under-billed chargeback line, and reconciling against
/// provider invoices is the single most-cited defect in comparable gateways.
/// </para>
/// <para>
/// Two rules follow. First, Gatehouse always prefers the usage figures the provider
/// reports over anything it counts locally — see <see cref="IsProviderReported"/>.
/// Second, cached prompt tokens are tracked separately because providers bill them at a
/// different rate, and folding them into <see cref="PromptTokens"/> silently overstates
/// cost.
/// </para>
/// </remarks>
public sealed record TokenUsage
{
    /// <summary>Tokens consumed by the prompt, including any cached prefix.</summary>
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; init; }

    /// <summary>Tokens produced by the model.</summary>
    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; init; }

    /// <summary>The sum of prompt and completion tokens.</summary>
    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; init; }

    /// <summary>
    /// The subset of <see cref="PromptTokens"/> served from the provider's prompt cache,
    /// which most providers bill at a reduced rate.
    /// </summary>
    [JsonPropertyName("cached_prompt_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CachedPromptTokens { get; init; }

    /// <summary>
    /// Whether these counts came from the provider rather than from local estimation.
    /// Estimated usage must never be presented to finance as though it were billable
    /// truth, so the distinction travels with the data.
    /// </summary>
    [JsonIgnore]
    public bool IsProviderReported { get; init; }

    /// <summary>An empty, provider-reported usage record.</summary>
    public static TokenUsage None { get; } = new() { IsProviderReported = true };

    /// <summary>
    /// Creates a usage record from provider-reported counts, deriving the total when the
    /// provider omits it.
    /// </summary>
    public static TokenUsage FromProvider(int promptTokens, int completionTokens, int cachedPromptTokens = 0) => new()
    {
        PromptTokens = promptTokens,
        CompletionTokens = completionTokens,
        TotalTokens = promptTokens + completionTokens,
        CachedPromptTokens = cachedPromptTokens,
        IsProviderReported = true,
    };

    /// <summary>
    /// Adds two usage records. Used when a request fans out across a fallback chain and
    /// more than one provider was actually billed. The result is provider-reported only
    /// if both operands were.
    /// </summary>
    public static TokenUsage operator +(TokenUsage left, TokenUsage right) => new()
    {
        PromptTokens = left.PromptTokens + right.PromptTokens,
        CompletionTokens = left.CompletionTokens + right.CompletionTokens,
        TotalTokens = left.TotalTokens + right.TotalTokens,
        CachedPromptTokens = left.CachedPromptTokens + right.CachedPromptTokens,
        IsProviderReported = left.IsProviderReported && right.IsProviderReported,
    };

    /// <summary>Adds two usage records. Named alternative to <c>operator +</c>.</summary>
    public static TokenUsage Add(TokenUsage left, TokenUsage right) => left + right;

    /// <summary>
    /// Returns this usage marked as provider-reported.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Required because <see cref="IsProviderReported"/> is deliberately not on the wire — it
    /// is Gatehouse bookkeeping, and emitting it would break clients that validate the OpenAI
    /// response shape. The consequence is that usage deserialized from an upstream arrives
    /// with the flag at its default of <see langword="false"/>, i.e. claiming to be estimated
    /// when it is in fact the provider's own count.
    /// </para>
    /// <para>
    /// Every provider must therefore call this on usage it parsed from an upstream response.
    /// Forgetting to is not a crash: it silently downgrades authoritative billing data to
    /// estimated, and the only symptom is a chargeback report that declines to vouch for
    /// itself. <c>ChatCompletionsEndpointTests</c> covers it end to end for that reason.
    /// </para>
    /// </remarks>
    public TokenUsage AsProviderReported() =>
        IsProviderReported ? this : this with { IsProviderReported = true };
}
