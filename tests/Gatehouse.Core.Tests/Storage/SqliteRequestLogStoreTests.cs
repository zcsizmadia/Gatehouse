using Gatehouse.Configuration;
using Gatehouse.Storage;
using Gatehouse.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Gatehouse.Tests.Storage;

/// <summary>
/// Tests for the default SQLite request log.
/// </summary>
/// <remarks>
/// These use a real database file rather than an in-memory one. The behaviour under test —
/// WAL journalling, batched transactions, migration stamping — is exactly what an in-memory
/// SQLite connection does differently, so faking it would test the wrong thing.
/// </remarks>
public class SqliteRequestLogStoreTests
{
    [Test]
    public async Task Creates_and_stamps_the_schema_on_first_run()
    {
        using var db = new TemporaryDatabase();
        using SqliteRequestLogStore store = db.CreateStore();

        await store.StartAsync(CancellationToken.None);
        await store.StopAsync(CancellationToken.None);

        await using SqliteConnection connection = db.Connect();
        await connection.OpenAsync();

        await Assert.That(await SqliteSchema.GetVersionAsync(connection))
            .IsEqualTo(SqliteSchema.CurrentVersion);
    }

    [Test]
    public async Task Migration_is_idempotent()
    {
        // An operator restarting the gateway must not re-apply migrations, and a second
        // stamped row would make the version query ambiguous.
        using var db = new TemporaryDatabase();
        await using SqliteConnection connection = db.Connect();
        await connection.OpenAsync();

        int first = await SqliteSchema.MigrateAsync(connection);
        int second = await SqliteSchema.MigrateAsync(connection);

        await Assert.That(first).IsEqualTo(SqliteSchema.CurrentVersion);
        await Assert.That(second).IsEqualTo(SqliteSchema.CurrentVersion);
    }

