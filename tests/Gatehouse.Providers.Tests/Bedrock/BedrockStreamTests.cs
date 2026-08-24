using System.Runtime.CompilerServices;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime.EventStreams;
using Gatehouse.Providers.Bedrock;
using Gatehouse.Routing;
using Gatehouse.Wire;
using BedrockUsage = Amazon.BedrockRuntime.Model.TokenUsage;

namespace Gatehouse.Providers.Tests.Bedrock;

/// <summary>Tests for turning Bedrock's event stream into OpenAI-compatible chunks.</summary>
public class BedrockStreamTests
{
    private static readonly ModelRoute Route = new()
    {
        Alias = "claude",
        Provider = "amazon-bedrock",
        UpstreamModel = "anthropic.claude-sonnet-4-20250514-v1:0",
    };

    [Test]
    public async Task Emits_one_chunk_per_content_delta_plus_a_final_chunk()
    {
        List<ChatCompletionChunk> chunks = await TranslateAsync(
            Delta("Hello"),
            Delta(" there"),
            new MessageStopEvent { StopReason = StopReason.End_turn },
            Metadata(new BedrockUsage { InputTokens = 10, OutputTokens = 2, TotalTokens = 12 }));

        // Two content chunks and one terminator, not three content chunks: the last carries the
        // finish reason and usage, which is where an OpenAI client looks for them.
        await Assert.That(chunks.Count).IsEqualTo(3);
        await Assert.That(Text(chunks)).IsEqualTo("Hello there");
    }

    [Test]
    public async Task Puts_the_finish_reason_and_usage_only_on_the_final_chunk()
    {
        List<ChatCompletionChunk> chunks = await TranslateAsync(
            Delta("hi"),
            new MessageStopEvent { StopReason = StopReason.Max_tokens },
            Metadata(new BedrockUsage { InputTokens = 10, OutputTokens = 2, TotalTokens = 12 }));

        await Assert.That(chunks[0].Choices[0].FinishReason).IsNull();
        await Assert.That(chunks[0].Usage).IsNull();

        ChatCompletionChunk last = chunks[^1];
        await Assert.That(last.Choices[0].FinishReason).IsEqualTo("length");
        await Assert.That(last.Usage!.PromptTokens).IsEqualTo(10);
        await Assert.That(last.Choices[0].Delta.Content).IsNull();
    }

    [Test]
    public async Task Ignores_the_structural_events_that_carry_no_content()
    {
        // A new event type from Bedrock must not break the stream, so anything unrecognised is
        // skipped rather than treated as an error.
        List<ChatCompletionChunk> chunks = await TranslateAsync(
            new MessageStartEvent { Role = ConversationRole.Assistant },
            new ContentBlockStartEvent { ContentBlockIndex = 0 },
            Delta("text"),
            new ContentBlockStopEvent { ContentBlockIndex = 0 },
            new MessageStopEvent { StopReason = StopReason.End_turn });

        await Assert.That(chunks.Count).IsEqualTo(2);
        await Assert.That(Text(chunks)).IsEqualTo("text");
    }

