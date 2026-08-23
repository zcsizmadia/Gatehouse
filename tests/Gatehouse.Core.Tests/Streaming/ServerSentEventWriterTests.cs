using System.Text;
using Gatehouse.Streaming;
using Gatehouse.Wire;

namespace Gatehouse.Tests.Streaming;

/// <summary>
/// Tests for the client-facing server-sent event writer.
/// </summary>
public class ServerSentEventWriterTests
{
    [Test]
    public async Task Writes_a_chunk_in_sse_framing()
    {
        var output = new RecordingStream();
        var writer = new ServerSentEventWriter(output);

        await writer.WriteChunkAsync(Chunk("Hello"));

        string written = output.Text;
        await Assert.That(written).StartsWith("data: ");
        await Assert.That(written).EndsWith("\n\n");
        await Assert.That(written).Contains("\"content\":\"Hello\"");
    }

    [Test]
    public async Task Flushes_after_every_chunk()
    {
        // The single most important assertion in the streaming path. Without the flush the
        // bytes sit in Kestrel's output buffer and the stream arrives in bursts — every
        // content assertion still passes, and the user waits.
        var output = new RecordingStream();
        var writer = new ServerSentEventWriter(output);

        await writer.WriteChunkAsync(Chunk("one"));
        await writer.WriteChunkAsync(Chunk("two"));
        await writer.WriteChunkAsync(Chunk("three"));

        await Assert.That(output.FlushCount).IsEqualTo(3);
    }

    [Test]
    public async Task Flushes_after_the_last_write_of_each_event()
    {
        // Stronger than counting flushes: no bytes may be left unflushed when the write
        // returns, or the final chunk of a completion can be stranded.
        var output = new RecordingStream();
        var writer = new ServerSentEventWriter(output);

        await writer.WriteChunkAsync(Chunk("payload"));

        await Assert.That(output.UnflushedByteCount).IsEqualTo(0);
    }

    [Test]
    public async Task Writes_the_done_sentinel()
    {
        // Client libraries treat the sentinel, not connection close, as a clean finish.
        var output = new RecordingStream();
        var writer = new ServerSentEventWriter(output);

        await writer.WriteDoneAsync();

        await Assert.That(output.Text).IsEqualTo("data: [DONE]\n\n");
        await Assert.That(output.FlushCount).IsEqualTo(1);
    }

    [Test]
    public async Task Writes_an_error_as_an_in_band_event()
    {
        var output = new RecordingStream();
        var writer = new ServerSentEventWriter(output);

        await writer.WriteErrorAsync(
            ErrorResponse.Create("upstream exploded", ErrorTypes.Upstream, "openai"));

        await Assert.That(output.Text).Contains("upstream exploded");
        await Assert.That(output.Text).Contains(ErrorTypes.Upstream);
        await Assert.That(output.FlushCount).IsEqualTo(1);
    }

    [Test]
    public async Task Writes_a_keep_alive_as_a_comment()
    {
        // Must be a comment, not a data event: an SSE comment is discarded by the client,
        // whereas a data frame would be handed to the application as a phantom chunk.
        var output = new RecordingStream();
        var writer = new ServerSentEventWriter(output);

        await writer.WriteKeepAliveAsync();

        await Assert.That(output.Text).StartsWith(":");
        await Assert.That(output.Text).EndsWith("\n\n");
    }

    [Test]
    public async Task Round_trips_through_the_reader()
    {
        // The writer and the reader are the two halves of the same contract, so the strongest
        // test is that one undoes the other.
        var output = new RecordingStream();
        var writer = new ServerSentEventWriter(output);

        await writer.WriteChunkAsync(Chunk("first"));
        await writer.WriteChunkAsync(Chunk("second"));
        await writer.WriteDoneAsync();

        using var input = new MemoryStream(output.ToArray());
        List<ServerSentEvent> events = [];
        await foreach (ServerSentEvent sse in ServerSentEventReader.ReadAsync(input))
        {
            events.Add(sse);
        }

        await Assert.That(events).Count().IsEqualTo(3);
        await Assert.That(events[2].IsDone).IsTrue();

        ChatCompletionChunk? parsed = System.Text.Json.JsonSerializer.Deserialize(
            events[0].Data,
            GatehouseJsonContext.Default.ChatCompletionChunk);

        await Assert.That(parsed!.Choices[0].Delta.Content).IsEqualTo("first");
    }

    [Test]
    public async Task Emits_no_raw_newlines_inside_a_chunk_payload()
    {
        // A literal newline in the payload would terminate the SSE data line early and split
        // one event into two malformed ones. JSON escaping is what prevents it.
        var output = new RecordingStream();
        var writer = new ServerSentEventWriter(output);

        await writer.WriteChunkAsync(Chunk("line one\nline two"));

        string body = output.Text["data: ".Length..^2];
        await Assert.That(body).DoesNotContain("\n");
    }

    private static ChatCompletionChunk Chunk(string content) => new()
    {
        Id = "chatcmpl-test",
        Created = 1_700_000_000,
        Model = "gpt-4o-mini",
        Choices =
        [
            new ChatChunkChoice { Index = 0, Delta = new ChatDelta { Content = content } },
        ],
    };

    /// <summary>
    /// A stream that records how much was written and when it was flushed.
    /// </summary>
    private sealed class RecordingStream : Stream
    {
        private readonly MemoryStream _buffer = new();
        private long _flushedThrough;

        public int FlushCount { get; private set; }

        public long UnflushedByteCount => _buffer.Length - _flushedThrough;

        public string Text => Encoding.UTF8.GetString(_buffer.ToArray());

        public byte[] ToArray() => _buffer.ToArray();

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            _flushedThrough = _buffer.Length;
            return Task.CompletedTask;
        }

        public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _buffer.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count) => _buffer.Write(buffer, offset, count);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;

        public override long Position
        {
            get => _buffer.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
