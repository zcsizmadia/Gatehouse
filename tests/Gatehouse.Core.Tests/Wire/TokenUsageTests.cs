using System.Text.Json;
using Gatehouse.Wire;

namespace Gatehouse.Tests.Wire;

/// <summary>
/// Tests for token accounting.
/// </summary>
/// <remarks>
/// Metering is the feature finance teams check, and an undercounted token is an under-billed
/// chargeback line. These tests exist to pin down the two properties that make reconciliation
/// against a provider invoice possible: totals are derived rather than trusted from the wire,
/// and estimated usage is never indistinguishable from measured usage.
/// </remarks>
public class TokenUsageTests
{
    [Test]
    public async Task Derives_the_total_from_the_parts()
    {
        // Providers occasionally omit total_tokens. Deriving it means a missing field cannot
        // turn into a zero-cost request.
        TokenUsage usage = TokenUsage.FromProvider(promptTokens: 120, completionTokens: 35);

        await Assert.That(usage.TotalTokens).IsEqualTo(155);
    }

    [Test]
    public async Task Marks_provider_reported_usage_as_authoritative()
    {
        TokenUsage usage = TokenUsage.FromProvider(10, 5);

        await Assert.That(usage.IsProviderReported).IsTrue();
    }

    [Test]
    public async Task Does_not_claim_authority_by_default()
    {
        // The parameterless case is local estimation. It must not default to authoritative,
        // because a zero that claims to be measured cannot be reconciled later.
        var usage = new TokenUsage { PromptTokens = 10, CompletionTokens = 5 };

        await Assert.That(usage.IsProviderReported).IsFalse();
    }

    [Test]
    public async Task Tracks_cached_prompt_tokens_separately()
    {
        // Providers bill cached prefixes at a reduced rate. Folding them into PromptTokens
        // would silently overstate cost.
        TokenUsage usage = TokenUsage.FromProvider(1000, 50, cachedPromptTokens: 800);

        await Assert.That(usage.PromptTokens).IsEqualTo(1000);
        await Assert.That(usage.CachedPromptTokens).IsEqualTo(800);
        await Assert.That(usage.TotalTokens).IsEqualTo(1050);
    }

    [Test]
    public async Task Sums_usage_across_a_fallback_chain()
    {
        TokenUsage first = TokenUsage.FromProvider(100, 10, cachedPromptTokens: 20);
        TokenUsage second = TokenUsage.FromProvider(200, 20, cachedPromptTokens: 5);

        TokenUsage combined = first + second;

        await Assert.That(combined.PromptTokens).IsEqualTo(300);
        await Assert.That(combined.CompletionTokens).IsEqualTo(30);
        await Assert.That(combined.TotalTokens).IsEqualTo(330);
        await Assert.That(combined.CachedPromptTokens).IsEqualTo(25);
    }

    [Test]
    public async Task Loses_authority_when_either_operand_was_estimated()
    {
        // Half-measured usage is estimated usage. Reporting the sum as authoritative would be
        // the exact kind of quiet inaccuracy that destroys trust in a chargeback report.
        TokenUsage measured = TokenUsage.FromProvider(100, 10);
        var estimated = new TokenUsage { PromptTokens = 50, CompletionTokens = 5, TotalTokens = 55 };

        await Assert.That((measured + estimated).IsProviderReported).IsFalse();
        await Assert.That((estimated + measured).IsProviderReported).IsFalse();
    }

    [Test]
    public async Task Add_matches_the_operator()
    {
        TokenUsage a = TokenUsage.FromProvider(1, 2);
        TokenUsage b = TokenUsage.FromProvider(3, 4);

        await Assert.That(TokenUsage.Add(a, b)).IsEqualTo(a + b);
    }

    [Test]
    public async Task None_is_empty_and_authoritative()
    {
        await Assert.That(TokenUsage.None.TotalTokens).IsEqualTo(0);
        await Assert.That(TokenUsage.None.IsProviderReported).IsTrue();
    }

    [Test]
    public async Task Serialises_with_the_openai_field_names()
    {
        TokenUsage usage = TokenUsage.FromProvider(7, 3);

        string json = JsonSerializer.Serialize(usage, GatehouseJsonContext.Default.TokenUsage);

        await Assert.That(json).Contains("\"prompt_tokens\":7");
        await Assert.That(json).Contains("\"completion_tokens\":3");
        await Assert.That(json).Contains("\"total_tokens\":10");
    }

    [Test]
    public async Task Does_not_serialise_the_authority_flag()
    {
        // IsProviderReported is internal bookkeeping, not part of the OpenAI wire contract.
        // Emitting it would break clients that validate the response shape.
        string json = JsonSerializer.Serialize(
            TokenUsage.FromProvider(1, 1),
            GatehouseJsonContext.Default.TokenUsage);

        await Assert.That(json).DoesNotContain("ProviderReported");
        await Assert.That(json).DoesNotContain("is_provider_reported");
    }

    [Test]
    public async Task Round_tripping_through_json_loses_the_authority_flag()
    {
        // Pins down why AsProviderReported has to exist. IsProviderReported is deliberately
        // off the wire, so usage parsed from an upstream comes back claiming to be estimated.
        // A provider that forgets to re-stamp it silently downgrades real billing data.
        string json = JsonSerializer.Serialize(
            TokenUsage.FromProvider(11, 7),
            GatehouseJsonContext.Default.TokenUsage);

        TokenUsage? parsed = JsonSerializer.Deserialize(json, GatehouseJsonContext.Default.TokenUsage);

        await Assert.That(parsed!.PromptTokens).IsEqualTo(11);
        await Assert.That(parsed.IsProviderReported).IsFalse();
    }

    [Test]
    public async Task AsProviderReported_restores_the_authority_flag()
    {
        var parsed = new TokenUsage { PromptTokens = 11, CompletionTokens = 7, TotalTokens = 18 };

        TokenUsage stamped = parsed.AsProviderReported();

        await Assert.That(stamped.IsProviderReported).IsTrue();
        await Assert.That(stamped.PromptTokens).IsEqualTo(11);
        await Assert.That(stamped.CompletionTokens).IsEqualTo(7);
        await Assert.That(stamped.TotalTokens).IsEqualTo(18);
    }

    [Test]
    public async Task AsProviderReported_is_idempotent()
    {
        TokenUsage already = TokenUsage.FromProvider(1, 1);

        await Assert.That(already.AsProviderReported()).IsEqualTo(already);
    }

    [Test]
    public async Task Omits_cached_tokens_when_there_are_none()
    {
        string json = JsonSerializer.Serialize(
            TokenUsage.FromProvider(5, 5),
            GatehouseJsonContext.Default.TokenUsage);

        await Assert.That(json).DoesNotContain("cached_prompt_tokens");
    }
}
