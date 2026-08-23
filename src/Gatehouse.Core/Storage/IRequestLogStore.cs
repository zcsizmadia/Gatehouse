namespace Gatehouse.Storage;

/// <summary>
/// Persists the record of each request that passes through the gateway.
/// </summary>
/// <remarks>
/// <para>
/// Implementations must not block the request path. Recording is done for billing and audit,
/// both of which tolerate a short delay; an inference request does not tolerate waiting on a
/// disk write, and a store that made every completion wait for <c>fsync</c> would be the
/// gateway's dominant latency cost.
/// </para>
/// <para>
/// The interface exists so that shops which need Postgres or an append-only log can supply
/// one, but SQLite remains the default and no external database is ever <em>required</em>.
/// </para>
/// </remarks>
public interface IRequestLogStore
{
    /// <summary>
    /// Records one completed request.
    /// </summary>
    /// <remarks>
    /// Called after the response has been sent, so the cancellation token here is the
    /// application shutdown token rather than the client's.
    /// </remarks>
    ValueTask RecordAsync(RequestRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads back the most recent records, newest first.
    /// </summary>
    /// <param name="limit">Maximum number of records to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Present in Phase 0 so that the storage path can be verified end to end by a test and
    /// by an operator, rather than being a write-only sink that is only discovered to be
    /// broken when someone first needs the data.
    /// </remarks>
    ValueTask<IReadOnlyList<RequestRecord>> GetRecentAsync(int limit, CancellationToken cancellationToken = default);
}
