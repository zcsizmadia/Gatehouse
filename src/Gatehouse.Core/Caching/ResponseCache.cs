using Gatehouse.Configuration;
using Gatehouse.Diagnostics;
using Gatehouse.Wire;
using Microsoft.Extensions.Options;

namespace Gatehouse.Caching;

/// <summary>A cached completion, and what it cost when it was real.</summary>
/// <param name="Response">The response to replay.</param>
/// <param name="ApproximateBytes">Roughly how much memory the entry holds.</param>
public sealed record CachedResponse(ChatCompletionResponse Response, int ApproximateBytes);

/// <summary>
/// An exact-match cache of completions.
/// </summary>
public interface IResponseCache
{
    /// <summary>Whether caching is switched on.</summary>
    bool Enabled { get; }

    /// <summary>
    /// Whether an entry is only servable to the organisation that created it.
    /// </summary>
    /// <remarks>
    /// Exposed because the caller computes the key, and the key has to carry the scope. A
    /// cache that knew its own scoping rule but could not tell the key builder about it would
    /// leave the boundary enforced in two places that can disagree.
    /// </remarks>
    bool ScopeToOrganisation { get; }

    /// <summary>Looks up a key, and counts the hit or miss.</summary>
    /// <param name="key">The key from <see cref="CacheKey.Compute"/>.</param>
    /// <param name="cached">The cached completion, when there is a live one.</param>
    bool TryGet(string key, out CachedResponse? cached);

    /// <summary>Stores a completion, evicting if the cache is full.</summary>
    /// <param name="key">The key from <see cref="CacheKey.Compute"/>.</param>
    /// <param name="response">The response to store.</param>
    void Store(string key, ChatCompletionResponse response);

    /// <summary>How many entries are held.</summary>
    int Count { get; }
}

/// <summary>
/// The default <see cref="IResponseCache"/>: bounded, in-process, least-recently-used.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Exact match only.</strong> Semantic caching — serving a stored answer to a
/// question that merely resembles the stored one — is deliberately not here and is not in
/// the near-term plan. It is the feature most likely to return a confidently wrong answer
/// to a caller who has no way to tell, and shipping it without honest measurements of how
/// often that happens would be selling a hazard as a saving. It arrives in Phase 4 with
/// those measurements or it does not arrive.
/// </para>
/// <para>
/// <strong>In-process, and therefore per instance.</strong> Two gateways behind a load
/// balancer keep independent caches, so the hit rate falls roughly with instance count. A
/// shared cache means a required Redis, and project governance puts a required external
/// dependency behind an RFC — a working Gatehouse needs the binary and a file. The honest
/// trade is a smaller hit rate rather than a bigger deployment.
/// </para>
/// <para>
/// <strong>Bounded twice.</strong> By entry count, and by the size of any single response.
/// Worst-case memory is therefore about <c>MaxEntries × MaxResponseBytes</c>, which an
/// operator can reason about before it becomes an incident. An unbounded cache in front of
/// a gateway does not save money, it converts a cost problem into an out-of-memory crash.
/// </para>
/// <para>
/// Expiry is lazy: entries are checked on read and dropped when stale. A background sweep
/// would be more machinery for no benefit — an expired entry that nobody asks for costs
/// only the memory that the entry bound already accounts for.
/// </para>
/// <para>Instances are thread-safe.</para>
/// </remarks>
public sealed class ResponseCache : IResponseCache
{
    private readonly object _gate = new();

    // Dictionary for the lookup, linked list for the recency order. The alternative — a
    // ConcurrentDictionary with timestamps and a scan to find the oldest — turns every
    // insert at capacity into an O(n) walk, which is worst exactly when the cache is
    // working hardest. A lock around two O(1) operations is cheaper and predictable.
    private readonly Dictionary<string, LinkedListNode<Entry>> _index;
    private readonly LinkedList<Entry> _recency = new();

    private readonly TimeProvider _timeProvider;
    private readonly CacheOptions _options;
    private readonly long _ttlTicks;

