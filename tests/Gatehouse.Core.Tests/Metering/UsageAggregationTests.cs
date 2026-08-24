using Gatehouse.Configuration;
using Gatehouse.Metering;
using Gatehouse.Storage;
using Gatehouse.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Gatehouse.Tests.Metering;

/// <summary>
/// Tests for the usage aggregation query.
/// </summary>
/// <remarks>
/// Against a real database, because the behaviour under test is the SQL. Every classification a
/// reconciliation depends on — metered against unmetered, reported against absent, inside the
/// window against outside it — is a CASE expression in one statement, and a fake store would
/// only prove that the test's own arithmetic works.
/// </remarks>
public class UsageAggregationTests
{
    private static readonly DateTimeOffset InsideWindow = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Aggregates_tokens_per_provider_and_upstream_model()
    {
        using var db = new TemporaryDatabase();

        await db.WriteAsync(
            Record("openai", "gpt-4o-mini", prompt: 100, completion: 20),
            Record("openai", "gpt-4o-mini", prompt: 300, completion: 40),
            Record("openai", "gpt-4o", prompt: 1_000, completion: 200),
            Record("anthropic", "claude-sonnet-5", prompt: 500, completion: 60));

        IReadOnlyList<UsageSummary> summaries = await db.SummariseAsync();

        await Assert.That(summaries.Count).IsEqualTo(3);

        UsageSummary mini = summaries.Single(s => s.UpstreamModel == "gpt-4o-mini");
        await Assert.That(mini.Requests).IsEqualTo(2);
        await Assert.That(mini.PromptTokens).IsEqualTo(400);
        await Assert.That(mini.CompletionTokens).IsEqualTo(60);
        await Assert.That(mini.TotalTokens).IsEqualTo(460);
    }

    [Test]
    public async Task Excludes_requests_outside_the_window()
    {
        // Half-open: the start instant is in, the end instant is out. Getting this wrong makes
        // twelve monthly reports fail to add up to the year, and nobody notices until they do.
        using var db = new TemporaryDatabase();
        UsageWindow window = UsageWindow.Month(2026, 8);

        await db.WriteAsync(
            Record("openai", "m", prompt: 1, completion: 0, at: window.FromInclusive.AddTicks(-1)),
            Record("openai", "m", prompt: 10, completion: 0, at: window.FromInclusive),
            Record("openai", "m", prompt: 100, completion: 0, at: window.ToExclusive.AddTicks(-1)),
            Record("openai", "m", prompt: 1_000, completion: 0, at: window.ToExclusive));

        IReadOnlyList<UsageSummary> summaries = await db.SummariseAsync(window);

        await Assert.That(summaries[0].PromptTokens).IsEqualTo(110);
    }

    [Test]
    public async Task Counts_unmetered_passthrough_separately()
    {
        using var db = new TemporaryDatabase();

        await db.WriteAsync(
            Record("openai", "gpt-4o-mini", prompt: 100, completion: 20),
            Record("openai", "gpt-4o-mini", prompt: 0, completion: 0, metered: false, reported: false));

        UsageSummary summary = (await db.SummariseAsync())[0];

        await Assert.That(summary.Requests).IsEqualTo(2);
        await Assert.That(summary.UnmeteredRequests).IsEqualTo(1);
        await Assert.That(summary.ProviderReportedRequests).IsEqualTo(1);
        await Assert.That(summary.UnaccountedRequests).IsEqualTo(1);
        await Assert.That(summary.Confidence).IsEqualTo(0.5);
    }

    [Test]
    public async Task Does_not_treat_a_reported_zero_as_an_authoritative_measurement()
    {
        // An upstream that rejects a request reports zero tokens. Counting that as measured
        // usage would let a month of rejections look like a month of free traffic, and drive
        // the confidence figure to 100% precisely when it should be falling.
        using var db = new TemporaryDatabase();

        await db.WriteAsync(
            Record("openai", "gpt-4o-mini", prompt: 100, completion: 20),
            Record("openai", "gpt-4o-mini", prompt: 0, completion: 0, reported: true, status: 400));

        UsageSummary summary = (await db.SummariseAsync())[0];

        await Assert.That(summary.ProviderReportedRequests).IsEqualTo(1);
        await Assert.That(summary.RequestsWithoutUsage).IsEqualTo(1);
        await Assert.That(summary.FailedRequests).IsEqualTo(1);
    }

    [Test]
    public async Task Sums_the_cache_split_that_billing_depends_on()
    {
        // Cache reads bill at a fraction of the input rate and cache writes at a premium, so a
        // reconciliation that only has the prompt total can see a variance and not explain it.
        using var db = new TemporaryDatabase();

        await db.WriteAsync(
            Record("anthropic", "claude-sonnet-5", prompt: 1_000, completion: 100, cached: 600, cacheWrite: 200),
            Record("anthropic", "claude-sonnet-5", prompt: 500, completion: 50, cached: 100, cacheWrite: 0));

        UsageSummary summary = (await db.SummariseAsync())[0];

        await Assert.That(summary.PromptTokens).IsEqualTo(1_500);
        await Assert.That(summary.CachedPromptTokens).IsEqualTo(700);
        await Assert.That(summary.CacheCreationTokens).IsEqualTo(200);
        await Assert.That(summary.UncachedPromptTokens).IsEqualTo(600);
    }

