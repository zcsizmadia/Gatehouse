namespace Gatehouse.Metering;

/// <summary>
/// One line of a provider's own usage statement.
/// </summary>
/// <param name="Provider">The Gatehouse provider key this line belongs to.</param>
/// <param name="UpstreamModel">The model as the provider names it.</param>
/// <param name="PromptTokens">Input tokens the provider says it billed.</param>
/// <param name="CompletionTokens">Output tokens the provider says it billed.</param>
/// <remarks>
/// Deliberately a token statement rather than a currency one. Reconciling money requires a
/// price book per provider per model per date, which is a standing maintenance liability and a
/// source of wrong answers whenever a vendor changes a rate — that arrives with the FOCUS
/// chargeback work in Phase 3. Tokens are what both sides can state without interpretation,
/// and every provider's usage export gives them.
/// </remarks>
public sealed record ProviderStatementLine(
    string Provider,
    string UpstreamModel,
    long PromptTokens,
    long CompletionTokens);

/// <summary>How a line's variance was judged.</summary>
/// <remarks>
/// The numeric values are ordered by <em>severity</em>, because the report sorts on them and an
/// operator reads the top of it and stops. Severity here means "how likely is this to be money
/// leaving without governance", which is why a provider billing for a model Gatehouse has never
/// heard of outranks a variance Gatehouse cannot explain: the first means a credential is in use
/// outside the gateway entirely, the second only that some of it is. A line the statement omits
/// ranks lowest of the findings because it is usually an artefact of how the export was scoped.
/// </remarks>
public enum ReconciliationVerdict
{
    /// <summary>Within tolerance. Nothing to explain.</summary>
    Balanced = 0,

    /// <summary>
    /// Outside tolerance, but no larger than the gaps Gatehouse already knows it has.
    /// </summary>
    /// <remarks>
    /// Not a clean bill of health — it means the difference is consistent with the requests
    /// Gatehouse could not read, rather than proven to be caused by them.
    /// </remarks>
    WithinKnownGaps = 1,

    /// <summary>
    /// Gatehouse recorded usage the provider's statement does not mention.
    /// </summary>
    /// <remarks>
    /// Almost always a window-boundary or statement-scope problem rather than a billing one —
    /// but it is reported instead of dropped, because silently ignoring rows that only one
    /// side has is how a reconciliation comes to agree with everything.
    /// </remarks>
    NotOnStatement = 2,

    /// <summary>
    /// Outside tolerance by more than the known gaps can account for. Investigate.
    /// </summary>
    /// <remarks>
    /// The usual causes, in the order worth checking: traffic reaching the provider without
    /// going through Gatehouse, a second Gatehouse deployment sharing the same provider
    /// account, or the statement covering a different period than it appears to.
    /// </remarks>
    Unexplained = 3,

    /// <summary>The provider billed for a model Gatehouse has no record of at all.</summary>
    /// <remarks>
    /// The most serious verdict, and distinct from <see cref="Unexplained"/> because the
    /// diagnosis differs: not "some requests bypassed the gateway" but "all of them did", which
    /// means a credential for this provider is in use outside Gatehouse entirely. Every
    /// governance control the gateway offers is inert for that traffic.
    /// </remarks>
    NoLocalRecord = 4,
}

/// <summary>Tolerance for treating a variance as noise.</summary>
/// <param name="AbsoluteTokens">Variance at or below this is always noise.</param>
/// <param name="RelativeShare">Variance at or below this share of the statement is noise.</param>
/// <remarks>
/// Both, not either alone. A pure relative tolerance calls a 40-token difference on a 100-token
/// month a catastrophe; a pure absolute one calls a 5,000-token difference on a billion-token
/// month a problem worth waking someone for. The defaults — 1,000 tokens or 0.5% — are set so
/// that ordinary rounding and clock skew do not generate findings, because a reconciliation
/// that cries wolf monthly is one nobody runs.
/// </remarks>
public sealed record ReconciliationTolerance(long AbsoluteTokens = 1_000, double RelativeShare = 0.005)
{
    /// <summary>The default tolerance.</summary>
    public static ReconciliationTolerance Default { get; } = new();

    /// <summary>Whether a variance is within tolerance for a statement of this size.</summary>
    /// <param name="variance">The signed variance.</param>
    /// <param name="statementTotal">The statement figure the variance is relative to.</param>
    public bool IsNoise(long variance, long statementTotal)
    {
        long magnitude = Math.Abs(variance);
        return magnitude <= AbsoluteTokens
            || magnitude <= (long)(Math.Abs(statementTotal) * RelativeShare);
    }
}

