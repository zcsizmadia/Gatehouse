using System.Globalization;
using Gatehouse.Configuration;
using Gatehouse.Metering;
using Gatehouse.Storage;
using Gatehouse.Storage.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Gatehouse.Server.Infrastructure;

/// <summary>
/// The <c>gatehouse usage</c> commands.
/// </summary>
/// <remarks>
/// <para>
/// Reconciliation lives on the command line rather than behind an HTTP endpoint because of who
/// runs it and when. It is a month-end task, performed by someone holding a provider invoice,
/// who needs to feed a file in and read prose out. That is a CLI, and making it an API first
/// would mean the first real user had to write a client to do their job.
/// </para>
/// <para>
/// Read-only. It opens the store, aggregates, prints, and exits — it never writes, so it is
/// safe to run against a live gateway's database. SQLite in WAL mode permits exactly this.
/// </para>
/// </remarks>
internal static class UsageCommands
{
    /// <summary>Runs a <c>usage</c> subcommand.</summary>
    /// <param name="args">The full argument list, beginning with <c>usage</c>.</param>
    /// <param name="configPath">The configuration file, if one was supplied.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(string[] args, string? configPath)
    {
        string? subcommand = args.Length > 1 ? args[1] : null;

        return subcommand switch
        {
            "summary" => await SummaryAsync(args, configPath),
            "reconcile" => await ReconcileAsync(args, configPath),
            _ => Usage(subcommand),
        };
    }

    private static async Task<int> SummaryAsync(string[] args, string? configPath)
    {
        if (!TryReadWindow(args, out UsageWindow? window))
        {
            return 2;
        }

        // Disposed: the store is a BackgroundService and owns a connection once started. It is
        // never started here, but leaving a disposable un-disposed in a short-lived command is
        // the kind of thing that is correct until someone reuses the helper somewhere it is not.
        using SqliteRequestLogStore store = OpenStore(configPath);
        IReadOnlyList<UsageSummary> summaries =
            await store.SummariseAsync(window!, ValueOf(args, "--provider"));

        Console.WriteLine($"Usage for {window}");
        Console.WriteLine();

        if (summaries.Count == 0)
        {
            Console.WriteLine("No requests were recorded in this window.");
            return 0;
        }

        WriteSummaryTable(summaries);
        return 0;
    }

    private static async Task<int> ReconcileAsync(string[] args, string? configPath)
    {
        if (!TryReadWindow(args, out UsageWindow? window))
        {
            return 2;
        }

        string? statementPath = ValueOf(args, "--statement");
        if (string.IsNullOrWhiteSpace(statementPath))
        {
            Console.Error.WriteLine(
                "gatehouse usage reconcile requires --statement <path-to-csv>.");
            Console.Error.WriteLine($"The CSV needs a header row of: {ProviderStatementReader.ExpectedColumns}");
            return 2;
        }

        if (!File.Exists(statementPath))
        {
            Console.Error.WriteLine($"No such statement file: {statementPath}");
            return 2;
        }

        IReadOnlyList<ProviderStatementLine> statement = ProviderStatementReader.Parse(
            await File.ReadAllTextAsync(statementPath),
            out IReadOnlyList<string> errors);

        if (errors.Count > 0)
        {
            Console.Error.WriteLine($"The statement could not be read ({errors.Count} problem(s)):");
            foreach (string error in errors)
            {
                Console.Error.WriteLine($"  {error}");
            }

            return 2;
        }

        ReconciliationTolerance tolerance = ReconciliationTolerance.Default;

        if (ValueOf(args, "--tolerance-tokens") is { } raw)
        {
            if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long absolute)
                || absolute < 0)
            {
                Console.Error.WriteLine("--tolerance-tokens must be a non-negative whole number.");
                return 2;
            }

