using System.Diagnostics.CodeAnalysis;
using Gatehouse.Wire;

namespace Gatehouse.Metering;

/// <summary>
/// Checks that a provider's reported token counts are internally consistent.
/// </summary>
/// <remarks>
/// <para>
/// Every provider reports usage differently, and the differences are silent. OpenAI sends
/// absolute counts on the final chunk; Anthropic sends <em>cumulative</em> counts on every
/// <c>message_delta</c>, so summing them over-counts; Google does not document which of the
/// two it does. A provider implementation that guesses wrong produces a chargeback report
/// that is confidently incorrect, which is worse than one that is obviously broken — nobody
/// investigates a number that looks plausible.
/// </para>
/// <para>
/// This is the arithmetic backstop. It cannot tell us the right answer, but it can tell us
/// when our assumption about a provider's semantics has stopped holding: if prompt plus
/// completion no longer equals the reported total, something upstream changed. The result is
/// a logged discrepancy and a flag on the record, rather than a wrong invoice line.
/// </para>
/// <para>
/// It deliberately does not throw. A metering disagreement must not fail a request the caller
/// has already paid for; the completion is delivered and the discrepancy is recorded.
/// </para>
/// </remarks>
public static class MeteringConsistency
{
    /// <summary>
    /// Validates a usage record.
    /// </summary>
    /// <param name="usage">The usage as reported by the provider.</param>
    /// <param name="discrepancy">
    /// A human-readable description of the inconsistency, or <see langword="null"/> when the
    /// record is consistent.
    /// </param>
    /// <returns><see langword="true"/> when the record is internally consistent.</returns>
    public static bool TryValidate(TokenUsage usage, [NotNullWhen(false)] out string? discrepancy)
    {
        ArgumentNullException.ThrowIfNull(usage);

        if (usage.PromptTokens < 0 || usage.CompletionTokens < 0 || usage.TotalTokens < 0)
        {
            discrepancy =
                $"Negative token count reported (prompt {usage.PromptTokens}, "
                + $"completion {usage.CompletionTokens}, total {usage.TotalTokens}).";
            return false;
        }

        if (usage.CachedPromptTokens < 0 || usage.CacheCreationTokens < 0)
        {
            discrepancy =
                $"Negative cache token count reported (read {usage.CachedPromptTokens}, "
                + $"creation {usage.CacheCreationTokens}).";
            return false;
        }

        // Cache reads and cache writes are both subsets of the prompt in Gatehouse's
        // normalised model, so together they cannot exceed it.
        //
        // This is the check most likely to catch a real mistake. Anthropic reports its cache
        // figures as categories *separate* from input_tokens, so a provider that maps
        // input_tokens straight onto PromptTokens leaves the cache counts larger than the
        // prompt they are supposed to be part of. Without this assertion that mistake shows up
        // only as an unexplained shortfall against the provider invoice, months later.
        int cacheTotal = usage.CachedPromptTokens + usage.CacheCreationTokens;

        if (cacheTotal > usage.PromptTokens)
        {
            discrepancy =
                $"Cache tokens ({usage.CachedPromptTokens} read + {usage.CacheCreationTokens} "
                + $"created = {cacheTotal}) exceed prompt tokens ({usage.PromptTokens}). Both are "
                + "meant to be subsets of the prompt; a provider reporting additive cache "
                + "categories must sum them into the prompt count first.";
            return false;
        }

        // A zero total with non-zero parts means the provider omitted the total and we failed
        // to derive it. Checked separately from the sum below so the message is actionable.
        int expectedTotal = usage.PromptTokens + usage.CompletionTokens;

        if (usage.TotalTokens == 0 && expectedTotal > 0)
        {
            discrepancy =
                $"Total tokens reported as 0 but prompt and completion sum to {expectedTotal}; "
                + "the total was not derived.";
            return false;
        }

        if (usage.TotalTokens != 0 && usage.TotalTokens != expectedTotal)
        {
            discrepancy =
                $"Reported total ({usage.TotalTokens}) does not equal prompt plus completion "
                + $"({usage.PromptTokens} + {usage.CompletionTokens} = {expectedTotal}). "
                + "This usually means the provider changed its streamed-usage semantics — "
                + "for example from cumulative to per-chunk — and the provider implementation "
                + "is now accumulating them incorrectly.";
            return false;
        }

        discrepancy = null;
        return true;
    }

    /// <summary>
    /// Returns the usage unchanged when it is consistent, or downgrades it to estimated when
    /// it is not.
    /// </summary>
    /// <remarks>
    /// Downgrading rather than discarding is the point. The counts are still the best
    /// information available and are probably close to right, but they are no longer evidence
    /// we are willing to put in front of a finance team as measured truth. A chargeback export
    /// can then show them as estimated, which is an honest position; presenting them as
    /// authoritative would not be.
    /// </remarks>
    /// <param name="usage">The usage as reported.</param>
    /// <param name="onDiscrepancy">Invoked with a description when the record is inconsistent.</param>
    public static TokenUsage Vet(TokenUsage? usage, Action<string>? onDiscrepancy = null)
    {
        if (usage is null)
        {
            return TokenUsage.None with { IsProviderReported = false };
        }

        if (TryValidate(usage, out string? discrepancy))
        {
            return usage;
        }

        onDiscrepancy?.Invoke(discrepancy);
        return usage with { IsProviderReported = false };
    }
}
