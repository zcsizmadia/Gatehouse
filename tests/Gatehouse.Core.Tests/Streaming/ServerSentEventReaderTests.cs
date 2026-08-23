using System.Text;
using Gatehouse.Streaming;

namespace Gatehouse.Tests.Streaming;

/// <summary>
/// Tests for the upstream server-sent event parser.
/// </summary>
public class ServerSentEventReaderTests
{
    [Test]
    public async Task Reads_a_single_event()
    {
        List<ServerSentEvent> events = await ReadAllAsync("data: hello\n\n");

        await Assert.That(events).Count().IsEqualTo(1);
        await Assert.That(events[0].Data).IsEqualTo("hello");
    }

    [Test]
    public async Task Reads_multiple_events_in_order()
    {
        List<ServerSentEvent> events = await ReadAllAsync("data: one\n\ndata: two\n\ndata: three\n\n");

        await Assert.That(events.Select(e => e.Data)).IsEquivalentTo(new[] { "one", "two", "three" });
    }

    [Test]
    public async Task Strips_exactly_one_leading_space_from_the_value()
    {
        // "data:  two spaces" must keep the second space: only the first is framing.
        List<ServerSentEvent> events = await ReadAllAsync("data:  padded\n\n");

        await Assert.That(events[0].Data).IsEqualTo(" padded");
    }

    [Test]
    public async Task Handles_a_field_with_no_space_after_the_colon()
    {
        List<ServerSentEvent> events = await ReadAllAsync("data:compact\n\n");

        await Assert.That(events[0].Data).IsEqualTo("compact");
    }

    [Test]
    public async Task Joins_multiple_data_lines_with_a_newline()
    {
        List<ServerSentEvent> events = await ReadAllAsync("data: first\ndata: second\n\n");

        await Assert.That(events).Count().IsEqualTo(1);
        await Assert.That(events[0].Data).IsEqualTo("first\nsecond");
    }

    [Test]
    public async Task Ignores_comment_lines()
    {
        // Providers send comments as keep-alives. Treating one as an event would inject an
        // empty chunk into the caller's stream.
        List<ServerSentEvent> events = await ReadAllAsync(": keep-alive\n\ndata: real\n\n");

        await Assert.That(events).Count().IsEqualTo(1);
        await Assert.That(events[0].Data).IsEqualTo("real");
    }

    [Test]
    public async Task Captures_the_event_and_id_fields()
    {
        List<ServerSentEvent> events = await ReadAllAsync("event: delta\nid: 42\ndata: payload\n\n");

        await Assert.That(events[0].EventType).IsEqualTo("delta");
        await Assert.That(events[0].Id).IsEqualTo("42");
        await Assert.That(events[0].Data).IsEqualTo("payload");
    }

    [Test]
    public async Task Does_not_carry_metadata_across_events()
    {
        List<ServerSentEvent> events = await ReadAllAsync("event: first\ndata: one\n\ndata: two\n\n");

        await Assert.That(events[1].EventType).IsNull();
    }

    [Test]
    public async Task Ignores_unknown_fields()
    {
        // "retry" is legal SSE that Gatehouse does not act on; an invented field must not
        // break the stream either.
        List<ServerSentEvent> events = await ReadAllAsync("retry: 3000\nx-vendor: 1\ndata: payload\n\n");

        await Assert.That(events).Count().IsEqualTo(1);
        await Assert.That(events[0].Data).IsEqualTo("payload");
    }

    [Test]
    public async Task Dispatches_a_trailing_event_that_was_not_terminated_by_a_blank_line()
    {
        // A provider that closes the connection without the final blank line would otherwise
        // lose its last chunk — which is the one carrying usage.
        List<ServerSentEvent> events = await ReadAllAsync("data: complete\n\ndata: truncated");

        await Assert.That(events).Count().IsEqualTo(2);
        await Assert.That(events[1].Data).IsEqualTo("truncated");
    }

    [Test]
    public async Task Recognises_the_done_sentinel()
    {
        List<ServerSentEvent> events = await ReadAllAsync("data: [DONE]\n\n");

        await Assert.That(events[0].IsDone).IsTrue();
    }

    [Test]
    public async Task Does_not_treat_a_json_payload_as_done()
    {
        List<ServerSentEvent> events = await ReadAllAsync("""data: {"choices":[]}""" + "\n\n");

        await Assert.That(events[0].IsDone).IsFalse();
    }

    [Test]
    public async Task Produces_no_events_for_an_empty_stream()
    {
        List<ServerSentEvent> events = await ReadAllAsync(string.Empty);

        await Assert.That(events).IsEmpty();
    }

    [Test]
    public async Task Preserves_utf8_content()
    {
        List<ServerSentEvent> events = await ReadAllAsync("data: árvíztűrő tükörfúrógép — 日本語\n\n");

        await Assert.That(events[0].Data).IsEqualTo("árvíztűrő tükörfúrógép — 日本語");
    }

    [Test]
    public async Task Yields_each_event_before_the_next_one_arrives()
    {
        // The property that actually matters. A reader that buffers would still pass every
        // test above, so this one drives the stream from a source that refuses to produce
        // the second event until the first has been consumed.
        var source = new HandshakeStream(["data: first\n\n", "data: second\n\n"]);

        List<string> observed = [];
        await foreach (ServerSentEvent sse in ServerSentEventReader.ReadAsync(source))
        {
            observed.Add(sse.Data);
        }

        await Assert.That(observed).IsEquivalentTo(new[] { "first", "second" });
        await Assert.That(source.DeliveredWithoutBuffering).IsTrue();
    }

    [Test]
    public async Task Stops_when_cancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.That(async () =>
        {
            await foreach (ServerSentEvent _ in ServerSentEventReader.ReadAsync(
                new MemoryStream("data: x\n\n"u8.ToArray()), cts.Token))
            {
                // The enumeration is expected to throw rather than yield.
            }
        }).Throws<OperationCanceledException>();
    }

    private static async Task<List<ServerSentEvent>> ReadAllAsync(string payload)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        List<ServerSentEvent> events = [];
        await foreach (ServerSentEvent sse in ServerSentEventReader.ReadAsync(stream))
        {
            events.Add(sse);
        }

        return events;
    }

    /// <summary>
    /// A stream that hands over one chunk per read and records whether the consumer came
    /// back for more before the producer had offered it.
    /// </summary>
    private sealed class HandshakeStream : Stream
    {
        private readonly Queue<byte[]> _chunks;
        private int _readsIssued;

        public HandshakeStream(IEnumerable<string> chunks) =>
            _chunks = new Queue<byte[]>(chunks.Select(Encoding.UTF8.GetBytes));

        /// <summary>
        /// True when the reader asked for each chunk separately rather than draining the
        /// stream up front.
        /// </summary>
        public bool DeliveredWithoutBuffering => _readsIssued >= 2;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_chunks.Count == 0)
            {
                return 0;
            }

            _readsIssued++;
            byte[] chunk = _chunks.Dequeue();
            int length = Math.Min(count, chunk.Length);
            chunk.AsSpan(0, length).CopyTo(buffer.AsSpan(offset));
            return length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
