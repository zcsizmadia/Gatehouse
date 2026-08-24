using BenchmarkDotNet.Attributes;
using Gatehouse.Caching;
using Gatehouse.Configuration;
using Gatehouse.Routing;
using Gatehouse.Wire;
using Microsoft.Extensions.Options;

namespace Gatehouse.Benchmarks;

/// <summary>
/// The cost the cache adds to a request, and what it saves.
/// </summary>
/// <remarks>
/// <para>
/// Enabling the cache puts a SHA-256 over the whole conversation on the path of every request,
/// including the ones that miss. That cost is paid by everybody and the saving is only
/// collected by the hits, so the ratio between the two is what decides whether caching is
/// worth switching on for a given workload — and it is the number this file exists to let a
/// reader measure on their own hardware rather than take on trust.
/// </para>
/// <para>
/// <see cref="LongPromptCacheKey"/> is the one to watch. Key computation is linear in the
/// prompt, so a retrieval-augmented workload sending tens of kilobytes of context pays
/// proportionally more for a lookup than a chat workload does.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class CachingBenchmarks
{
    private static readonly ModelRoute Route = new()
    {
        Alias = "fast",
        Provider = "openai",
        UpstreamModel = "gpt-4o-mini",
    };

    private ChatCompletionRequest _shortRequest = null!;
    private ChatCompletionRequest _longRequest = null!;
    private ResponseCache _cache = null!;
    private string _hotKey = string.Empty;

    /// <summary>Builds the requests and pre-warms one cache entry.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _shortRequest = Request("Summarise this in one sentence.");

        // Roughly 16 KB of context, which is an ordinary retrieval-augmented prompt.
        _longRequest = Request(new string('x', 16 * 1024));

        _cache = new ResponseCache(
            Options.Create(new GatehouseOptions
            {
                Cache = new CacheOptions { Enabled = true, MaxEntries = 1_000 },
            }),
            TimeProvider.System);

        _hotKey = CacheKey.Compute(_shortRequest, Route, "acme");
        _cache.Store(_hotKey, Response());
    }

    /// <summary>Hashing a short chat prompt into a cache key.</summary>
    [Benchmark(Baseline = true)]
    public string ShortPromptCacheKey() => CacheKey.Compute(_shortRequest, Route, "acme");

    /// <summary>Hashing a 16 KB retrieval-augmented prompt into a cache key.</summary>
    [Benchmark]
    public string LongPromptCacheKey() => CacheKey.Compute(_longRequest, Route, "acme");

    /// <summary>
    /// A cache hit: the lookup only, with the key already computed.
    /// </summary>
    /// <remarks>
    /// Separated from key computation on purpose. Together they are what a hit costs; apart,
    /// they say which half to attack if that ever needs to be cheaper.
    /// </remarks>
    [Benchmark]
    public bool CacheHitLookup() => _cache.TryGet(_hotKey, out _);

    /// <summary>A cache miss: the lookup that finds nothing.</summary>
    [Benchmark]
    public bool CacheMissLookup() => _cache.TryGet("0000000000000000000000000000000000000000000000000000000000000000", out _);

    /// <summary>Storing a response, including the eviction check.</summary>
    [Benchmark]
    public void Store() => _cache.Store(_hotKey, Response());

    private static ChatCompletionRequest Request(string content) => new()
    {
        Model = "fast",
        Messages =
        [
            new ChatMessage { Role = ChatRoles.System, Content = "You are a helpful assistant." },
            new ChatMessage { Role = ChatRoles.User, Content = content },
        ],
        Temperature = 0f,
    };

    private static ChatCompletionResponse Response() => new()
    {
        Id = "chatcmpl-bench",
        Created = 0,
        Model = "gpt-4o-mini",
        Choices =
        [
            new ChatChoice
            {
                Index = 0,
                Message = new ChatMessage { Role = ChatRoles.Assistant, Content = "A cached answer." },
                FinishReason = FinishReasons.Stop,
            },
        ],
        Usage = TokenUsage.FromProvider(1_000, 50),
    };
}