/// <summary>One provider-and-model line of a reconciliation.</summary>
public sealed record ReconciliationLine
{
    /// <summary>The provider.</summary>
    public required string Provider { get; init; }

    /// <summary>The upstream model.</summary>
    public required string UpstreamModel { get; init; }

    /// <summary>What the provider says it billed, or null when the statement omits this line.</summary>
    public ProviderStatementLine? Statement { get; init; }

    /// <summary>What Gatehouse recorded, or null when Gatehouse has no record.</summary>
    public UsageSummary? Recorded { get; init; }

    /// <summary>The verdict.</summary>
    public required ReconciliationVerdict Verdict { get; init; }

    /// <summary>
    /// Statement tokens less recorded tokens. Positive means the provider billed for more
    /// than Gatehouse saw.
    /// </summary>
    public long Variance { get; init; }

    /// <summary>
    /// The largest variance the known gaps on this line could account for, in tokens.
    /// </summary>
    /// <remarks>
    /// An estimate, and the report says so. It is derived from the number of requests whose
    /// tokens Gatehouse could not read, priced at the mean token count of the requests it
    /// could. That is the best available inference and it is wrong whenever unreadable
    /// requests differ systematically in size from readable ones — passthrough traffic, for
    /// instance, is often the long-context requests that had no OpenAI-compatible expression.
    /// It is published as a bound to compare against, never as a correction to apply.
    /// </remarks>
    public long ExplainableVariance { get; init; }

    /// <summary>The human-readable reasons behind <see cref="ExplainableVariance"/>.</summary>
    public IReadOnlyList<string> Explanations { get; init; } = [];
}

/// <summary>The result of reconciling a statement against the request log.</summary>
public sealed record ReconciliationReport
{
    /// <summary>The window reconciled.</summary>
    public required UsageWindow Window { get; init; }

    /// <summary>The tolerance applied.</summary>
    public required ReconciliationTolerance Tolerance { get; init; }

    /// <summary>Every line, worst verdict first.</summary>
    public IReadOnlyList<ReconciliationLine> Lines { get; init; } = [];

    /// <summary>Whether every line balanced or was accounted for.</summary>
    public bool IsClean => Lines.All(static line =>
        line.Verdict is ReconciliationVerdict.Balanced or ReconciliationVerdict.WithinKnownGaps);

    /// <summary>Lines that need a human.</summary>
    public IEnumerable<ReconciliationLine> Findings => Lines.Where(static line =>
        line.Verdict is not (ReconciliationVerdict.Balanced or ReconciliationVerdict.WithinKnownGaps));
}

