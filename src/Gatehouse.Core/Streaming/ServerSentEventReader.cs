using System.Runtime.CompilerServices;
using System.Text;

namespace Gatehouse.Streaming;

/// <summary>
/// Reads a server-sent event stream from an upstream provider.
/// </summary>
/// <remarks>
/// <para>
/// Written by hand rather than delegated to a client library because the correctness
/// properties that matter here are not the ones a general-purpose SSE client optimises for.
/// Specifically, this reader must never hold a completed event back waiting for more input:
/// the whole value of a streamed completion is that the first token reaches the user
/// quickly, and a reader that fills a buffer before yielding destroys that while leaving
/// every content assertion passing.
/// </para>
/// <para>
/// The parser follows the WHATWG event-stream rules that apply in practice: <c>field: value</c>
/// with one optional leading space stripped from the value, lines beginning with <c>:</c>
/// treated as comments (providers send these as keep-alives), multiple <c>data:</c> lines
/// joined with a newline, and an event dispatched on a blank line. Unknown fields are
/// ignored rather than treated as errors.
/// </para>
/// </remarks>
public static class ServerSentEventReader
{
    // SSE mandates UTF-8. Decoding with a permissive encoder would silently turn a
    // truncated multi-byte sequence into a replacement character, which for a completion
    // stream means corrupted text rather than a visible failure.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>
    /// Reads events from a stream until it ends or the token is cancelled.
    /// </summary>
    /// <param name="stream">The response body. Not disposed by this method.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>Each dispatched event, in order, as soon as it is complete.</returns>
    public static async IAsyncEnumerable<ServerSentEvent> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // leaveOpen: the caller owns the stream. It is usually an HttpResponseMessage body
        // whose lifetime is tied to the response, and disposing it here would break the
        // caller's own using block in a way that only shows up under load.
        using var reader = new StreamReader(stream, StrictUtf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        StringBuilder? data = null;
        string? eventType = null;
        string? id = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);

            if (line is null)
            {
                // End of stream. A well-behaved provider terminates with a blank line, but
                // an event left buffered here would otherwise be silently dropped, so
                // dispatch it rather than lose the final chunk.
                if (data is not null)
                {
                    yield return new ServerSentEvent(data.ToString(), eventType, id);
                }

                yield break;
            }

            if (line.Length == 0)
            {
                if (data is not null)
                {
                    yield return new ServerSentEvent(data.ToString(), eventType, id);
                    data = null;
                    eventType = null;
                    id = null;
                }

                continue;
            }

            if (line[0] == ':')
            {
                // A comment. Providers send these as keep-alives to stop intermediaries
                // from timing out an idle connection; there is nothing to dispatch.
                continue;
            }

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            string field;
            string value;

            if (colon < 0)
            {
                // A bare field name with no colon is legal and carries an empty value.
                field = line;
                value = string.Empty;
            }
            else
            {
                field = line[..colon];
                int valueStart = colon + 1;

                // Exactly one leading space is part of the framing, not the value.
                if (valueStart < line.Length && line[valueStart] == ' ')
                {
                    valueStart++;
                }

                value = line[valueStart..];
            }

            switch (field)
            {
                case "data":
                    data ??= new StringBuilder();
                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }

                    data.Append(value);
                    break;

                case "event":
                    eventType = value;
                    break;

                case "id":
                    id = value;
                    break;

                default:
                    // "retry" and anything a provider invents. Not our concern: Gatehouse
                    // does not reconnect a completion stream, because replaying a partially
                    // billed generation is worse than surfacing the failure.
                    break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }
}