    [Test]
    public async Task Excludes_requests_that_never_reached_a_provider()
    {
        // An unknown model or a rejected credential is recorded with no provider. No provider
        // billed for it, so including it would add zero-token lines to a usage report and
        // dilute every average computed from it.
        using var db = new TemporaryDatabase();

        await db.WriteAsync(
            Record("openai", "gpt-4o-mini", prompt: 100, completion: 20),
            Record(null, null, prompt: 0, completion: 0, status: 404, reported: false));

        IReadOnlyList<UsageSummary> summaries = await db.SummariseAsync();

        await Assert.That(summaries.Count).IsEqualTo(1);
        await Assert.That(summaries[0].Requests).IsEqualTo(1);
    }

    [Test]
    public async Task Filters_to_one_provider_when_asked()
    {
        using var db = new TemporaryDatabase();

        await db.WriteAsync(
            Record("openai", "gpt-4o-mini", prompt: 100, completion: 20),
            Record("anthropic", "claude-sonnet-5", prompt: 500, completion: 60));

        IReadOnlyList<UsageSummary> summaries = await db.SummariseAsync(provider: "anthropic");

        await Assert.That(summaries.Count).IsEqualTo(1);
        await Assert.That(summaries[0].Provider).IsEqualTo("anthropic");
    }

    [Test]
    public async Task Reports_full_confidence_for_a_window_with_nothing_unaccounted()
    {
        using var db = new TemporaryDatabase();

        await db.WriteAsync(Record("openai", "gpt-4o-mini", prompt: 100, completion: 20));

        UsageSummary summary = (await db.SummariseAsync())[0];

        await Assert.That(summary.Confidence).IsEqualTo(1);
        await Assert.That(summary.UnaccountedRequests).IsEqualTo(0);
    }

    private static RequestRecord Record(
        string? provider,
        string? upstreamModel,
        int prompt,
        int completion,
        int cached = 0,
        int cacheWrite = 0,
        bool metered = true,
        bool reported = true,
        int status = 200,
        DateTimeOffset? at = null) =>
        new()
        {
            Id = $"chatcmpl-{Guid.NewGuid():N}",
            Timestamp = at ?? InsideWindow,
            RequestedModel = upstreamModel ?? "unrouted",
            Provider = provider,
            UpstreamModel = upstreamModel,
            Streamed = false,
            StatusCode = status,
            PromptTokens = prompt,
            CompletionTokens = completion,
            CachedPromptTokens = cached,
            CacheCreationTokens = cacheWrite,
            Metered = metered,
            UsageIsProviderReported = reported,
            Duration = TimeSpan.FromMilliseconds(10),
        };

    /// <summary>A real database file, torn down with the test that owns it.</summary>
    /// <remarks>
    /// <c>Pooling=False</c> for the reason given at length in
    /// <see cref="Storage.SqliteRequestLogStoreTests"/>: the only way to force a pooled handle
    /// closed is process-global, and TUnit runs these in parallel.
    /// </remarks>
    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            $"gatehouse-usage-{Guid.NewGuid():N}.db");

        private string ConnectionString => $"Data Source={_path};Pooling=False";

        /// <summary>Writes records through the real store, then shuts it down to flush.</summary>
        public async Task WriteAsync(params RequestRecord[] records)
        {
            using SqliteRequestLogStore store = CreateStore();
            await store.StartAsync(CancellationToken.None);

            foreach (RequestRecord record in records)
            {
                await store.RecordAsync(record);
            }

            // Stopping drains the queue. Writing through the real store rather than with hand
            // rolled SQL is what makes these tests catch a column the insert path forgot.
            await store.StopAsync(CancellationToken.None);
        }

        public async Task<IReadOnlyList<UsageSummary>> SummariseAsync(
            UsageWindow? window = null,
            string? provider = null)
        {
            using SqliteRequestLogStore store = CreateStore();
            return await store.SummariseAsync(window ?? UsageWindow.Month(2026, 8), provider);
        }

        public void Dispose()
        {
            foreach (string suffix in (string[])["", "-wal", "-shm"])
            {
                try
                {
                    File.Delete(_path + suffix);
                }
                catch (IOException)
                {
                    // A leaked handle on Windows is not worth failing a passing test over; the
                    // file lands in the temp directory and the OS reclaims it.
                }
            }
        }

        private SqliteRequestLogStore CreateStore() =>
            new(
                Options.Create(new GatehouseOptions
                {
                    Store = new StoreOptions { ConnectionString = ConnectionString, AutoMigrate = true },
                }),
                NullLogger<SqliteRequestLogStore>.Instance);
    }
}