/// <summary>
/// Compares a provider's usage statement against what Gatehouse recorded.
/// </summary>
/// <remarks>
/// <para>
/// The reason this exists as a first-class feature rather than a reporting afterthought: the
/// most-cited complaint about the incumbent gateways is that their numbers do not match the
/// provider bill, and nobody can say why. Producing a total is easy. Producing a total
/// alongside a defensible account of where it can and cannot be trusted is the actual
/// requirement, because the number gets handed to a finance team who will ask.
/// </para>
/// <para>
/// So this deliberately does not try to make the numbers agree. It quantifies the
/// disagreement, bounds how much of it Gatehouse's own known gaps could explain, and reports
/// the remainder as needing investigation. A reconciliation that always balances is a
/// reconciliation that is not doing anything.
/// </para>
/// </remarks>
public static class MeteringReconciliation
{
    /// <summary>Reconciles a statement against recorded usage.</summary>
    /// <param name="window">The period both sides claim to cover.</param>
    /// <param name="statement">The provider's usage lines.</param>
    /// <param name="recorded">Gatehouse's aggregated usage for the same period.</param>
    /// <param name="tolerance">Variance below which a difference is treated as noise.</param>
    public static ReconciliationReport Reconcile(
        UsageWindow window,
        IEnumerable<ProviderStatementLine> statement,
        IEnumerable<UsageSummary> recorded,
        ReconciliationTolerance? tolerance = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(recorded);

        ReconciliationTolerance applied = tolerance ?? ReconciliationTolerance.Default;

        Dictionary<(string Provider, string Model), ProviderStatementLine> statementLines = [];

        foreach (ProviderStatementLine line in statement)
        {
            (string, string) key = (Normalise(line.Provider), Normalise(line.UpstreamModel));

            // A provider export can list the same model on several rows — per day, per region,
            // per project. Summing rather than replacing is the difference between reconciling
            // a month and reconciling its last day.
            statementLines[key] = statementLines.TryGetValue(key, out ProviderStatementLine? existing)
                ? existing with
                {
                    PromptTokens = existing.PromptTokens + line.PromptTokens,
                    CompletionTokens = existing.CompletionTokens + line.CompletionTokens,
                }
                : line;
        }

        Dictionary<(string Provider, string Model), UsageSummary> recordedLines = [];

        foreach (UsageSummary summary in recorded)
        {
            recordedLines[(Normalise(summary.Provider), Normalise(summary.UpstreamModel))] = summary;
        }

        List<ReconciliationLine> lines = [];

        foreach ((string Provider, string Model) key in statementLines.Keys.Union(recordedLines.Keys))
        {
            statementLines.TryGetValue(key, out ProviderStatementLine? statementLine);
            recordedLines.TryGetValue(key, out UsageSummary? recordedLine);

            lines.Add(BuildLine(key, statementLine, recordedLine, applied));
        }

        return new ReconciliationReport
        {
            Window = window,
            Tolerance = applied,

            // Worst first, then largest variance. An operator reads the top of this and stops.
            Lines = [.. lines
                .OrderByDescending(static l => (int)l.Verdict)
                .ThenByDescending(static l => Math.Abs(l.Variance))
                .ThenBy(static l => l.Provider, StringComparer.Ordinal)
                .ThenBy(static l => l.UpstreamModel, StringComparer.Ordinal)],
        };
    }

    /// <summary>
    /// Builds one line from whichever sides of it exist.
    /// </summary>
    /// <remarks>
    /// Dispatched on which side is present rather than checked with null-forgiving operators.
    /// The caller only ever passes a key drawn from the union of the two dictionaries, so at
    /// least one side is always there — but that invariant lives in the caller, and asserting
    /// it here with <c>!</c> while also testing for null further down is a contradiction a
    /// reader has to resolve by hand. CodeQL flagged exactly that. Matching on the pair proves
    /// the non-nullness to the compiler in each branch instead of asserting it, and states the
    /// impossible case as an exception rather than leaving it to be a silent
    /// <see cref="NullReferenceException"/> if the caller ever changes.
    /// </remarks>
    private static ReconciliationLine BuildLine(
        (string Provider, string Model) key,
        ProviderStatementLine? statement,
        UsageSummary? recorded,
        ReconciliationTolerance tolerance) => (statement, recorded) switch
        {
            (null, null) => throw new ArgumentException(
                $"Neither side of '{key.Provider}/{key.Model}' has data, so there is nothing to "
                + "reconcile. Keys must come from the union of the statement and the recorded "
                + "usage.",
                nameof(statement)),

            (null, not null) => NotOnStatement(recorded),
            (not null, null) => NoLocalRecord(statement),
            _ => Compare(statement, recorded, tolerance),
        };

    /// <summary>Gatehouse recorded usage the statement says nothing about.</summary>
    private static ReconciliationLine NotOnStatement(UsageSummary recorded) => new()
    {
        Provider = recorded.Provider,
        UpstreamModel = recorded.UpstreamModel,
        Recorded = recorded,
        Verdict = ReconciliationVerdict.NotOnStatement,
        Variance = -recorded.TotalTokens,
        Explanations =
        [
            $"Gatehouse recorded {recorded.TotalTokens:N0} tokens that the statement "
            + "does not mention. Check that the statement covers the same period and "
            + "the same provider account.",
        ],
    };

    /// <summary>The provider billed for something Gatehouse has never seen.</summary>
    private static ReconciliationLine NoLocalRecord(ProviderStatementLine statement)
    {
        long statementTotal = statement.PromptTokens + statement.CompletionTokens;

        return new ReconciliationLine
        {
            Provider = statement.Provider,
            UpstreamModel = statement.UpstreamModel,
            Statement = statement,
            Verdict = ReconciliationVerdict.NoLocalRecord,
            Variance = statementTotal,
            Explanations =
            [
                $"The provider billed {statementTotal:N0} tokens for "
                + $"'{statement.UpstreamModel}' and Gatehouse has no record of it at all. A "
                + "credential for this provider is very likely in use outside the gateway.",
            ],
        };
    }

