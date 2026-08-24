using Gatehouse.Metering;

namespace Gatehouse.Tests.Metering;

/// <summary>Tests for reading a provider usage export.</summary>
public class ProviderStatementReaderTests
{
    [Test]
    public async Task Reads_a_well_formed_statement()
    {
        IReadOnlyList<ProviderStatementLine> lines = Parse(
            """
            provider,model,prompt_tokens,completion_tokens
            openai,gpt-4o-mini,1000000,200000
            anthropic,claude-sonnet-5,500000,90000
            """,
            out IReadOnlyList<string> errors);

        await Assert.That(errors).IsEmpty();
        await Assert.That(lines.Count).IsEqualTo(2);
        await Assert.That(lines[0].PromptTokens).IsEqualTo(1_000_000);
        await Assert.That(lines[1].UpstreamModel).IsEqualTo("claude-sonnet-5");
    }

    [Test]
    public async Task Accepts_the_columns_in_any_order()
    {
        // Providers order their exports however they like, and reordering a file by hand before
        // it can be read is the sort of friction that stops a monthly task happening.
        IReadOnlyList<ProviderStatementLine> lines = Parse(
            """
            completion_tokens,provider,prompt_tokens,model
            200000,openai,1000000,gpt-4o-mini
            """,
            out _);

        await Assert.That(lines[0].Provider).IsEqualTo("openai");
        await Assert.That(lines[0].PromptTokens).IsEqualTo(1_000_000);
        await Assert.That(lines[0].CompletionTokens).IsEqualTo(200_000);
    }

    [Test]
    public async Task Accepts_upstream_model_as_a_synonym_for_model()
    {
        IReadOnlyList<ProviderStatementLine> lines = Parse(
            """
            provider,upstream_model,prompt_tokens,completion_tokens
            azure,my-gpt-4o-deployment,10,20
            """,
            out IReadOnlyList<string> errors);

        await Assert.That(errors).IsEmpty();
        await Assert.That(lines[0].UpstreamModel).IsEqualTo("my-gpt-4o-deployment");
    }

    [Test]
    public async Task Strips_thousands_separators_from_token_counts()
    {
        // Exports contain them, and a parser that rejects "1,000,000" makes the operator
        // reformat a spreadsheet before they can reconcile anything.
        IReadOnlyList<ProviderStatementLine> lines = Parse(
            """
            provider,model,prompt_tokens,completion_tokens
            openai,gpt-4o-mini,"1,000,000","200,000"
            """,
            out IReadOnlyList<string> errors);

        await Assert.That(errors).IsEmpty();
        await Assert.That(lines[0].PromptTokens).IsEqualTo(1_000_000);
        await Assert.That(lines[0].CompletionTokens).IsEqualTo(200_000);
    }

    [Test]
    public async Task Skips_blank_lines_and_comments()
    {
        IReadOnlyList<ProviderStatementLine> lines = Parse(
            """
            # OpenAI usage export, invoice INV-2026-08
            provider,model,prompt_tokens,completion_tokens

            openai,gpt-4o-mini,10,20
            # a note in the middle
            openai,gpt-4o,30,40
            """,
            out IReadOnlyList<string> errors);

        await Assert.That(errors).IsEmpty();
        await Assert.That(lines.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Rejects_a_fractional_token_count_rather_than_truncating_it()
    {
        // A decimal in this column means it is not the column we think it is — a cost, most
        // likely. Silently truncating to 1,000 would produce a confident wrong answer about
        // money, which is the one outcome this parser exists to prevent.
        Parse(
            """
            provider,model,prompt_tokens,completion_tokens
            openai,gpt-4o-mini,1000.75,200
            """,
            out IReadOnlyList<string> errors);

        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0]).Contains("not a whole number");
    }

    [Test]
    public async Task Reports_every_bad_row_rather_than_stopping_at_the_first()
    {
        Parse(
            """
            provider,model,prompt_tokens,completion_tokens
            openai,gpt-4o-mini,not-a-number,200
            ,gpt-4o,10,20
            openai,gpt-4o,-5,20
            """,
            out IReadOnlyList<string> errors);

        // One pass to fix the file, not one per mistake.
        await Assert.That(errors.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Returns_no_lines_at_all_when_the_file_had_errors()
    {
        // Partial success is the dangerous outcome: a reconciliation run against three of four
        // rows reports a shortfall that looks exactly like traffic bypassing the gateway.
        IReadOnlyList<ProviderStatementLine> lines = Parse(
            """
            provider,model,prompt_tokens,completion_tokens
            openai,gpt-4o-mini,1000,200
            openai,gpt-4o,broken,20
            """,
            out IReadOnlyList<string> errors);

        await Assert.That(errors).IsNotEmpty();
        await Assert.That(lines).IsEmpty();
    }

    [Test]
    public async Task Names_the_missing_column_when_the_header_is_wrong()
    {
        Parse(
            """
            provider,model,input_tokens,output_tokens
            openai,gpt-4o-mini,1000,200
            """,
            out IReadOnlyList<string> errors);

        await Assert.That(errors.Count).IsEqualTo(2);
        await Assert.That(string.Join(" ", errors)).Contains("prompt_tokens");
        await Assert.That(string.Join(" ", errors)).Contains("completion_tokens");
    }

    [Test]
    public async Task Rejects_an_empty_file_with_an_explanation()
    {
        Parse(string.Empty, out IReadOnlyList<string> errors);

        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0]).Contains("header row");
    }

    [Test]
    public async Task Handles_a_quoted_model_name_containing_a_comma()
    {
        IReadOnlyList<ProviderStatementLine> lines = Parse(
            """
            provider,model,prompt_tokens,completion_tokens
            openai,"weird,name",10,20
            """,
            out IReadOnlyList<string> errors);

        await Assert.That(errors).IsEmpty();
        await Assert.That(lines[0].UpstreamModel).IsEqualTo("weird,name");
    }

    [Test]
    public async Task Treats_an_empty_token_field_as_zero()
    {
        // Exports leave a cell blank for "none of these", and a model that produced no
        // completion tokens is ordinary rather than an error.
        IReadOnlyList<ProviderStatementLine> lines = Parse(
            """
            provider,model,prompt_tokens,completion_tokens
            openai,text-embedding-3-small,50000,
            """,
            out IReadOnlyList<string> errors);

        await Assert.That(errors).IsEmpty();
        await Assert.That(lines[0].CompletionTokens).IsEqualTo(0);
    }

    private static IReadOnlyList<ProviderStatementLine> Parse(string csv, out IReadOnlyList<string> errors) =>
        ProviderStatementReader.Parse(csv, out errors);
}