    /// <summary>Creates the cache.</summary>
    /// <param name="options">The bound Gatehouse configuration.</param>
    /// <param name="timeProvider">The clock expiry is measured against.</param>
    public ResponseCache(IOptions<GatehouseOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _options = options.Value.Cache;
        _timeProvider = timeProvider;
        _index = new Dictionary<string, LinkedListNode<Entry>>(
            Math.Min(_options.MaxEntries, 1024),
            StringComparer.Ordinal);

        _ttlTicks = Math.Max(1, timeProvider.TimestampFrequency * _options.TtlSeconds);
    }

    /// <inheritdoc />
    public bool Enabled => _options.Enabled;

    /// <inheritdoc />
    public bool ScopeToOrganisation => _options.ScopeToOrganisation;

    /// <inheritdoc />
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _index.Count;
            }
        }
    }

    /// <inheritdoc />
    public bool TryGet(string key, out CachedResponse? cached)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        cached = null;

        if (!_options.Enabled)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_index.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                GatehouseTelemetry.CacheMisses.Add(1);
                return false;
            }

            if (_timeProvider.GetTimestamp() - node.Value.StoredStamp >= _ttlTicks)
            {
                // Dropped on read rather than served-then-refreshed. A stale answer is the
                // thing the TTL exists to prevent.
                _recency.Remove(node);
                _index.Remove(key);
                GatehouseTelemetry.CacheMisses.Add(1);
                return false;
            }

            // Move to the front: this is the "recently used" in least-recently-used, and
            // omitting it turns the whole thing into a first-in-first-out queue that evicts
            // the hottest entries.
            _recency.Remove(node);
            _recency.AddFirst(node);

            cached = new CachedResponse(node.Value.Response, node.Value.ApproximateBytes);
            GatehouseTelemetry.CacheHits.Add(1);
            GatehouseTelemetry.CacheTokensAvoided.Add(
                (node.Value.Response.Usage?.PromptTokens ?? 0)
                + (node.Value.Response.Usage?.CompletionTokens ?? 0));

            return true;
        }
    }

    /// <inheritdoc />
    public void Store(string key, ChatCompletionResponse response)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(response);

        if (!_options.Enabled)
        {
            return;
        }

        int bytes = EstimateBytes(response);

        if (bytes > _options.MaxResponseBytes)
        {
            // Skipped rather than stored-and-immediately-evicted. One oversized response
            // would otherwise flush a cache full of useful small ones.
            GatehouseTelemetry.CacheSkippedTooLarge.Add(1);
            return;
        }

        var entry = new Entry(key, response, bytes, _timeProvider.GetTimestamp());

        lock (_gate)
        {
            if (_index.TryGetValue(key, out LinkedListNode<Entry>? existing))
            {
                // A concurrent miss on the same key means two callers both went upstream.
                // Both answers are valid; the newer one has the longer remaining life.
                _recency.Remove(existing);
                _index.Remove(key);
            }

            LinkedListNode<Entry> node = _recency.AddFirst(entry);
            _index[key] = node;

            while (_index.Count > _options.MaxEntries)
            {
                LinkedListNode<Entry>? oldest = _recency.Last;
                if (oldest is null)
                {
                    break;
                }

                _recency.RemoveLast();

                // The entry carries its own key so eviction is O(1). Finding it by scanning
                // the index instead would make every insert at capacity walk the whole cache,
                // which is the cost this data structure exists to avoid.
                _index.Remove(oldest.Value.Key);

                GatehouseTelemetry.CacheEvictions.Add(1);
            }
        }
    }

    /// <summary>
    /// Estimates an entry's memory footprint.
    /// </summary>
    /// <remarks>
    /// Deliberately rough, and only ever used to compare against a configured ceiling, so
    /// precision would buy nothing. Counts the response text — which dominates — at two
    /// bytes per char, plus a flat allowance for the object graph.
    /// </remarks>
    private static int EstimateBytes(ChatCompletionResponse response)
    {
        int bytes = 256;

        foreach (ChatChoice choice in response.Choices)
        {
            bytes += ((choice.Message.Content?.Length ?? 0) * 2) + 128;
        }

        return bytes;
    }

    private readonly record struct Entry(
        string Key,
        ChatCompletionResponse Response,
        int ApproximateBytes,
        long StoredStamp);
}