    /// <summary>Both sides exist, so there is a variance to judge.</summary>
    private static ReconciliationLine Compare(
        ProviderStatementLine statement,
        UsageSummary recorded,
        ReconciliationTolerance tolerance)
    {
        long statementTotal = statement.PromptTokens + statement.CompletionTokens;
        long variance = statementTotal - recorded.TotalTokens;

        (long explainable, List<string> explanations) = Explain(recorded, variance);

        ReconciliationVerdict verdict = tolerance.IsNoise(variance, statementTotal)
            ? ReconciliationVerdict.Balanced
            : Math.Abs(variance) <= explainable
                ? ReconciliationVerdict.WithinKnownGaps
                : ReconciliationVerdict.Unexplained;

        if (verdict == ReconciliationVerdict.Unexplained)
        {
            long residual = Math.Abs(variance) - explainable;
            explanations.Add(
                $"{residual:N0} tokens remain unaccounted for after the above. Check for "
                + "applications calling the provider directly, a second gateway sharing this "
                + "provider account, or a statement covering a different period.");
        }

        // Taken from the statement, which is the side a reader will be holding an invoice for.
        // Both sides normalise to the same key, so they agree up to case and padding.
        return new ReconciliationLine
        {
            Provider = statement.Provider,
            UpstreamModel = statement.UpstreamModel,
            Statement = statement,
            Recorded = recorded,
            Verdict = verdict,
            Variance = variance,
            ExplainableVariance = explainable,
            Explanations = explanations,
        };
    }

    /// <summary>
    /// Bounds how much of a variance Gatehouse's own known gaps could account for.
    /// </summary>
    /// <remarks>
    /// Only gaps in the direction of the variance are counted. Unreadable requests can only
    /// make the provider's figure <em>larger</em> than Gatehouse's, so they cannot explain a
    /// statement that comes in lower — and counting them anyway would let the report excuse a
    /// discrepancy that points at a genuine over-count.
    /// </remarks>
    private static (long Explainable, List<string> Explanations) Explain(UsageSummary recorded, long variance)
    {
        List<string> explanations = [];

        if (variance <= 0)
        {
            if (variance < 0)
            {
                explanations.Add(
                    $"Gatehouse recorded {Math.Abs(variance):N0} tokens more than the statement. "
                    + "Unreadable requests cannot explain this direction — it points at a "
                    + "double count, or at a statement narrower than the window.");
            }

            return (0, explanations);
        }

        if (recorded.UnaccountedRequests == 0)
        {
            return (0, explanations);
        }

        // Priced at the mean of what we could read. See ReconciliationLine.ExplainableVariance
        // for why this is a bound to compare against and not a correction to apply.
        long measurable = recorded.Requests - recorded.UnaccountedRequests;
        long meanTokens = measurable > 0 ? recorded.TotalTokens / measurable : 0;
        long explainable = 0;

        if (recorded.UnmeteredRequests > 0)
        {
            long bound = recorded.UnmeteredRequests * meanTokens;
            explainable += bound;
            explanations.Add(
                $"{recorded.UnmeteredRequests:N0} passthrough request(s) were forwarded verbatim "
                + $"and could not be metered; at the mean size of metered traffic that is about "
                + $"{bound:N0} tokens. Passthrough requests are often larger than average, so "
                + "treat this as a floor.");
        }

        if (recorded.RequestsWithoutUsage > 0)
        {
            long bound = recorded.RequestsWithoutUsage * meanTokens;
            explainable += bound;
            explanations.Add(
                $"{recorded.RequestsWithoutUsage:N0} request(s) returned no token counts at all; "
                + $"at the mean size of the rest that is about {bound:N0} tokens.");
        }

        if (recorded.FailedRequests > 0)
        {
            explanations.Add(
                $"{recorded.FailedRequests:N0} request(s) failed. Any that failed after the "
                + "provider had begun generating were still billed, and providers do not report "
                + "usage on an error response, so their cost is invisible to Gatehouse.");
        }

        if (meanTokens == 0 && recorded.UnaccountedRequests > 0)
        {
            explanations.Add(
                "No metered request on this line had readable token counts, so there is no basis "
                + "for estimating the size of the unreadable ones.");
        }

        return (explainable, explanations);
    }

    private static string Normalise(string value) => value.Trim().ToLowerInvariant();
}
