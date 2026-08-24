using Gatehouse.Metering;

namespace Gatehouse.Tests.Metering;

/// <summary>Tests for reconciling a provider statement against recorded usage.</summary>
public class ReconciliationTests
{
    private static readonly UsageWindow August = UsageWindow.Month(2026, 8);

    [Test]
    public async Task Balances_when_the_statement_matches_what_was_recorded()
    {
        ReconciliationReport report = Reconcile(
            statement: [Line("openai", "gpt-4o-mini", 1_000_000, 200_000)],
            recorded: [Recorded("openai", "gpt-4o-mini", prompt: 1_000_000, completion: 200_000, requests: 5_000)]);

        await Assert.That(report.IsClean).IsTrue();
        await Assert.That(report.Lines[0].Verdict).IsEqualTo(ReconciliationVerdict.Balanced);
        await Assert.That(report.Lines[0].Variance).IsEqualTo(0);
    }

    [Test]
    public async Task Treats_a_small_difference_as_noise()
    {
        // Providers round, and window boundaries never align to the instant. A reconciliation
        // that reports every one of those as a finding is one nobody runs a second time.
        ReconciliationReport report = Reconcile(
            statement: [Line("openai", "gpt-4o-mini", 1_000_400, 200_000)],
            recorded: [Recorded("openai", "gpt-4o-mini", prompt: 1_000_000, completion: 200_000, requests: 5_000)]);

        await Assert.That(report.Lines[0].Verdict).IsEqualTo(ReconciliationVerdict.Balanced);
    }

    [Test]
    public async Task Scales_tolerance_with_the_size_of_the_statement()
    {
        // 0.4% of a billion tokens is four million — noise at that scale, and a catastrophe if
        // the tolerance were absolute only.
        ReconciliationReport report = Reconcile(
            statement: [Line("openai", "gpt-4o-mini", 1_000_000_000, 0)],
            recorded: [Recorded("openai", "gpt-4o-mini", prompt: 996_000_000, completion: 0, requests: 5_000_000)]);

        await Assert.That(report.Lines[0].Verdict).IsEqualTo(ReconciliationVerdict.Balanced);
    }

    [Test]
    public async Task Explains_a_gap_with_the_unmetered_requests_that_could_account_for_it()
    {
        // 100 passthrough requests among 1,100. The 1,000 readable ones total 1,200,000 tokens
        // and so average 1,200 each, which bounds the unreadable ones at about 120,000 — enough
        // to cover the 100,000 the provider billed above what Gatehouse saw.
        ReconciliationReport report = Reconcile(
            statement: [Line("openai", "gpt-4o-mini", 1_100_000, 200_000)],
            recorded:
            [
                Recorded(
                    "openai",
                    "gpt-4o-mini",
                    prompt: 1_000_000,
                    completion: 200_000,
                    requests: 1_100,
                    reported: 1_000,
                    unmetered: 100),
            ]);

        ReconciliationLine line = report.Lines[0];

        await Assert.That(line.Verdict).IsEqualTo(ReconciliationVerdict.WithinKnownGaps);
        await Assert.That(line.Variance).IsEqualTo(100_000);
        await Assert.That(line.ExplainableVariance).IsEqualTo(120_000);
        await Assert.That(string.Join(" ", line.Explanations)).Contains("passthrough");
        await Assert.That(report.IsClean).IsTrue();
    }

    [Test]
    public async Task Reports_a_gap_larger_than_the_known_gaps_as_unexplained()
    {
        // Every request was measured, so nothing on the Gatehouse side can account for the
        // provider billing half again as much. This is the finding the whole feature exists
        // to produce: traffic is very likely reaching the provider without passing through.
        ReconciliationReport report = Reconcile(
            statement: [Line("openai", "gpt-4o-mini", 1_500_000, 300_000)],
            recorded:
            [
                Recorded("openai", "gpt-4o-mini", prompt: 1_000_000, completion: 200_000, requests: 1_000),
            ]);

        ReconciliationLine line = report.Lines[0];

        await Assert.That(line.Verdict).IsEqualTo(ReconciliationVerdict.Unexplained);
        await Assert.That(line.Variance).IsEqualTo(600_000);
        await Assert.That(line.ExplainableVariance).IsEqualTo(0);
        await Assert.That(string.Join(" ", line.Explanations)).Contains("unaccounted for");
        await Assert.That(report.IsClean).IsFalse();
    }

