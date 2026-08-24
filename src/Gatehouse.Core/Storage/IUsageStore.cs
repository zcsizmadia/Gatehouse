using Gatehouse.Metering;

namespace Gatehouse.Storage;

/// <summary>
/// Aggregates recorded requests into usage figures.
/// </summary>
/// <remarks>
/// Separate from <see cref="IRequestLogStore"/> because the two have opposite shapes.
/// Recording is a high-frequency, latency-critical write that must never block a completion;
/// aggregation is an infrequent, latency-tolerant read that scans a month. A single interface
/// would force every alternative backend to implement both to do either.
/// </remarks>
public interface IUsageStore
{
    /// <summary>
    /// Aggregates usage per provider and upstream model over a window.
    /// </summary>
    /// <param name="window">The period to cover.</param>
    /// <param name="provider">Restrict to one provider, or null for all of them.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Aggregated in the database rather than by reading rows into memory. A month of traffic
    /// on a busy gateway is millions of rows, and a reconciliation that needs the whole log
    /// resident is a reconciliation that gets run once and then avoided.
    /// </remarks>
    ValueTask<IReadOnlyList<UsageSummary>> SummariseAsync(
        UsageWindow window,
        string? provider = null,
        CancellationToken cancellationToken = default);
}
