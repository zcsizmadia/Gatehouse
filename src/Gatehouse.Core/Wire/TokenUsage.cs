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
    /// billed at a steep discount — a tenth of the base input rate on Anthropic.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Always a <em>subset</em> of <see cref="PromptTokens"/> in Gatehouse, which is a
    /// normalisation rather than a passthrough. Providers disagree: OpenAI reports cached
    /// tokens as a subset of its prompt count, whereas Anthropic reports
    /// <c>input_tokens</c> as only the tokens after the last cache breakpoint, making its
    /// cache figures <em>additive</em>:
    /// </para>
    /// <code>
    /// total_input_tokens = cache_read_input_tokens + cache_creation_input_tokens + input_tokens
    /// </code>
    /// <para>
    /// A provider that maps Anthropic's <c>input_tokens</c> straight onto
    /// <see cref="PromptTokens"/> therefore under-reports the prompt by the whole cached
    /// portion — which on a cache-heavy workload is most of it. Every provider is responsible
    /// for normalising to subset semantics before constructing this record, and
    /// <c>MeteringConsistency</c> asserts the invariant.
    /// </para>
    /// </remarks>
    [JsonPropertyName("cached_prompt_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CachedPromptTokens { get; init; }

    /// <summary>
    /// The subset of <see cref="PromptTokens"/> written into the provider's prompt cache by
    /// this request.
    /// </summary>
    /// <remarks>
    /// Tracked separately from <see cref="CachedPromptTokens"/> because it is billed at a
    /// <em>premium</em>, not a discount — 1.25x the base input rate for Anthropic's five-minute
    /// cache and 2x for its one-hour cache, against 0.1x for a read. Collapsing writes and
    /// reads into one "cached" number would price a cache-warming request as though it were a
    /// cache hit, understating it by more than an order of magnitude.
    /// </remarks>
    [JsonPropertyName("cache_creation_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CacheCreationTokens { get; init; }

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
    public static TokenUsage FromProvider(
        int promptTokens,
        int completionTokens,
        int cachedPromptTokens = 0,
        int cacheCreationTokens = 0) => new()
    {
        PromptTokens = promptTokens,
        CompletionTokens = completionTokens,
        TotalTokens = promptTokens + completionTokens,
        CachedPromptTokens = cachedPromptTokens,
        CacheCreationTokens = cacheCreationTokens,
        IsProviderReported = true,
    };

    /// <summary>
    /// Creates a usage record from a provider that reports its cache figures as categories
    /// separate from the prompt count, rather than as a subset of it.
    /// </summary>
    /// <remarks>
    /// Anthropic works this way: its <c>input_tokens</c> counts only the tokens after the last
    /// cache breakpoint, so the billable prompt is the sum of all three. This overload exists
    /// so a provider cannot accidentally use the subset-semantics factory above and
    /// under-report the prompt by the entire cached portion.
    /// </remarks>
    /// <param name="uncachedPromptTokens">Tokens not eligible for the cache.</param>
    /// <param name="completionTokens">Tokens generated.</param>
    /// <param name="cacheReadTokens">Tokens served from the cache, billed at a discount.</param>
    /// <param name="cacheCreationTokens">Tokens written to the cache, billed at a premium.</param>
    public static TokenUsage FromProviderWithAdditiveCache(
        int uncachedPromptTokens,
        int completionTokens,
        int cacheReadTokens,
        int cacheCreationTokens)
    {
        int promptTokens = uncachedPromptTokens + cacheReadTokens + cacheCreationTokens;

        return new TokenUsage
        {
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = promptTokens + completionTokens,
            CachedPromptTokens = cacheReadTokens,
            CacheCreationTokens = cacheCreationTokens,
            IsProviderReported = true,
        };
    }

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
        CacheCreationTokens = left.CacheCreationTokens + right.CacheCreationTokens,
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