    [Test]
    public async Task Does_not_let_unreadable_requests_excuse_an_over_count()
    {
        // Gatehouse recorded MORE than the provider billed. Unmetered requests can only push
        // the provider's figure up, so they cannot explain this direction — and accepting them
        // as an explanation would hide a double count, which is the failure mode that
        // overcharges an internal team.
        ReconciliationReport report = Reconcile(
            statement: [Line("openai", "gpt-4o-mini", 500_000, 100_000)],
            recorded:
            [
                Recorded(
                    "openai",
                    "gpt-4o-mini",
                    prompt: 1_000_000,
                    completion: 200_000,
                    requests: 1_100,
                    reported: 1_000,
                    unmetered: 100),
            ]);

        ReconciliationLine line = report.Lines[0];

        await Assert.That(line.Verdict).IsEqualTo(ReconciliationVerdict.Unexplained);
        await Assert.That(line.ExplainableVariance).IsEqualTo(0);
        await Assert.That(string.Join(" ", line.Explanations)).Contains("double count");
    }

    [Test]
    public async Task Flags_a_model_the_provider_billed_for_and_gatehouse_never_saw()
    {
        ReconciliationReport report = Reconcile(
            statement: [Line("openai", "gpt-4o", 800_000, 100_000)],
            recorded: [Recorded("openai", "gpt-4o-mini", prompt: 1_000, completion: 100, requests: 10)]);

        ReconciliationLine finding = report.Lines[0];

        await Assert.That(finding.Verdict).IsEqualTo(ReconciliationVerdict.NoLocalRecord);
        await Assert.That(finding.UpstreamModel).IsEqualTo("gpt-4o");
        await Assert.That(string.Join(" ", finding.Explanations)).Contains("outside the gateway");
    }

    [Test]
    public async Task Flags_recorded_usage_the_statement_omits_rather_than_dropping_it()
    {
        // Silently ignoring rows only one side has is how a reconciliation comes to agree with
        // everything it is given.
        ReconciliationReport report = Reconcile(
            statement: [Line("openai", "gpt-4o-mini", 1_000_000, 200_000)],
            recorded:
            [
                Recorded("openai", "gpt-4o-mini", prompt: 1_000_000, completion: 200_000, requests: 1_000),
                Recorded("anthropic", "claude-sonnet-5", prompt: 500_000, completion: 90_000, requests: 400),
            ]);

        ReconciliationLine finding = report.Lines[0];

        await Assert.That(finding.Verdict).IsEqualTo(ReconciliationVerdict.NotOnStatement);
        await Assert.That(finding.Provider).IsEqualTo("anthropic");
        await Assert.That(report.IsClean).IsFalse();
    }

    [Test]
    public async Task Sums_repeated_statement_rows_for_one_model()
    {
        // A per-day export lists each model many times. Replacing rather than summing would
        // reconcile the last day of the month against the whole month's recorded usage, and
        // report a catastrophic shortfall every time.
        ReconciliationReport report = Reconcile(
            statement:
            [
                Line("openai", "gpt-4o-mini", 400_000, 80_000),
                Line("openai", "gpt-4o-mini", 350_000, 70_000),
                Line("openai", "gpt-4o-mini", 250_000, 50_000),
            ],
            recorded: [Recorded("openai", "gpt-4o-mini", prompt: 1_000_000, completion: 200_000, requests: 3_000)]);

        await Assert.That(report.Lines.Count).IsEqualTo(1);
        await Assert.That(report.Lines[0].Verdict).IsEqualTo(ReconciliationVerdict.Balanced);
    }