            tolerance = tolerance with { AbsoluteTokens = absolute };
        }

        // Disposed: the store is a BackgroundService and owns a connection once started. It is
        // never started here, but leaving a disposable un-disposed in a short-lived command is
        // the kind of thing that is correct until someone reuses the helper somewhere it is not.
        using SqliteRequestLogStore store = OpenStore(configPath);
        IReadOnlyList<UsageSummary> recorded =
            await store.SummariseAsync(window!, ValueOf(args, "--provider"));

        ReconciliationReport report =
            MeteringReconciliation.Reconcile(window!, statement, recorded, tolerance);

        WriteReport(report);

        // Exit 1 on findings, so this can be a scheduled job whose failure is visible without
        // anyone reading the output. Exit 2 stays reserved for bad input.
        return report.IsClean ? 0 : 1;
    }

    private static void WriteSummaryTable(IReadOnlyList<UsageSummary> summaries)
    {
        Console.WriteLine(
            $"{"PROVIDER",-16} {"MODEL",-28} {"REQUESTS",10} {"PROMPT",14} {"CACHED",12} "
            + $"{"COMPLETION",14} {"CONFIDENCE",11}");

        long requests = 0;
        long prompt = 0;
        long completion = 0;
        long unaccounted = 0;

        foreach (UsageSummary s in summaries)
        {
            Console.WriteLine(
                $"{Fit(s.Provider, 16),-16} {Fit(s.UpstreamModel, 28),-28} {s.Requests,10:N0} "
                + $"{s.PromptTokens,14:N0} {s.CachedPromptTokens,12:N0} {s.CompletionTokens,14:N0} "
                + $"{s.Confidence,10:P0}");

            requests += s.Requests;
            prompt += s.PromptTokens;
            completion += s.CompletionTokens;
            unaccounted += s.UnaccountedRequests;
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Total: {requests:N0} request(s), {prompt:N0} prompt + {completion:N0} completion "
            + $"= {prompt + completion:N0} tokens.");

        // Stated on every summary, not only when reconciling. A total printed without its
        // confidence is the number that ends up in a spreadsheet as though it were exact.
        if (unaccounted > 0)
        {
            Console.WriteLine(
                $"{unaccounted:N0} request(s) had no readable token counts, so the totals above "
                + "are a floor rather than a measurement. 'gatehouse usage reconcile' bounds the "
                + "gap against a provider statement.");
        }
        else
        {
            Console.WriteLine("Every request in this window reported token counts from the provider.");
        }
    }

    private static void WriteReport(ReconciliationReport report)
    {
        Console.WriteLine($"Reconciliation for {report.Window}");
        Console.WriteLine(
            $"Tolerance: {report.Tolerance.AbsoluteTokens:N0} tokens "
            + $"or {report.Tolerance.RelativeShare:P2}, whichever is larger.");
        Console.WriteLine();

        Console.WriteLine(
            $"{"PROVIDER",-16} {"MODEL",-24} {"STATEMENT",14} {"RECORDED",14} {"VARIANCE",14}  VERDICT");

        foreach (ReconciliationLine line in report.Lines)
        {
            long statement = line.Statement is null
                ? 0
                : line.Statement.PromptTokens + line.Statement.CompletionTokens;

            Console.WriteLine(
                $"{Fit(line.Provider, 16),-16} {Fit(line.UpstreamModel, 24),-24} "
                + $"{statement,14:N0} {line.Recorded?.TotalTokens ?? 0,14:N0} "
                + $"{Signed(line.Variance),14}  {Describe(line.Verdict)}");
        }

        if (report.IsClean)
        {
            Console.WriteLine();
            Console.WriteLine("Every line balanced or was accounted for by known gaps.");
        }

        foreach (ReconciliationLine finding in report.Lines.Where(l => l.Explanations.Count > 0))
        {
            Console.WriteLine();
            Console.WriteLine($"{finding.Provider}/{finding.UpstreamModel} — {Describe(finding.Verdict)}");

            foreach (string explanation in finding.Explanations)
            {
                Console.WriteLine($"  - {explanation}");
            }
        }
    }

    private static string Describe(ReconciliationVerdict verdict) => verdict switch
    {
        ReconciliationVerdict.Balanced => "balanced",
        ReconciliationVerdict.WithinKnownGaps => "within known gaps",
        ReconciliationVerdict.Unexplained => "UNEXPLAINED",
        ReconciliationVerdict.NoLocalRecord => "NOT RECORDED BY GATEHOUSE",
        ReconciliationVerdict.NotOnStatement => "NOT ON STATEMENT",
        _ => verdict.ToString(),
    };

    /// <summary>Formats a variance with an explicit sign.</summary>
    /// <remarks>
    /// The direction is the first thing a reader needs: "the provider billed more than we saw"
    /// and "we recorded more than the provider billed" have completely different diagnoses, and
    /// in a right-aligned column of digits a lone minus sign is easy to miss.
    /// </remarks>
    private static string Signed(long value) =>
        value > 0 ? value.ToString("+#,##0", CultureInfo.InvariantCulture)
                  : value.ToString("#,##0", CultureInfo.InvariantCulture);

    private static string Fit(string value, int width) =>
        value.Length <= width ? value : string.Concat(value.AsSpan(0, width - 1), "…");

    private static bool TryReadWindow(string[] args, out UsageWindow? window)
    {
        window = null;

        string? month = ValueOf(args, "--month");
        string? from = ValueOf(args, "--from");
        string? to = ValueOf(args, "--to");

        if (month is not null)
        {
            if (from is not null || to is not null)
            {
                Console.Error.WriteLine("--month cannot be combined with --from or --to.");
                return false;
            }

            if (!UsageWindow.TryParseMonth(month, out window))
            {
                Console.Error.WriteLine($"--month must be YYYY-MM, for example 2026-08. Got: {month}");
                return false;
            }

            return true;
        }

        if (from is null || to is null)
        {
            Console.Error.WriteLine("Specify either --month YYYY-MM, or both --from and --to as ISO-8601 instants.");
            return false;
        }

        if (!DateTimeOffset.TryParse(from, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset start)
            || !DateTimeOffset.TryParse(to, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset end))
        {
            Console.Error.WriteLine("--from and --to must be ISO-8601 instants, for example 2026-08-01T00:00:00Z.");
            return false;
        }

        if (end <= start)
        {
            Console.Error.WriteLine("--to must be after --from.");
            return false;
        }

        window = new UsageWindow(start, end);
        return true;
    }

    /// <summary>
    /// Opens the usage store directly, without building a host.
    /// </summary>
    /// <remarks>
    /// The store is constructed rather than resolved from DI, and its migration hook is never
    /// run: this command reads, and a reporting command that silently migrated an operator's
    /// database as a side effect of asking it a question would be a nasty surprise. A database
    /// older than the current schema will fail the query with a missing-column error naming the
    /// column, which is the correct outcome — start the gateway to migrate.
    /// </remarks>
    private static SqliteRequestLogStore OpenStore(string? configPath)
    {
        var configuration = new ConfigurationBuilder();

        if (!string.IsNullOrWhiteSpace(configPath))
        {
            configuration.AddJsonFile(configPath, optional: false);
        }

        configuration.AddEnvironmentVariables();

        GatehouseOptions options = configuration.Build()
                                       .GetSection(GatehouseOptions.SectionName)
                                       .Get<GatehouseOptions>()
                                   ?? new GatehouseOptions();

        return new SqliteRequestLogStore(
            Options.Create(options),
            NullLogger<SqliteRequestLogStore>.Instance);
    }

    private static string? ValueOf(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static int Usage(string? subcommand)
    {
        TextWriter output = subcommand is null ? Console.Out : Console.Error;

        output.WriteLine(
            """
            gatehouse usage — what the gateway recorded, and whether it matches the bill

            Usage:
              gatehouse usage summary   [--month YYYY-MM | --from <iso> --to <iso>]
                                        [--provider <name>] [--config <path>]

              gatehouse usage reconcile --statement <path.csv>
                                        [--month YYYY-MM | --from <iso> --to <iso>]
                                        [--provider <name>] [--tolerance-tokens <n>]
                                        [--config <path>]

            The statement is a CSV export from the provider's own usage dashboard, with a
            header row of:

                provider,model,prompt_tokens,completion_tokens

            'provider' must match the key used in the Gatehouse configuration; 'model' is the
            upstream model or Azure deployment name. Repeated rows for one model are summed,
            so a per-day export can be fed in unchanged.

            reconcile exits 1 when a line needs investigating and 2 on bad input, so it can be
            run as a scheduled job.

            Both commands are read-only and safe to run against a live gateway.
            """);

        return subcommand is null ? 1 : 2;
    }
}
