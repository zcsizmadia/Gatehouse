using System.Globalization;
using System.Threading.Channels;
using Gatehouse.Configuration;
using Gatehouse.Metering;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gatehouse.Storage.Sqlite;

/// <summary>
/// The default request-log store, backed by SQLite.
/// </summary>
/// <remarks>
/// <para>
/// Writes are queued to a bounded channel and committed in batches by a background loop, so
/// that no inference request ever waits on a disk write. The channel is bounded rather than
/// unbounded on purpose: an unbounded queue in front of a slow disk does not prevent the
/// problem, it converts a latency problem into an out-of-memory crash that takes the gateway
/// down with it.
/// </para>
/// <para>
/// When the queue is full, writers wait. The alternative — dropping records — is cheaper at
/// runtime and wrong: these rows become billing and audit data, and a chargeback report with
/// silent holes in it is worse than a slow one. Recording happens after the response has been
/// flushed to the client, so the backpressure lands on connection teardown rather than on
/// time-to-first-token.
/// </para>
/// </remarks>
public sealed class SqliteRequestLogStore : BackgroundService, IRequestLogStore, IUsageStore
{
    // Roughly a minute of sustained traffic at a few hundred requests per second. Large
    // enough to absorb a disk stall, small enough to stay bounded in memory.
    private const int QueueCapacity = 10_000;

    // Batching amortises the WAL commit across many rows. Larger batches trade a longer
    // window of un-committed records for less I/O; 128 keeps that window well under a second
    // at any plausible request rate.
    private const int MaxBatchSize = 128;

    private readonly Channel<RequestRecord> _queue = Channel.CreateBounded<RequestRecord>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly string _connectionString;
    private readonly bool _autoMigrate;
    private readonly ILogger<SqliteRequestLogStore> _logger;

    // One long-lived write connection. SQLite serialises writers anyway, so a pool would buy
    // nothing, and holding it open keeps the WAL checkpointing behaviour predictable.
    private SqliteConnection? _connection;