    [Test]
    public async Task Skips_an_empty_delta_rather_than_emitting_a_blank_chunk()
    {
        List<ChatCompletionChunk> chunks = await TranslateAsync(
            Delta("real"),
            Delta(string.Empty),
            new MessageStopEvent { StopReason = StopReason.End_turn });

        await Assert.That(chunks.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Still_terminates_the_stream_when_bedrock_sends_no_stop_event()
    {
        // A client waiting for a finish reason would hang forever otherwise. Defaulting to
        // "stop" is a guess, but a stream that never ends is not a better one.
        List<ChatCompletionChunk> chunks = await TranslateAsync(Delta("truncated"));

        await Assert.That(chunks[^1].Choices[0].FinishReason).IsEqualTo("stop");
    }

    [Test]
    public async Task Reports_a_usage_report_that_does_not_add_up()
    {
        // The consistency check is what would catch Bedrock changing its cache-token semantics.
        // Without the callback firing, a mis-metered request is indistinguishable from a
        // correct one until someone reconciles a month of them.
        List<string> discrepancies = [];

        await TranslateAsync(
            discrepancies.Add,
            Delta("hi"),
            new MessageStopEvent { StopReason = StopReason.End_turn },

            // The cache read alone exceeds the prompt count it claims to be a subset of.
            Metadata(new BedrockUsage
            {
                InputTokens = 10,
                OutputTokens = 2,
                CacheReadInputTokens = 900,
                TotalTokens = 12,
            }));

        await Assert.That(discrepancies).IsNotEmpty();
    }

    [Test]
    public async Task Stamps_one_id_and_timestamp_across_every_chunk()
    {
        // Clients correlate chunks by id. A per-chunk id looks like several interleaved
        // completions to anything that reassembles by grouping.
        List<ChatCompletionChunk> chunks = await TranslateAsync(
            Delta("a"),
            Delta("b"),
            new MessageStopEvent { StopReason = StopReason.End_turn });

        await Assert.That(chunks.Select(c => c.Id).Distinct().Count()).IsEqualTo(1);
        await Assert.That(chunks.Select(c => c.Created).Distinct().Count()).IsEqualTo(1);
        await Assert.That(chunks.All(c => c.Model == Route.UpstreamModel)).IsTrue();
    }

    [Test]
    public async Task Stops_when_the_client_disconnects()
    {
        using var cancellation = new CancellationTokenSource();
        List<ChatCompletionChunk> chunks = [];

        IAsyncEnumerable<ChatCompletionChunk> stream = BedrockProvider.TranslateEvents(
            Events([Delta("one"), Delta("two"), Delta("three")]),
            Route,
            "chatcmpl-test",
            0,
            _ => { },
            cancellation.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (ChatCompletionChunk chunk in stream.WithCancellation(cancellation.Token))
            {
                chunks.Add(chunk);
                await cancellation.CancelAsync();
            }
        });

        // The first chunk was delivered before the cancellation, and nothing after it was.
        await Assert.That(chunks.Count).IsEqualTo(1);
    }

    private static ContentBlockDeltaEvent Delta(string text) => new()
    {
        ContentBlockIndex = 0,
        Delta = new ContentBlockDelta { Text = text },
    };

    private static ConverseStreamMetadataEvent Metadata(BedrockUsage usage) => new() { Usage = usage };

    private static string Text(IEnumerable<ChatCompletionChunk> chunks) =>
        string.Concat(chunks.Select(c => c.Choices[0].Delta.Content));

    private static Task<List<ChatCompletionChunk>> TranslateAsync(params IEventStreamEvent[] events) =>
        TranslateAsync(_ => { }, events);

    private static async Task<List<ChatCompletionChunk>> TranslateAsync(
        Action<string> onDiscrepancy,
        params IEventStreamEvent[] events)
    {
        List<ChatCompletionChunk> chunks = [];

        await foreach (ChatCompletionChunk chunk in BedrockProvider.TranslateEvents(
                           Events(events), Route, "chatcmpl-test", 1_700_000_000, onDiscrepancy, default))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    /// <summary>
    /// Yields the events with a real await between them, the way a socket would.
    /// </summary>
    /// <remarks>
    /// The yield matters: a synchronous sequence lets the whole translation run in one
    /// continuation, which would hide a bug that only appears when the enumerator suspends.
    /// </remarks>
    private static async IAsyncEnumerable<IEventStreamEvent> Events(
        IEventStreamEvent[] events,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (IEventStreamEvent evt in events)
        {
            // Observed here because this stands in for a socket read, which is where
            // cancellation is actually noticed. A fake that ignored the token would let a
            // provider that never forwards it pass this test.
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return evt;
        }
    }
}
