using Gatehouse.Metering;
using Gatehouse.Wire;

namespace Gatehouse.Tests.Metering;

/// <summary>
/// Tests for the metering arithmetic backstop.
/// </summary>
/// <remarks>
/// This check exists because providers disagree about what their token fields mean and change
/// them without notice. It cannot tell us the right answer; it can only tell us when our
/// assumption stopped holding. These tests pin down that it does so for each way an assumption
/// can break.
/// </remarks>
public class MeteringConsistencyTests
{
    [Test]
    public async Task Accepts_a_consistent_record()
    {
        TokenUsage usage = TokenUsage.FromProvider(promptTokens: 100, completionTokens: 20);

        await Assert.That(MeteringConsistency.TryValidate(usage, out string? reason)).IsTrue();
        await Assert.That(reason).IsNull();
    }

    [Test]
    public async Task Accepts_cache_figures_that_fit_inside_the_prompt()
    {
        TokenUsage usage = TokenUsage.FromProviderWithAdditiveCache(
            uncachedPromptTokens: 100,
            completionTokens: 10,
            cacheReadTokens: 700,
            cacheCreationTokens: 200);

        await Assert.That(MeteringConsistency.TryValidate(usage, out _)).IsTrue();
        await Assert.That(usage.PromptTokens).IsEqualTo(1000);
    }

    [Test]
    public async Task Rejects_cache_figures_that_exceed_the_prompt()
    {
        // The check most likely to catch a real mistake: a provider mapping Anthropic's
        // input_tokens straight onto PromptTokens leaves the cache counts larger than the
        // prompt they are meant to be part of.
        var usage = new TokenUsage
        {
            PromptTokens = 100,
            CompletionTokens = 10,
            TotalTokens = 110,
            CachedPromptTokens = 800,
            IsProviderReported = true,
        };

        await Assert.That(MeteringConsistency.TryValidate(usage, out string? reason)).IsFalse();
        await Assert.That(reason!).Contains("exceed prompt tokens");
    }

    [Test]
    public async Task Rejects_cache_reads_and_writes_that_together_exceed_the_prompt()
    {
        var usage = new TokenUsage
        {
            PromptTokens = 100,
            CompletionTokens = 10,
            TotalTokens = 110,
            CachedPromptTokens = 60,
            CacheCreationTokens = 60,
            IsProviderReported = true,
        };

        await Assert.That(MeteringConsistency.TryValidate(usage, out _)).IsFalse();
    }

    [Test]
    public async Task Rejects_a_total_that_does_not_match_the_parts()
    {
        // The signature of a cumulative-versus-incremental mistake: summing streamed usage
        // reports leaves the total larger than the parts it was derived from.
        var usage = new TokenUsage
        {
            PromptTokens = 100,
            CompletionTokens = 20,
            TotalTokens = 500,
            IsProviderReported = true,
        };

        await Assert.That(MeteringConsistency.TryValidate(usage, out string? reason)).IsFalse();
        await Assert.That(reason!).Contains("does not equal prompt plus completion");
    }

    [Test]
    public async Task Rejects_a_zero_total_with_non_zero_parts()
    {
        var usage = new TokenUsage
        {
            PromptTokens = 100,
            CompletionTokens = 20,
            TotalTokens = 0,
            IsProviderReported = true,
        };

        await Assert.That(MeteringConsistency.TryValidate(usage, out string? reason)).IsFalse();
        await Assert.That(reason!).Contains("not derived");
    }

    [Test]
    public async Task Accepts_an_all_zero_record()
    {
        // A request that produced nothing is consistent, not broken.
        await Assert.That(MeteringConsistency.TryValidate(TokenUsage.None, out _)).IsTrue();
    }

    [Test]
    [Arguments(-1, 0, 0)]
    [Arguments(0, -1, 0)]
    [Arguments(0, 0, -1)]
    public async Task Rejects_negative_counts(int prompt, int completion, int total)
    {
        var usage = new TokenUsage
        {
            PromptTokens = prompt,
            CompletionTokens = completion,
            TotalTokens = total,
        };

        await Assert.That(MeteringConsistency.TryValidate(usage, out _)).IsFalse();
    }

    // ---------------------------------------------------------------- Vet

    [Test]
    public async Task Vet_returns_a_consistent_record_unchanged()
    {
        TokenUsage usage = TokenUsage.FromProvider(10, 2);

        await Assert.That(MeteringConsistency.Vet(usage)).IsEqualTo(usage);
    }

    [Test]
    public async Task Vet_downgrades_an_inconsistent_record_to_estimated()
    {
        // Downgrading rather than discarding: the counts are still the best information
        // available, they are just no longer evidence we would put in front of finance as
        // measured truth.
        var usage = new TokenUsage
        {
            PromptTokens = 100,
            CompletionTokens = 20,
            TotalTokens = 999,
            IsProviderReported = true,
        };

        TokenUsage vetted = MeteringConsistency.Vet(usage);

        await Assert.That(vetted.IsProviderReported).IsFalse();
        await Assert.That(vetted.PromptTokens).IsEqualTo(100);
    }

    [Test]
    public async Task Vet_reports_the_discrepancy_to_the_callback()
    {
        var usage = new TokenUsage { PromptTokens = 100, CompletionTokens = 20, TotalTokens = 999 };

        string? reported = null;
        MeteringConsistency.Vet(usage, r => reported = r);

        await Assert.That(reported).IsNotNull();
    }

    [Test]
    public async Task Vet_does_not_invoke_the_callback_for_a_consistent_record()
    {
        bool called = false;
        MeteringConsistency.Vet(TokenUsage.FromProvider(1, 1), _ => called = true);

        await Assert.That(called).IsFalse();
    }

    [Test]
    public async Task Vet_treats_missing_usage_as_estimated_rather_than_measured()
    {
        // A request with no usage at all must not claim to be measured; an authoritative zero
        // is worse than an admitted unknown because only the second can be reconciled.
        TokenUsage vetted = MeteringConsistency.Vet(null);

        await Assert.That(vetted.IsProviderReported).IsFalse();
        await Assert.That(vetted.TotalTokens).IsEqualTo(0);
    }

    [Test]
    public async Task Vet_never_throws_on_an_inconsistent_record()
    {
        // A metering disagreement must not fail a request the caller has already paid for.
        var usage = new TokenUsage { PromptTokens = -5, CompletionTokens = -5, TotalTokens = -10 };

        await Assert.That(() => MeteringConsistency.Vet(usage)).ThrowsNothing();
    }
}
