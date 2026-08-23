namespace Gatehouse.Storage;

/// <summary>
/// What Gatehouse records about one request after it completes.
/// </summary>
/// <remarks>
/// <para>
/// This is the seed of three Phase 2 features that all read from the same rows: the
/// immutable audit log, hierarchical budget enforcement, and FinOps chargeback export.
/// Defining the record now, before any of them exists, means early deployments accumulate
/// usable history rather than having to be told their first months of data are unusable.
/// </para>
/// <para>
/// Note what is deliberately absent: prompt and completion text. Recording message content
/// by default would make the request log a copy of every prompt the organisation has ever
/// sent, in a database that exists for billing. Content capture is opt-in and belongs on
/// the telemetry path, where a compliance owner is already making an explicit decision
/// about it.
/// </para>
/// </remarks>
public sealed record RequestRecord
{
    /// <summary>The completion identifier, matching the one returned to the caller.</summary>
    public required string Id { get; init; }

    /// <summary>When the request was received, in UTC.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The model alias the caller asked for.</summary>
    public required string RequestedModel { get; init; }

    /// <summary>The provider that served it, or null if routing failed.</summary>
    public string? Provider { get; init; }

    /// <summary>The upstream model that answered, or null if the call never happened.</summary>
    public string? UpstreamModel { get; init; }

    /// <summary>Whether the response was streamed.</summary>
    public required bool Streamed { get; init; }

    /// <summary>The HTTP status returned to the caller.</summary>
    public required int StatusCode { get; init; }

    /// <summary>Prompt tokens consumed.</summary>
    public int PromptTokens { get; init; }

    /// <summary>Completion tokens produced.</summary>
    public int CompletionTokens { get; init; }

    /// <summary>
    /// Whether the token counts came from the provider rather than local estimation.
    /// </summary>
    /// <remarks>
    /// Persisted rather than derived because it is the field that makes invoice
    /// reconciliation possible. A chargeback report that cannot distinguish measured usage
    /// from estimated usage will eventually be handed to a finance team as though every row
    /// were authoritative, and the discrepancy will surface as a trust problem rather than
    /// a data problem.
    /// </remarks>
    public required bool UsageIsProviderReported { get; init; }

    /// <summary>Wall-clock duration of the request.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Time to the first streamed chunk, for streamed requests. Null otherwise.
    /// </summary>
    public TimeSpan? TimeToFirstChunk { get; init; }

    /// <summary>The error class, when the request failed. Null on success.</summary>
    public string? ErrorType { get; init; }
}
