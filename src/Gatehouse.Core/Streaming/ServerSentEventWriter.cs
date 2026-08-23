using System.Buffers;
using System.Text;
using System.Text.Json;
using Gatehouse.Wire;

namespace Gatehouse.Streaming;

/// <summary>
/// Writes a server-sent event stream to a client.
/// </summary>
/// <remarks>
/// <para>
/// Every write is followed by an explicit flush. This is the single most important line in
/// the streaming path and the easiest one to lose in a refactor: without it the response
/// sits in Kestrel's output buffer until the buffer fills or the request completes, and the
/// stream arrives in bursts. Every functional test still passes, because the bytes are all
/// correct and all present — they are just late. The Phase 0 gate asserts on inter-chunk
/// timing precisely so that a regression here fails CI rather than a user's patience.
/// </para>
/// <para>
/// Serialization goes through <see cref="GatehouseJsonContext"/> so the writer stays
/// NativeAOT-safe. A reflection-based overload here would compile, pass every test on the
/// JIT, and throw on the published single-file binary.
/// </para>
/// </remarks>
public sealed class ServerSentEventWriter
{
    private static readonly byte[] DataPrefix = "data: "u8.ToArray();
    private static readonly byte[] EventTerminator = "\n\n"u8.ToArray();
    private static readonly byte[] DoneEvent = "data: [DONE]\n\n"u8.ToArray();

    private readonly Stream _output;

    /// <summary>Creates a writer over a response body stream.</summary>
    /// <param name="output">The response body. Not disposed by this writer.</param>
    public ServerSentEventWriter(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    /// <summary>
    /// Writes one completion chunk as an event and flushes it to the client.
    /// </summary>
    public async Task WriteChunkAsync(ChatCompletionChunk chunk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(chunk, GatehouseJsonContext.Default.ChatCompletionChunk);

        await _output.WriteAsync(DataPrefix, cancellationToken);
        await _output.WriteAsync(payload, cancellationToken);
        await _output.WriteAsync(EventTerminator, cancellationToken);
        await _output.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Writes an error as an event.
    /// </summary>
    /// <remarks>
    /// Once the response has started streaming, the status code is already on the wire and
    /// cannot be changed to a 5xx. Reporting the failure in-band is the only way the client
    /// learns the difference between "the model stopped" and "the upstream died mid-answer",
    /// and a gateway that just closes the connection leaves the caller unable to tell.
    /// </remarks>
    public async Task WriteErrorAsync(ErrorResponse error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(error);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(error, GatehouseJsonContext.Default.ErrorResponse);

        await _output.WriteAsync(DataPrefix, cancellationToken);
        await _output.WriteAsync(payload, cancellationToken);
        await _output.WriteAsync(EventTerminator, cancellationToken);
        await _output.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Writes the terminating <c>[DONE]</c> sentinel and flushes.
    /// </summary>
    /// <remarks>
    /// OpenAI client libraries treat the sentinel, not connection close, as the signal that
    /// the stream finished cleanly. Omitting it makes a successful completion look like a
    /// dropped connection to the caller.
    /// </remarks>
    public async Task WriteDoneAsync(CancellationToken cancellationToken = default)
    {
        await _output.WriteAsync(DoneEvent, cancellationToken);
        await _output.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Writes a comment line, used as a keep-alive.
    /// </summary>
    /// <remarks>
    /// Intermediaries — load balancers, corporate proxies — commonly close a connection
    /// that has been idle for 60 seconds. A reasoning model can think for longer than that
    /// before emitting its first token, so without periodic keep-alives the requests most
    /// worth waiting for are exactly the ones that get cut off.
    /// </remarks>
    public async Task WriteKeepAliveAsync(CancellationToken cancellationToken = default)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64);
        try
        {
            int written = Encoding.UTF8.GetBytes(": keep-alive\n\n", buffer);
            await _output.WriteAsync(buffer.AsMemory(0, written), cancellationToken);
            await _output.FlushAsync(cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