    [Test]
    public async Task Records_a_request_and_reads_it_back()
    {
        using var db = new TemporaryDatabase();
        using SqliteRequestLogStore store = db.CreateStore();
        await store.StartAsync(CancellationToken.None);

        RequestRecord written = Record("chatcmpl-1");
        await store.RecordAsync(written);

        // StopAsync drains the queue, which is the documented guarantee: a record accepted
        // from the request path is not lost to shutdown.
        await store.StopAsync(CancellationToken.None);

        IReadOnlyList<RequestRecord> read = await store.GetRecentAsync(10);

        await Assert.That(read).Count().IsEqualTo(1);
        await Assert.That(read[0].Id).IsEqualTo("chatcmpl-1");
        await Assert.That(read[0].RequestedModel).IsEqualTo("gpt-4o-mini");
        await Assert.That(read[0].Provider).IsEqualTo("openai");
        await Assert.That(read[0].PromptTokens).IsEqualTo(120);
        await Assert.That(read[0].CompletionTokens).IsEqualTo(35);
        await Assert.That(read[0].StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task Preserves_whether_usage_was_provider_reported()
    {
        // The field that makes invoice reconciliation possible. If it does not survive a
        // round trip, every historical row becomes unusable for billing.
        using var db = new TemporaryDatabase();
        using SqliteRequestLogStore store = db.CreateStore();
        await store.StartAsync(CancellationToken.None);

        await store.RecordAsync(Record("measured") with { UsageIsProviderReported = true });
        await store.RecordAsync(Record("estimated") with { UsageIsProviderReported = false });
        await store.StopAsync(CancellationToken.None);

        IReadOnlyList<RequestRecord> read = await store.GetRecentAsync(10);
        Dictionary<string, bool> byId = read.ToDictionary(r => r.Id, r => r.UsageIsProviderReported);

        await Assert.That(byId["measured"]).IsTrue();
        await Assert.That(byId["estimated"]).IsFalse();
    }

    [Test]
    public async Task Preserves_null_columns()
    {
        using var db = new TemporaryDatabase();
        using SqliteRequestLogStore store = db.CreateStore();
        await store.StartAsync(CancellationToken.None);

        await store.RecordAsync(Record("unrouted") with
        {
            Provider = null,
            UpstreamModel = null,
            TimeToFirstChunk = null,
            ErrorType = null,
        });
        await store.StopAsync(CancellationToken.None);

        RequestRecord read = (await store.GetRecentAsync(1))[0];

        await Assert.That(read.Provider).IsNull();
        await Assert.That(read.UpstreamModel).IsNull();
        await Assert.That(read.TimeToFirstChunk).IsNull();
        await Assert.That(read.ErrorType).IsNull();
    }

    [Test]
    public async Task Round_trips_durations()
    {
        using var db = new TemporaryDatabase();
        using SqliteRequestLogStore store = db.CreateStore();
        await store.StartAsync(CancellationToken.None);

        await store.RecordAsync(Record("timed") with
        {
            Duration = TimeSpan.FromMilliseconds(1234.5),
            TimeToFirstChunk = TimeSpan.FromMilliseconds(87.25),
        });
        await store.StopAsync(CancellationToken.None);

        RequestRecord read = (await store.GetRecentAsync(1))[0];

        await Assert.That(read.Duration.TotalMilliseconds).IsEqualTo(1234.5);
        await Assert.That(read.TimeToFirstChunk!.Value.TotalMilliseconds).IsEqualTo(87.25);
    }

    [Test]
    public async Task Returns_the_newest_records_first()
    {
        using var db = new TemporaryDatabase();
        using SqliteRequestLogStore store = db.CreateStore();
        await store.StartAsync(CancellationToken.None);

        DateTimeOffset baseline = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        await store.RecordAsync(Record("oldest") with { Timestamp = baseline });
        await store.RecordAsync(Record("newest") with { Timestamp = baseline.AddMinutes(5) });
        await store.RecordAsync(Record("middle") with { Timestamp = baseline.AddMinutes(2) });
        await store.StopAsync(CancellationToken.None);

        IReadOnlyList<RequestRecord> read = await store.GetRecentAsync(10);

        await Assert.That(read.Select(r => r.Id)).IsEquivalentTo(new[] { "newest", "middle", "oldest" });
    }

    [Test]
    public async Task Honours_the_limit()
    {
        using var db = new TemporaryDatabase();
        using SqliteRequestLogStore store = db.CreateStore();
        await store.StartAsync(CancellationToken.None);

        for (int i = 0; i < 10; i++)
        {
            await store.RecordAsync(Record($"r{i}") with { Timestamp = DateTimeOffset.UnixEpoch.AddMinutes(i) });
        }

        await store.StopAsync(CancellationToken.None);

        await Assert.That(await store.GetRecentAsync(3)).Count().IsEqualTo(3);
    }

    [Test]
    public async Task Writes_a_batch_larger_than_one_transaction_worth()
    {
        // Exercises the batching loop rather than the single-record path: the batch size is
        // 128, so 300 records force several commits plus a partial one.
        using var db = new TemporaryDatabase();
        using SqliteRequestLogStore store = db.CreateStore();
        await store.StartAsync(CancellationToken.None);

        for (int i = 0; i < 300; i++)
        {
            await store.RecordAsync(Record($"bulk-{i:D4}"));
        }

        await store.StopAsync(CancellationToken.None);

        await Assert.That(await store.GetRecentAsync(1000)).Count().IsEqualTo(300);
    }

    [Test]
    public async Task Rejects_a_non_positive_limit()
    {
        using var db = new TemporaryDatabase();
        using SqliteRequestLogStore store = db.CreateStore();

        await Assert.That(async () => await store.GetRecentAsync(0))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Enables_write_ahead_logging()
    {
        // WAL is what lets the background writer commit while readers query. Without it the
        // store would serialise against every read, which is the reason SQLite is usually
        // dismissed for this job.
        using var db = new TemporaryDatabase();
        await using SqliteConnection connection = db.Connect();
        await connection.OpenAsync();
        await SqliteSchema.ApplyPragmasAsync(connection);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        object? mode = await command.ExecuteScalarAsync();

        await Assert.That(((string)mode!).ToUpperInvariant()).IsEqualTo("WAL");
    }

    private static RequestRecord Record(string id) => new()
    {
        Id = id,
        Timestamp = new DateTimeOffset(2026, 8, 23, 10, 30, 0, TimeSpan.Zero),
        RequestedModel = "gpt-4o-mini",
        Provider = "openai",
        UpstreamModel = "gpt-4o-mini-2024-07-18",
        Streamed = true,
        StatusCode = 200,
        PromptTokens = 120,
        CompletionTokens = 35,
        UsageIsProviderReported = true,
        Duration = TimeSpan.FromMilliseconds(450),
        TimeToFirstChunk = TimeSpan.FromMilliseconds(90),
    };

    /// <summary>A database file that deletes itself, along with its WAL sidecars.</summary>
    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            $"gatehouse-test-{Guid.NewGuid():N}.db");

        public string ConnectionString => $"Data Source={_path}";

        public SqliteConnection Connect() => new(ConnectionString);

        public SqliteRequestLogStore CreateStore()
        {
            var options = new GatehouseOptions
            {
                Store = new StoreOptions { ConnectionString = ConnectionString, AutoMigrate = true },
            };

            return new SqliteRequestLogStore(
                Options.Create(options),
                NullLogger<SqliteRequestLogStore>.Instance);
        }

        public void Dispose()
        {
            // Microsoft.Data.Sqlite pools connections, so the file stays locked until the
            // pool is cleared. Without this the cleanup silently fails on Windows.
            SqliteConnection.ClearAllPools();

            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    File.Delete(_path + suffix);
                }
                catch (IOException)
                {
                    // A leaked handle should not fail an otherwise passing test run.
                }
            }
        }
    }
}
