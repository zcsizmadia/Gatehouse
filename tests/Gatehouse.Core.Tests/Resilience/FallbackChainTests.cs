using Gatehouse.Routing;

namespace Gatehouse.Tests.Resilience;

/// <summary>Tests for expanding a route into its attempt order.</summary>
public class FallbackChainTests
{
    [Test]
    public async Task A_route_with_no_fallbacks_is_a_chain_of_one()
    {
        var router = new StubRouter();
        ModelRoute primary = router.Add("fast", "openai");

        IReadOnlyList<ModelRoute> chain = FallbackChain.Resolve(router, primary, maxAttempts: 4);

        await Assert.That(chain.Select(r => r.Alias)).IsEquivalentTo(new[] { "fast" });
    }

    [Test]
    public async Task Fallbacks_are_tried_in_the_order_they_were_configured()
    {
        var router = new StubRouter();
        router.Add("backup", "anthropic");
        router.Add("last-resort", "local");
        ModelRoute primary = router.Add("fast", "openai", "backup", "last-resort");

        IReadOnlyList<ModelRoute> chain = FallbackChain.Resolve(router, primary, maxAttempts: 4);

        // Order is the operator's stated preference, not a set. Asserting sequentially is the
        // point of the test.
        await Assert.That(chain.Select(r => r.Alias).ToList())
                    .IsEquivalentTo(new List<string> { "fast", "backup", "last-resort" });
    }

    [Test]
    public async Task The_chain_is_capped_by_the_attempt_budget()
    {
        var router = new StubRouter();
        router.Add("b", "p2");
        router.Add("c", "p3");
        router.Add("d", "p4");
        ModelRoute primary = router.Add("a", "p1", "b", "c", "d");

        IReadOnlyList<ModelRoute> chain = FallbackChain.Resolve(router, primary, maxAttempts: 2);

        await Assert.That(chain.Count).IsEqualTo(2);
        await Assert.That(chain[1].Alias).IsEqualTo("b");
    }

    [Test]
    public async Task An_attempt_budget_of_one_disables_fallback_entirely()
    {
        var router = new StubRouter();
        router.Add("backup", "anthropic");
        ModelRoute primary = router.Add("fast", "openai", "backup");

        IReadOnlyList<ModelRoute> chain = FallbackChain.Resolve(router, primary, maxAttempts: 1);

        await Assert.That(chain.Count).IsEqualTo(1);
    }

    [Test]
    public async Task An_unresolvable_fallback_is_skipped_rather_than_failing_the_request()
    {
        // Startup validation rejects this configuration, so reaching it means a reload landed
        // inconsistently. Dropping the bad link still serves the request from a good one.
        var router = new StubRouter();
        router.Add("good", "anthropic");
        ModelRoute primary = router.Add("fast", "openai", "typo", "good");

        IReadOnlyList<ModelRoute> chain = FallbackChain.Resolve(router, primary, maxAttempts: 4);

        await Assert.That(chain.Select(r => r.Alias).ToList())
                    .IsEquivalentTo(new List<string> { "fast", "good" });
    }

    [Test]
    public async Task A_self_reference_does_not_produce_a_second_attempt_at_the_same_upstream()
    {
        var router = new StubRouter();
        ModelRoute primary = router.Add("fast", "openai", "fast");

        IReadOnlyList<ModelRoute> chain = FallbackChain.Resolve(router, primary, maxAttempts: 4);

        await Assert.That(chain.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_repeated_fallback_consumes_only_one_attempt()
    {
        // Two links to the same upstream would spend two of the attempt budget asking the
        // same failing provider the same question.
        var router = new StubRouter();
        router.Add("backup", "anthropic");
        ModelRoute primary = router.Add("fast", "openai", "backup", "backup");

        IReadOnlyList<ModelRoute> chain = FallbackChain.Resolve(router, primary, maxAttempts: 4);

        await Assert.That(chain.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Fallbacks_are_not_followed_transitively()
    {
        // 'a' falls back to 'b', and 'b' falls back to 'c'. A request for 'a' tries a then b
        // and stops: the chain is what one entry declares, so a reviewer reading 'a' sees
        // every route a request for 'a' can reach.
        var router = new StubRouter();
        router.Add("c", "p3");
        router.Add("b", "p2", "c");
        ModelRoute primary = router.Add("a", "p1", "b");

        IReadOnlyList<ModelRoute> chain = FallbackChain.Resolve(router, primary, maxAttempts: 4);

        await Assert.That(chain.Select(r => r.Alias).ToList())
                    .IsEquivalentTo(new List<string> { "a", "b" });
    }
}