    /// <summary>Creates the store.</summary>
    /// <param name="options">The bound Gatehouse configuration.</param>
    /// <param name="logger">The logger.</param>
    public SqliteRequestLogStore(
        IOptions<GatehouseOptions> options,
        ILogger<SqliteRequestLogStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _connectionString = options.Value.Store.ConnectionString;
        _autoMigrate = options.Value.Store.AutoMigrate;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask RecordAsync(RequestRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _queue.Writer.WriteAsync(record, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<RequestRecord>> GetRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await SqliteSchema.ApplyPragmasAsync(connection, cancellationToken);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, timestamp_utc, requested_model, provider, upstream_model, streamed,
                   status_code, prompt_tokens, completion_tokens, usage_is_provider_reported,
                   duration_ms, time_to_first_chunk_ms, error_type,
                   virtual_key_id, organisation, team, application,
                   prompt_tokens_cached, prompt_tokens_cache_write, metered,
                   served_from_cache
            FROM request_log
            ORDER BY timestamp_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        List<RequestRecord> records = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new RequestRecord
            {
                Id = reader.GetString(0),
                Timestamp = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                RequestedModel = reader.GetString(2),
                Provider = reader.IsDBNull(3) ? null : reader.GetString(3),
                UpstreamModel = reader.IsDBNull(4) ? null : reader.GetString(4),
                Streamed = reader.GetInt64(5) != 0,
                StatusCode = (int)reader.GetInt64(6),
                PromptTokens = (int)reader.GetInt64(7),
                CompletionTokens = (int)reader.GetInt64(8),
                UsageIsProviderReported = reader.GetInt64(9) != 0,
                Duration = TimeSpan.FromMilliseconds(reader.GetDouble(10)),
                TimeToFirstChunk = reader.IsDBNull(11) ? null : TimeSpan.FromMilliseconds(reader.GetDouble(11)),
                ErrorType = reader.IsDBNull(12) ? null : reader.GetString(12),
                VirtualKeyId = reader.IsDBNull(13) ? null : reader.GetString(13),
                Organisation = reader.IsDBNull(14) ? null : reader.GetString(14),
                Team = reader.IsDBNull(15) ? null : reader.GetString(15),
                Application = reader.IsDBNull(16) ? null : reader.GetString(16),
                CachedPromptTokens = (int)reader.GetInt64(17),
                CacheCreationTokens = (int)reader.GetInt64(18),
                Metered = reader.GetInt64(19) != 0,
                ServedFromCache = reader.GetInt64(20) != 0,
            });
        }

        return records;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Aggregated with a single GROUP BY rather than by streaming rows into memory. The
    /// <c>ix_request_log_usage</c> index covers the provider, model and timestamp columns, so
    /// a month's reconciliation is an index range scan.
    /// </para>
    /// <para>
    /// Rows with no provider are excluded: those are requests rejected before any upstream was
    /// chosen — an unknown model, a failed credential — and no provider ever billed for them.
    /// Including them would put rejected traffic into a usage report as zero-token lines,
    /// diluting every average computed from it.
    /// </para>
    /// </remarks>
    public async ValueTask<IReadOnlyList<UsageSummary>> SummariseAsync(
        UsageWindow window,
        string? provider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);

        await using SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await SqliteSchema.ApplyPragmasAsync(connection, cancellationToken);

        await using SqliteCommand command = connection.CreateCommand();

        // Half-open on timestamp so that consecutive windows neither overlap nor leave a gap.
        //
        // A note on what "provider-reported" means here: a row qualifies only if the provider
        // actually reported counts AND those counts are non-zero. A reported zero is what an
        // upstream sends on a request it rejected, and treating it as an authoritative
        // measurement would let a month of rejections look like a month of free traffic.
        command.CommandText =
            """
            SELECT provider,
                   COALESCE(upstream_model, '(unspecified)')          AS model,
                   COUNT(*)                                           AS requests,
                   SUM(CASE WHEN served_from_cache = 0
                             AND metered = 1
                             AND usage_is_provider_reported = 1
                             AND (prompt_tokens + completion_tokens) > 0
                            THEN 1 ELSE 0 END)                        AS reported_requests,
                   SUM(CASE WHEN served_from_cache = 0
                             AND metered = 1
                             AND (usage_is_provider_reported = 0
                                  OR (prompt_tokens + completion_tokens) = 0)
                            THEN 1 ELSE 0 END)                        AS requests_without_usage,
                   SUM(CASE WHEN served_from_cache = 0 AND metered = 0
                            THEN 1 ELSE 0 END)                        AS unmetered_requests,
                   SUM(CASE WHEN status_code >= 400 THEN 1 ELSE 0 END) AS failed_requests,

                   -- Every token sum below excludes cache hits. Those tokens are real but no
                   -- provider billed for them, and including them would make a reconciliation
                   -- report Gatehouse recording more than the invoice charged.
                   SUM(CASE WHEN served_from_cache = 0
                            THEN prompt_tokens ELSE 0 END)            AS prompt_tokens,
                   SUM(CASE WHEN served_from_cache = 0
                            THEN completion_tokens ELSE 0 END)        AS completion_tokens,
                   SUM(CASE WHEN served_from_cache = 0
                            THEN prompt_tokens_cached ELSE 0 END)     AS prompt_tokens_cached,
                   SUM(CASE WHEN served_from_cache = 0
                            THEN prompt_tokens_cache_write ELSE 0 END) AS prompt_tokens_cache_write,

                   -- ...and are reported here instead, as the saving.
                   SUM(CASE WHEN served_from_cache = 1 THEN 1 ELSE 0 END) AS cache_hits,
                   SUM(CASE WHEN served_from_cache = 1
                            THEN prompt_tokens + completion_tokens
                            ELSE 0 END)                               AS tokens_avoided
              FROM request_log
             WHERE timestamp_utc >= $from
               AND timestamp_utc <  $to
               AND provider IS NOT NULL
               AND ($provider IS NULL OR provider = $provider)
             GROUP BY provider, model
             ORDER BY prompt_tokens + completion_tokens DESC;
            """;

        command.Parameters.AddWithValue("$from", Iso(window.FromInclusive));
        command.Parameters.AddWithValue("$to", Iso(window.ToExclusive));
        command.Parameters.AddWithValue("$provider", (object?)provider ?? DBNull.Value);

        List<UsageSummary> summaries = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            summaries.Add(new UsageSummary
            {
                Provider = reader.GetString(0),
                UpstreamModel = reader.GetString(1),
                Requests = reader.GetInt64(2),
                ProviderReportedRequests = reader.GetInt64(3),
                RequestsWithoutUsage = reader.GetInt64(4),
                UnmeteredRequests = reader.GetInt64(5),
                FailedRequests = reader.GetInt64(6),
                PromptTokens = reader.GetInt64(7),
                CompletionTokens = reader.GetInt64(8),
                CachedPromptTokens = reader.GetInt64(9),
                CacheCreationTokens = reader.GetInt64(10),
                CacheHits = reader.GetInt64(11),
                TokensAvoided = reader.GetInt64(12),
            });
        }

