using System.Globalization;

namespace Gatehouse.Metering;

/// <summary>
/// Reads a provider usage statement from CSV.
/// </summary>
/// <remarks>
/// <para>
/// CSV because that is what every provider's usage export actually is, and because an operator
/// reconciling a bill needs to be able to open the file, see that it is wrong, and fix it in a
/// spreadsheet. A bespoke format would be tidier and would guarantee that the first step of
/// every reconciliation is writing a conversion script.
/// </para>
/// <para>
/// Deliberately not a general CSV parser. It handles a header row, blank lines, comments, and
/// quoted fields containing commas — and it rejects anything else rather than guessing. A
/// lenient parser here turns a malformed export into a confident wrong answer about money.
/// </para>
/// </remarks>
public static class ProviderStatementReader
{
    /// <summary>The header this reader expects, in any column order.</summary>
    public const string ExpectedColumns = "provider, model, prompt_tokens, completion_tokens";

    /// <summary>
    /// Parses statement lines from CSV text.
    /// </summary>
    /// <param name="csv">The file contents.</param>
    /// <param name="errors">Every problem found, empty when parsing succeeded.</param>
    /// <returns>The parsed lines, which is empty when <paramref name="errors"/> is not.</returns>
    /// <remarks>
    /// Reports every bad row rather than throwing on the first. An operator fixing an export
    /// should need one pass, not one per mistake.
    /// </remarks>
    public static IReadOnlyList<ProviderStatementLine> Parse(string csv, out IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(csv);

        List<string> problems = [];
        List<ProviderStatementLine> lines = [];

        string[] rows = csv.Split('\n');
        Dictionary<string, int>? columns = null;
        int lineNumber = 0;

        foreach (string raw in rows)
        {
            lineNumber++;
            string row = raw.Trim('\r', ' ', '\t');

            // '#' comments are not CSV, and are supported anyway: it is the only way an
            // operator can record which invoice a file came from, next to the numbers.
            if (row.Length == 0 || row.StartsWith('#'))
            {
                continue;
            }

            List<string> fields = SplitRow(row);

            if (columns is null)
            {
                columns = ReadHeader(fields, lineNumber, problems);

                if (columns is null)
                {
                    // Stop at an unreadable header rather than carrying on with none. Without
                    // this the next data row is treated as a header too, and every row in the
                    // file is reported as a second bad header — burying the one real problem
                    // under one error per line.
                    errors = problems;
                    return [];
                }

                continue;
            }

            if (fields.Count < columns.Count)
            {
                problems.Add($"Line {lineNumber}: expected {columns.Count} fields, found {fields.Count}.");
                continue;
            }

            if (TryReadLine(fields, columns, lineNumber, problems) is { } line)
            {
                lines.Add(line);
            }
        }

        if (columns is null && problems.Count == 0)
        {
            problems.Add($"The statement is empty. Expected a header row of: {ExpectedColumns}");
        }

        errors = problems;
        return problems.Count == 0 ? lines : [];
    }

    private static Dictionary<string, int>? ReadHeader(
        List<string> fields,
        int lineNumber,
        List<string> problems)
    {
        Dictionary<string, int> columns = new(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < fields.Count; i++)
        {
            // 'model' and 'upstream_model' both accepted: providers disagree about which they
            // call it, and rejecting a file over a synonym is a pointless obstacle.
            string name = fields[i].Trim().ToLowerInvariant().Replace(' ', '_');
            columns[name == "upstream_model" ? "model" : name] = i;
        }

        foreach (string required in (string[])["provider", "model", "prompt_tokens", "completion_tokens"])
        {
            if (!columns.ContainsKey(required))
            {
                problems.Add(
                    $"Line {lineNumber}: the header is missing a '{required}' column. "
                    + $"Expected: {ExpectedColumns}");
            }
        }

        return problems.Count == 0 ? columns : null;
    }

    private static ProviderStatementLine? TryReadLine(
        List<string> fields,
        Dictionary<string, int> columns,
        int lineNumber,
        List<string> problems)
    {
        string provider = fields[columns["provider"]].Trim();
        string model = fields[columns["model"]].Trim();

        if (provider.Length == 0 || model.Length == 0)
        {
            problems.Add($"Line {lineNumber}: provider and model are both required.");
            return null;
        }

        if (!TryReadTokens(fields[columns["prompt_tokens"]], lineNumber, "prompt_tokens", problems, out long prompt)
            || !TryReadTokens(fields[columns["completion_tokens"]], lineNumber, "completion_tokens", problems, out long completion))
        {
            return null;
        }

        return new ProviderStatementLine(provider, model, prompt, completion);
    }

    private static bool TryReadTokens(
        string field,
        int lineNumber,
        string column,
        List<string> problems,
        out long value)
    {
        // Thousands separators are stripped because exports contain them; a decimal point is
        // not, because a fractional token count means the column is not what we think it is.
        string cleaned = field.Trim().Replace(",", string.Empty, StringComparison.Ordinal);

        if (cleaned.Length == 0)
        {
            value = 0;
            return true;
        }

        if (!long.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            problems.Add($"Line {lineNumber}: '{field.Trim()}' is not a whole number of {column}.");
            return false;
        }

        if (value < 0)
        {
            problems.Add($"Line {lineNumber}: {column} is negative ({value}).");
            return false;
        }

        return true;
    }

    /// <summary>Splits one CSV row, honouring double-quoted fields.</summary>
    private static List<string> SplitRow(string row)
    {
        List<string> fields = [];
        var field = new System.Text.StringBuilder();
        bool quoted = false;

        for (int i = 0; i < row.Length; i++)
        {
            char c = row[i];

            if (quoted)
            {
                if (c != '"')
                {
                    field.Append(c);
                }
                else if (i + 1 < row.Length && row[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        fields.Add(field.ToString());
        return fields;
    }
}
