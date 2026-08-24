using System.Globalization;

namespace Gatehouse.Metering;

/// <summary>
/// The period a usage query covers.
/// </summary>
/// <param name="FromInclusive">Start of the period.</param>
/// <param name="ToExclusive">End of the period, exclusive.</param>
/// <remarks>
/// Half-open deliberately. A closed range makes consecutive monthly reports either
/// double-count the boundary instant or drop it, and whichever it does will not be noticed
/// until someone adds twelve reports up and gets a number that does not match the year.
/// </remarks>
public sealed record UsageWindow(DateTimeOffset FromInclusive, DateTimeOffset ToExclusive)
{
    /// <summary>The window covering one UTC calendar month.</summary>
    /// <param name="year">The year.</param>
    /// <param name="month">The month, 1-12.</param>
    public static UsageWindow Month(int year, int month)
    {
        var start = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
        return new UsageWindow(start, start.AddMonths(1));
    }

    /// <summary>Parses <c>YYYY-MM</c> or a pair of ISO-8601 instants.</summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="window">The parsed window.</param>
    public static bool TryParseMonth(string? text, out UsageWindow? window)
    {
        window = null;

        if (string.IsNullOrWhiteSpace(text)
            || !DateTime.TryParseExact(
                text,
                "yyyy-MM",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime parsed))
        {
            return false;
        }

        window = Month(parsed.Year, parsed.Month);
        return true;
    }

    /// <inheritdoc />
    public override string ToString() =>
        FormattableString.Invariant($"{FromInclusive:yyyy-MM-dd HH:mm}Z to {ToExclusive:yyyy-MM-dd HH:mm}Z");
}

/// <summary>
/// What Gatehouse recorded for one provider and upstream model over a window.
/// </summary>
/// <remarks>
/// <para>
/// The counts are split by <em>how much Gatehouse actually knows</em> rather than only by
/// outcome, because that split is the whole content of a reconciliation. A total that lumps
/// together requests whose tokens the provider reported, requests Gatehouse could not read at
/// all, and requests that failed after burning tokens is a number nobody can defend in front
/// of a finance team.
/// </para>
/// <para>
/// Token counts are <see cref="long"/> rather than <see cref="int"/>: a busy month clears two
/// billion tokens comfortably, and a silently overflowed billing total is the worst possible
/// bug in this file.
/// </para>
/// </remarks>
public sealed record UsageSummary
{
    /// <summary>The Gatehouse provider key.</summary>
    public required string Provider { get; init; }

    /// <summary>The upstream model, which for Azure OpenAI is the deployment name.</summary>
    public required string UpstreamModel { get; init; }

    /// <summary>Every request recorded against this provider and model.</summary>
    public long Requests { get; init; }

    /// <summary>
    /// Requests whose token counts came from the provider, and are therefore authoritative.
    /// </summary>
    public long ProviderReportedRequests { get; init; }

    /// <summary>
    /// Requests that reached the provider but produced no token counts at all.
    /// </summary>
    /// <remarks>
    /// Gatehouse does not estimate token counts locally, so this is a genuine unknown rather
    /// than a lower-confidence figure. Reporting it as zero tokens would be a lie that adds
    /// up: a month of these looks like free traffic.
    /// </remarks>
    public long RequestsWithoutUsage { get; init; }

    /// <summary>
    /// Requests forwarded verbatim through the passthrough route, which cannot be metered.
    /// </summary>
    public long UnmeteredRequests { get; init; }

    /// <summary>Requests that returned a 4xx or 5xx to the caller.</summary>
    /// <remarks>
    /// Tracked for reconciliation rather than for reliability reporting. A request that failed
    /// after the provider had already generated tokens was still billed, and providers do not
    /// report usage on an error response — so this count bounds a gap that cannot be measured
    /// directly.
    /// </remarks>
    public long FailedRequests { get; init; }

    /// <summary>Prompt tokens, including the cached and cache-write subsets.</summary>
    public long PromptTokens { get; init; }

    /// <summary>Completion tokens.</summary>
    public long CompletionTokens { get; init; }

    /// <summary>Prompt tokens served from the provider's cache.</summary>
    public long CachedPromptTokens { get; init; }

    /// <summary>Prompt tokens written into the provider's cache.</summary>
    public long CacheCreationTokens { get; init; }

    /// <summary>
    /// Prompt tokens billed at the ordinary input rate: the prompt total less both cache
    /// subsets.
    /// </summary>
    /// <remarks>
    /// Clamped at zero. The subsets are supposed to fit inside the total and
    /// <c>MeteringConsistency</c> checks that they do, but a provider changing its semantics
    /// mid-quarter should produce a conservative number here rather than a negative one that
    /// propagates into a report.
    /// </remarks>
    public long UncachedPromptTokens =>
        Math.Max(0, PromptTokens - CachedPromptTokens - CacheCreationTokens);

    /// <summary>
    /// Requests answered from Gatehouse's own response cache, and therefore never billed.
    /// </summary>
    /// <remarks>
    /// Excluded from every token figure on this type. They are counted here and priced in
    /// <see cref="TokensAvoided"/> instead.
    /// </remarks>
    public long CacheHits { get; init; }

    /// <summary>
    /// Tokens that a cache hit meant no provider ever billed for.
    /// </summary>
    /// <remarks>
    /// The saving. Kept out of <see cref="TotalTokens"/> deliberately: a reconciliation
    /// compares that against an invoice, and adding tokens nobody charged for would report
    /// Gatehouse recording more than the provider billed — inverting a cost win into an
    /// apparent overspend.
    /// </remarks>
    public long TokensAvoided { get; init; }

    /// <summary>Prompt plus completion tokens actually billed by the provider.</summary>
    public long TotalTokens => PromptTokens + CompletionTokens;

    /// <summary>
    /// The share of requests served from cache, or 0 when there were none.
    /// </summary>
    public double CacheHitRate => Requests == 0 ? 0 : (double)CacheHits / Requests;

    /// <summary>
    /// Requests whose token cost Gatehouse cannot account for: unreadable, or absent.
    /// </summary>
    public long UnaccountedRequests => UnmeteredRequests + RequestsWithoutUsage;

    /// <summary>
    /// Requests that a provider could have billed for: everything except cache hits.
    /// </summary>
    public long BillableRequests => Requests - CacheHits;

    /// <summary>
    /// The share of billable requests whose tokens came from the provider, or 1 when there
    /// were none.
    /// </summary>
    /// <remarks>
    /// The single number worth putting on a dashboard. Anything below 1 means a reconciliation
    /// against this line has a gap it can bound but not close.
    /// <para>
    /// Measured against <see cref="BillableRequests"/> rather than <see cref="Requests"/>,
    /// because a cache hit is not a measurement failure — it is a request that had nothing to
    /// measure. Dividing by the total would drag confidence down as the cache got <em>more</em>
    /// effective, which is precisely backwards.
    /// </para>
    /// </remarks>
    public double Confidence =>
        BillableRequests <= 0 ? 1 : (double)ProviderReportedRequests / BillableRequests;
}
