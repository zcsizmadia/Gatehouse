using Gatehouse.Caching;
using Gatehouse.Configuration;
using Gatehouse.Wire;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Gatehouse.Tests.Caching;

/// <summary>Tests for the bounded, expiring, in-process response cache.</summary>
public class ResponseCacheTests
{
    [Test]
    public async Task Serves_a_stored_response()
    {
        (ResponseCache cache, _) = Build();

        cache.Store("k", Response("hello"));

        await Assert.That(cache.TryGet("k", out CachedResponse? hit)).IsTrue();
        await Assert.That(hit!.Response.Choices[0].Message.Content).IsEqualTo("hello");
    }

    [Test]
    public async Task Misses_a_key_it_has_never_seen()
    {
        (ResponseCache cache, _) = Build();

        await Assert.That(cache.TryGet("absent", out CachedResponse? hit)).IsFalse();
        await Assert.That(hit).IsNull();
    }

    [Test]
    public async Task Stores_nothing_and_serves_nothing_when_disabled()
    {
        // Off is the default, so this is the behaviour most deployments get. It has to be a
        // genuine no-op rather than an empty cache that still allocates and locks.
        (ResponseCache cache, _) = Build(enabled: false);

        cache.Store("k", Response("hello"));

        await Assert.That(cache.TryGet("k", out _)).IsFalse();
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Stops_serving_an_entry_once_its_ttl_has_passed()
    {
        (ResponseCache cache, FakeTimeProvider time) = Build(ttlSeconds: 60);

        cache.Store("k", Response("hello"));
        time.Advance(TimeSpan.FromSeconds(59));

        await Assert.That(cache.TryGet("k", out _)).IsTrue();

        time.Advance(TimeSpan.FromSeconds(1));

        await Assert.That(cache.TryGet("k", out _)).IsFalse();
    }

    [Test]
    public async Task Drops_an_expired_entry_rather_than_holding_it()
    {
        // Expiry is lazy, so the read is what reclaims the memory. An expired entry that stayed
        // in the index would occupy one of the bounded slots forever.
        (ResponseCache cache, FakeTimeProvider time) = Build(ttlSeconds: 60);

        cache.Store("k", Response("hello"));
        time.Advance(TimeSpan.FromSeconds(61));
        cache.TryGet("k", out _);

        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Reading_an_entry_does_not_extend_its_life()
    {
        // A cache that refreshed the TTL on read would serve a popular answer indefinitely,
        // which is precisely the entry most likely to have gone stale.
        (ResponseCache cache, FakeTimeProvider time) = Build(ttlSeconds: 60);

        cache.Store("k", Response("hello"));

        time.Advance(TimeSpan.FromSeconds(40));
        cache.TryGet("k", out _);

        time.Advance(TimeSpan.FromSeconds(21));

        await Assert.That(cache.TryGet("k", out _)).IsFalse();
    }

    [Test]
    public async Task Evicts_the_least_recently_used_entry_when_full()
    {
        (ResponseCache cache, _) = Build(maxEntries: 3);

        cache.Store("a", Response("a"));
        cache.Store("b", Response("b"));
        cache.Store("c", Response("c"));

        // Touching 'a' makes 'b' the least recently used.
        cache.TryGet("a", out _);

        cache.Store("d", Response("d"));

        await Assert.That(cache.Count).IsEqualTo(3);
        await Assert.That(cache.TryGet("b", out _)).IsFalse();
        await Assert.That(cache.TryGet("a", out _)).IsTrue();
        await Assert.That(cache.TryGet("c", out _)).IsTrue();
        await Assert.That(cache.TryGet("d", out _)).IsTrue();
    }

    [Test]
    public async Task Reading_an_entry_promotes_it_out_of_eviction_range()
    {
        // Without the promotion on read this degenerates into a first-in-first-out queue, which
        // evicts the hottest entries and looks like a cache that simply does not work.
        (ResponseCache cache, _) = Build(maxEntries: 2);

        cache.Store("hot", Response("hot"));
        cache.Store("cold", Response("cold"));

        for (int i = 0; i < 5; i++)
        {
            cache.TryGet("hot", out _);
        }

        cache.Store("new", Response("new"));

        await Assert.That(cache.TryGet("hot", out _)).IsTrue();
        await Assert.That(cache.TryGet("cold", out _)).IsFalse();
    }

    [Test]
    public async Task Never_holds_more_than_the_configured_number_of_entries()
    {
        (ResponseCache cache, _) = Build(maxEntries: 10);

        for (int i = 0; i < 500; i++)
        {
            cache.Store($"k{i}", Response($"v{i}"));
        }

        // The bound is the memory guarantee. An unbounded cache in front of a gateway converts
        // a cost problem into an out-of-memory crash.
        await Assert.That(cache.Count).IsEqualTo(10);
    }

    [Test]
    public async Task Refuses_to_store_a_response_larger_than_the_limit()
    {
        (ResponseCache cache, _) = Build(maxResponseBytes: 1_000);

        cache.Store("big", Response(new string('x', 5_000)));

        // Skipped outright rather than stored and immediately evicted: one very long completion
        // would otherwise flush a cache full of useful short ones.
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(cache.TryGet("big", out _)).IsFalse();
    }

    [Test]
    public async Task Keeps_storing_small_responses_after_rejecting_a_large_one()
    {
        (ResponseCache cache, _) = Build(maxResponseBytes: 1_000);

        cache.Store("small", Response("ok"));
        cache.Store("big", Response(new string('x', 5_000)));

        await Assert.That(cache.TryGet("small", out _)).IsTrue();
        await Assert.That(cache.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Replaces_an_existing_entry_rather_than_duplicating_it()
    {
        // Two callers can miss the same key concurrently and both go upstream. Both answers are
        // valid; the newer one has the longer remaining life, and the index must not grow.
        (ResponseCache cache, _) = Build();

        cache.Store("k", Response("first"));
        cache.Store("k", Response("second"));

        cache.TryGet("k", out CachedResponse? hit);

        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(hit!.Response.Choices[0].Message.Content).IsEqualTo("second");
    }

    [Test]
    public async Task Replacing_an_entry_restarts_its_ttl()
    {
        (ResponseCache cache, FakeTimeProvider time) = Build(ttlSeconds: 60);

        cache.Store("k", Response("first"));
        time.Advance(TimeSpan.FromSeconds(50));
        cache.Store("k", Response("second"));
        time.Advance(TimeSpan.FromSeconds(50));

        // The second write was 50 seconds ago, not 100.
        await Assert.That(cache.TryGet("k", out _)).IsTrue();
    }

    [Test]
    public async Task Survives_concurrent_readers_and_writers()
    {
        // The cache is a singleton on the request path, so every operation races every other.
        // This does not prove thread safety, but it does catch the index and the recency list
        // falling out of step, which throws or corrupts Count when it happens.
        (ResponseCache cache, _) = Build(maxEntries: 50);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                string key = $"k{i % 100}";
                cache.Store(key, Response($"v{worker}"));
                cache.TryGet(key, out _);
            }
        })));

        await Assert.That(cache.Count).IsLessThanOrEqualTo(50);
    }

    private static (ResponseCache Cache, FakeTimeProvider Time) Build(
        bool enabled = true,
        int ttlSeconds = 3600,
        int maxEntries = 100,
        int maxResponseBytes = 256 * 1024)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero));

        var options = Options.Create(new GatehouseOptions
        {
            Cache = new CacheOptions
            {
                Enabled = enabled,
                TtlSeconds = ttlSeconds,
                MaxEntries = maxEntries,
                MaxResponseBytes = maxResponseBytes,
            },
        });

        return (new ResponseCache(options, time), time);
    }

    private static ChatCompletionResponse Response(string content) => new()
    {
        Id = "chatcmpl-test",
        Created = 0,
        Model = "gpt-4o-mini",
        Choices =
        [
            new ChatChoice
            {
                Index = 0,
                Message = new ChatMessage { Role = ChatRoles.Assistant, Content = content },
                FinishReason = FinishReasons.Stop,
            },
        ],
        Usage = TokenUsage.FromProvider(10, 5),
    };
}