    [Test]
    public async Task Matches_provider_and_model_without_regard_to_case_or_padding()
    {
        // Provider exports are full of trailing spaces and inconsistent casing. Treating
        // "GPT-4o-mini " as a different model would report two findings for one healthy line.
        ReconciliationReport report = Reconcile(
            statement: [Line(" OpenAI ", "GPT-4o-Mini ", 1_000_000, 200_000)],
            recorded: [Recorded("openai", "gpt-4o-mini", prompt: 1_000_000, completion: 200_000, requests: 1_000)]);

        await Assert.That(report.Lines.Count).IsEqualTo(1);
        await Assert.That(report.Lines[0].Verdict).IsEqualTo(ReconciliationVerdict.Balanced);
    }

    [Test]
    public async Task Puts_the_worst_verdict_first()
    {
        ReconciliationReport report = Reconcile(
            statement:
            [
                Line("openai", "balanced-model", 1_000, 100),
                Line("openai", "missing-model", 900_000, 100_000),
            ],
            recorded:
            [
                Recorded("openai", "balanced-model", prompt: 1_000, completion: 100, requests: 10),
            ]);

        // An operator reads the top of this and stops, so the top has to be the thing that
        // matters.
        await Assert.That(report.Lines[0].Verdict).IsEqualTo(ReconciliationVerdict.NoLocalRecord);
        await Assert.That(report.Lines[^1].Verdict).IsEqualTo(ReconciliationVerdict.Balanced);
    }

    [Test]
    public async Task Mentions_failed_requests_as_a_cost_it_cannot_measure()
    {
        ReconciliationReport report = Reconcile(
            statement: [Line("openai", "gpt-4o-mini", 1_100_000, 200_000)],
            recorded:
            [
                Recorded(
                    "openai",
                    "gpt-4o-mini",
                    prompt: 1_000_000,
                    completion: 200_000,
                    requests: 1_050,
                    reported: 1_000,
                    withoutUsage: 50,
                    failed: 50),
            ]);

        await Assert.That(string.Join(" ", report.Lines[0].Explanations)).Contains("failed");
    }

    [Test]
    public async Task Says_so_when_there_is_no_basis_for_estimating_the_gap()
    {
        // Nothing readable at all, so there is no mean to price the unreadable requests at.
        // Reporting a confident zero here would be the worst available answer.
        ReconciliationReport report = Reconcile(
            statement: [Line("openai", "gpt-4o-mini", 900_000, 100_000)],
            recorded:
            [
                Recorded(
                    "openai",
                    "gpt-4o-mini",
                    prompt: 0,
                    completion: 0,
                    requests: 500,
                    reported: 0,
                    unmetered: 500),
            ]);

        ReconciliationLine line = report.Lines[0];

        await Assert.That(line.Verdict).IsEqualTo(ReconciliationVerdict.Unexplained);
        await Assert.That(string.Join(" ", line.Explanations)).Contains("no basis for estimating");
    }

    private static ReconciliationReport Reconcile(
        ProviderStatementLine[] statement,
        UsageSummary[] recorded) =>
        MeteringReconciliation.Reconcile(August, statement, recorded);

    private static ProviderStatementLine Line(string provider, string model, long prompt, long completion) =>
        new(provider, model, prompt, completion);

    private static UsageSummary Recorded(
        string provider,
        string model,
        long prompt,
        long completion,
        long requests,
        long? reported = null,
        long unmetered = 0,
        long withoutUsage = 0,
        long failed = 0) =>
        new()
        {
            Provider = provider,
            UpstreamModel = model,
            PromptTokens = prompt,
            CompletionTokens = completion,
            Requests = requests,
            ProviderReportedRequests = reported ?? requests,
            UnmeteredRequests = unmetered,
            RequestsWithoutUsage = withoutUsage,
            FailedRequests = failed,
        };
}