        return summaries;
    }

    /// <summary>
    /// Formats an instant the way the timestamp column stores it.
    /// </summary>
    /// <remarks>
    /// The column is ISO-8601 text, so range comparisons are string comparisons and both sides
    /// must be formatted identically. Round-trip format ("O") sorts lexicographically in the
    /// same order as chronologically, which is the property the whole scheme rests on.
    /// </remarks>
    private static string Iso(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// Opens the database and applies any pending migrations before the host reports started.
    /// </summary>
    /// <remarks>
    /// Deliberately here rather than in <see cref="ExecuteAsync"/>. Two reasons, and both are
    /// about failing at the right moment:
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// <c>ExecuteAsync</c> runs concurrently with the rest of startup, so migrating there
    /// would let the gateway begin serving requests against a database that has no tables
    /// yet — the records would be dropped and the operator would see nothing wrong.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <c>ExecuteAsync</c> receives the <em>stopping</em> token. Migrating with it means a
    /// shutdown arriving during startup cancels the migration half-applied.
    /// </description>
    /// </item>
    /// </list>
    /// An unwritable database now fails the rollout instead of silently costing an audit trail.
    /// </remarks>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _connection = new SqliteConnection(_connectionString);
        await _connection.OpenAsync(cancellationToken);
        await SqliteSchema.ApplyPragmasAsync(_connection, cancellationToken);

        if (_autoMigrate)
        {
            int version = await SqliteSchema.MigrateAsync(_connection, cancellationToken);
            _logger.SchemaReady(version);
        }

        await base.StartAsync(cancellationToken);
    }

    /// <summary>Drains queued records until shutdown.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SqliteConnection connection = _connection
            ?? throw new InvalidOperationException("StartAsync must run before ExecuteAsync.");

        List<RequestRecord> batch = new(MaxBatchSize);

        try
        {
            // WaitToReadAsync rather than ReadAsync: it returns false when the channel has
            // been completed and drained, instead of throwing ChannelClosedException. That
            // distinction matters because shutdown completes the writer and cancels the token
            // at almost the same moment, and an unhandled exception on that path would fault
            // the host rather than stopping it.
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                // Take everything already queued without waiting for more. Latency stays low
                // when traffic is light and batches form naturally when it is heavy, with no
                // timer to tune.
                while (batch.Count < MaxBatchSize && _queue.Reader.TryRead(out RequestRecord? next))
                {
                    batch.Add(next);
                }

                if (batch.Count > 0)
                {
                    await WriteBatchAsync(connection, batch, CancellationToken.None);
                    batch.Clear();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown. Anything still queued is written below.
        }
        catch (Exception ex)
        {
            // BackgroundService awaits ExecuteAsync with ConfigureAwaitOptions.SuppressThrowing,
            // so anything escaping here vanishes without trace: the request log silently stops
            // working and the only symptom is a gap in the data, discovered weeks later by
            // whoever needed it. Log it, then still attempt the drain below.
            _logger.WriterStoppedUnexpectedly(ex);
        }

        // Drain rather than abandon: a record accepted from the request path has already been
        // treated as recorded, and losing it would put a hole in the usage history.
        await DrainRemainingAsync(connection);
    }

    private async Task DrainRemainingAsync(SqliteConnection connection)
    {
        try
        {
            await DrainRemainingCoreAsync(connection);
        }
        catch (Exception ex)
        {
            // Same reasoning as ExecuteAsync: an exception on the shutdown path would be
            // suppressed by the host, turning lost records into a silent gap.
            _logger.WriterStoppedUnexpectedly(ex);
        }
    }

    private async Task DrainRemainingCoreAsync(SqliteConnection connection)
    {
        List<RequestRecord> remaining = [];
        while (_queue.Reader.TryRead(out RequestRecord? record))
        {
            remaining.Add(record);
            if (remaining.Count == MaxBatchSize)
            {
                await WriteBatchAsync(connection, remaining, CancellationToken.None);
                remaining.Clear();
            }
        }

        if (remaining.Count > 0)
        {
            await WriteBatchAsync(connection, remaining, CancellationToken.None);
        }
    }

    private async Task WriteBatchAsync(
        SqliteConnection connection,
        List<RequestRecord> batch,
        CancellationToken cancellationToken)
    {
        try
        {
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT OR REPLACE INTO request_log (
                    id, timestamp_utc, requested_model, provider, upstream_model, streamed,
                    status_code, prompt_tokens, completion_tokens, usage_is_provider_reported,
                    duration_ms, time_to_first_chunk_ms, error_type,
                    virtual_key_id, organisation, team, application,
                    prompt_tokens_cached, prompt_tokens_cache_write, metered, served_from_cache
                ) VALUES (
                    $id, $timestamp, $requestedModel, $provider, $upstreamModel, $streamed,
                    $statusCode, $promptTokens, $completionTokens, $usageIsProviderReported,
                    $durationMs, $timeToFirstChunkMs, $errorType,
                    $virtualKeyId, $organisation, $team, $application,
                    $cachedPromptTokens, $cacheCreationTokens, $metered, $servedFromCache
                );
                """;

            // Parameters are created once and rebound per row. Recreating the command for
            // every record would reparse the same SQL thousands of times a minute.
            SqliteParameter id = command.Parameters.Add("$id", SqliteType.Text);
            SqliteParameter timestamp = command.Parameters.Add("$timestamp", SqliteType.Text);
            SqliteParameter requestedModel = command.Parameters.Add("$requestedModel", SqliteType.Text);
            SqliteParameter provider = command.Parameters.Add("$provider", SqliteType.Text);
            SqliteParameter upstreamModel = command.Parameters.Add("$upstreamModel", SqliteType.Text);
            SqliteParameter streamed = command.Parameters.Add("$streamed", SqliteType.Integer);
            SqliteParameter statusCode = command.Parameters.Add("$statusCode", SqliteType.Integer);
            SqliteParameter promptTokens = command.Parameters.Add("$promptTokens", SqliteType.Integer);
            SqliteParameter completionTokens = command.Parameters.Add("$completionTokens", SqliteType.Integer);
            SqliteParameter usageReported = command.Parameters.Add("$usageIsProviderReported", SqliteType.Integer);
            SqliteParameter durationMs = command.Parameters.Add("$durationMs", SqliteType.Real);
            SqliteParameter ttfcMs = command.Parameters.Add("$timeToFirstChunkMs", SqliteType.Real);
            SqliteParameter errorType = command.Parameters.Add("$errorType", SqliteType.Text);
            SqliteParameter virtualKeyId = command.Parameters.Add("$virtualKeyId", SqliteType.Text);
            SqliteParameter organisation = command.Parameters.Add("$organisation", SqliteType.Text);
            SqliteParameter team = command.Parameters.Add("$team", SqliteType.Text);
            SqliteParameter application = command.Parameters.Add("$application", SqliteType.Text);
            SqliteParameter cachedPromptTokens = command.Parameters.Add("$cachedPromptTokens", SqliteType.Integer);
            SqliteParameter cacheCreationTokens = command.Parameters.Add("$cacheCreationTokens", SqliteType.Integer);
            SqliteParameter metered = command.Parameters.Add("$metered", SqliteType.Integer);
            SqliteParameter servedFromCache = command.Parameters.Add("$servedFromCache", SqliteType.Integer);

            foreach (RequestRecord record in batch)
            {
                id.Value = record.Id;
                timestamp.Value = record.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                requestedModel.Value = record.RequestedModel;
                provider.Value = (object?)record.Provider ?? DBNull.Value;
                upstreamModel.Value = (object?)record.UpstreamModel ?? DBNull.Value;
                streamed.Value = record.Streamed ? 1 : 0;
                statusCode.Value = record.StatusCode;
                promptTokens.Value = record.PromptTokens;
                completionTokens.Value = record.CompletionTokens;
                usageReported.Value = record.UsageIsProviderReported ? 1 : 0;
                durationMs.Value = record.Duration.TotalMilliseconds;
                ttfcMs.Value = record.TimeToFirstChunk is { } ttfc ? ttfc.TotalMilliseconds : DBNull.Value;
                errorType.Value = (object?)record.ErrorType ?? DBNull.Value;
                virtualKeyId.Value = (object?)record.VirtualKeyId ?? DBNull.Value;
                organisation.Value = (object?)record.Organisation ?? DBNull.Value;
                team.Value = (object?)record.Team ?? DBNull.Value;
                application.Value = (object?)record.Application ?? DBNull.Value;
                cachedPromptTokens.Value = record.CachedPromptTokens;
                cacheCreationTokens.Value = record.CacheCreationTokens;
                metered.Value = record.Metered ? 1 : 0;
                servedFromCache.Value = record.ServedFromCache ? 1 : 0;

                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            _logger.RequestLogBatchWritten(batch.Count);
        }
        catch (SqliteException ex)
        {
            // Losing the request log must not take the gateway down. Inference keeps working;
            // the operator gets a loud error and, in Phase 2, a health check that goes amber
            // so that the gap in the audit trail is visible rather than discovered later.
            _logger.RequestLogWriteFailed(batch.Count, ex);
        }
    }

    /// <summary>Ensures queued records are flushed when the host shuts down.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Complete the writer first, so the drain loop sees an end-of-stream rather than
        // racing a cancellation and leaving records behind.
        _queue.Writer.TryComplete();
        await base.StopAsync(cancellationToken);

        if (_connection is null)
        {
            return;
        }

        // The final drain happens here, not only at the end of ExecuteAsync.
        //
        // BackgroundService offers no guarantee that ExecuteAsync has begun executing by the
        // time StopAsync runs — StartAsync stores the task, and on a fast start/stop the body
        // may never have been scheduled at all. Relying on the loop's own trailing drain
        // therefore loses every queued record in exactly that case, silently and with nothing
        // logged. Draining here is deterministic regardless of what the loop managed to do,
        // and is safe to run twice because the queue is empty the second time.
        await DrainRemainingAsync(_connection);

        await _connection.DisposeAsync();
        _connection = null;
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
