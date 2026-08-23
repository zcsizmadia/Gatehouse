using System.Globalization;
using System.Threading.Channels;
using Gatehouse.Configuration;
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
public sealed class SqliteRequestLogStore : BackgroundService, IRequestLogStore
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
                   duration_ms, time_to_first_chunk_ms, error_type
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
            });
        }

        return records;
    }

    /// <summary>
    /// Opens the database, migrates it, and drains queued records until shutdown.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(stoppingToken);
        await SqliteSchema.ApplyPragmasAsync(connection, stoppingToken);

        if (_autoMigrate)
        {
            int version = await SqliteSchema.MigrateAsync(connection, stoppingToken);
            _logger.SchemaReady(version);
        }

        List<RequestRecord> batch = new(MaxBatchSize);

        // Drain on shutdown rather than abandoning the queue: records already accepted from
        // the request path have been promised to the caller as recorded.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FillBatchAsync(batch, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (batch.Count > 0)
            {
                await WriteBatchAsync(connection, batch, CancellationToken.None);
                batch.Clear();
            }
        }

        await DrainRemainingAsync(connection);
    }

    private async Task FillBatchAsync(List<RequestRecord> batch, CancellationToken stoppingToken)
    {
        // Block until at least one record is available, then take whatever else is already
        // queued without waiting. This keeps latency low when traffic is light and batches
        // naturally when it is heavy, with no timer to tune.
        RequestRecord first = await _queue.Reader.ReadAsync(stoppingToken);
        batch.Add(first);

        while (batch.Count < MaxBatchSize && _queue.Reader.TryRead(out RequestRecord? next))
        {
            batch.Add(next);
        }
    }

    private async Task DrainRemainingAsync(SqliteConnection connection)
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
                    duration_ms, time_to_first_chunk_ms, error_type
                ) VALUES (
                    $id, $timestamp, $requestedModel, $provider, $upstreamModel, $streamed,
                    $statusCode, $promptTokens, $completionTokens, $usageIsProviderReported,
                    $durationMs, $timeToFirstChunkMs, $errorType
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

                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
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
        _queue.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }
}
