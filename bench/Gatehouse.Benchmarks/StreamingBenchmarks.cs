using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Gatehouse.Streaming;
using Gatehouse.Wire;

namespace Gatehouse.Benchmarks;

/// <summary>
/// The per-chunk cost of the streaming path.
/// </summary>
/// <remarks>
/// <para>
/// This is the benchmark that matters most, because it runs once per token rather than once
/// per request. A gateway that adds a microsecond per chunk adds a millisecond to a
/// thousand-token completion, on every concurrent stream at once.
/// </para>
/// <para>
/// It measures parse and serialise separately from the full round trip, so a regression can be
/// attributed rather than merely observed.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class StreamingBenchmarks
{
    private byte[] _sseStream = [];
    private string _chunkPayload = string.Empty;
    private ChatCompletionChunk _chunk = null!;

    /// <summary>Number of chunks in the simulated completion.</summary>
    [Params(1, 100, 1000)]
    public int ChunkCount { get; set; }

    /// <summary>Builds the fixtures once per parameter set.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _chunk = new ChatCompletionChunk
        {
            Id = "chatcmpl-benchmark",
            Created = 1_700_000_000,
            Model = "gpt-4o-mini",
            Choices =
            [
                new ChatChunkChoice
                {
                    Index = 0,
                    Delta = new ChatDelta { Content = " token" },
                },
            ],
        };

        _chunkPayload = JsonSerializer.Serialize(_chunk, GatehouseJsonContext.Default.ChatCompletionChunk);

        var builder = new StringBuilder();
        for (int i = 0; i < ChunkCount; i++)
        {
            builder.Append("data: ").Append(_chunkPayload).Append("\n\n");
        }

        builder.Append("data: [DONE]\n\n");
        _sseStream = Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>Parses a whole upstream stream into events.</summary>
    [Benchmark(Description = "Read SSE events from an upstream stream")]
    public async Task<int> ReadEvents()
    {
        using var stream = new MemoryStream(_sseStream);

        int count = 0;
        await foreach (ServerSentEvent sse in ServerSentEventReader.ReadAsync(stream))
        {
            count++;
        }

        return count;
    }

    /// <summary>Parses and deserialises a whole upstream stream, as the provider does.</summary>
    [Benchmark(Description = "Read and deserialise chunks")]
    public async Task<int> ReadAndDeserialiseChunks()
    {
        using var stream = new MemoryStream(_sseStream);

        int tokens = 0;
        await foreach (ServerSentEvent sse in ServerSentEventReader.ReadAsync(stream))
        {
            if (sse.IsDone)
            {
                break;
            }

            ChatCompletionChunk? chunk = JsonSerializer.Deserialize(
                sse.Data,
                GatehouseJsonContext.Default.ChatCompletionChunk);

            tokens += chunk?.Choices.Count ?? 0;
        }

        return tokens;
    }

    /// <summary>Serialises and frames chunks out to a client.</summary>
    [Benchmark(Description = "Write chunks as SSE to a client")]
    public async Task<long> WriteChunks()
    {
        using var output = new MemoryStream(_sseStream.Length);
        var writer = new ServerSentEventWriter(output);

        for (int i = 0; i < ChunkCount; i++)
        {
            await writer.WriteChunkAsync(_chunk);
        }

        await writer.WriteDoneAsync();
        return output.Length;
    }

    /// <summary>
    /// The full proxy shape: read an upstream event, deserialise it, re-serialise it, write it.
    /// </summary>
    /// <remarks>
    /// This is the number to quote as "Gatehouse overhead per chunk". Dividing it by
    /// <see cref="ChunkCount"/> gives the per-token cost the gateway adds on top of whatever
    /// the provider and the network already spent.
    /// </remarks>
    [Benchmark(Description = "Full relay: upstream event to client event")]
    public async Task<long> RelayEndToEnd()
    {
        using var input = new MemoryStream(_sseStream);
        using var output = new MemoryStream(_sseStream.Length);
        var writer = new ServerSentEventWriter(output);

        await foreach (ServerSentEvent sse in ServerSentEventReader.ReadAsync(input))
        {
            if (sse.IsDone)
            {
                await writer.WriteDoneAsync();
                break;
            }

            ChatCompletionChunk? chunk = JsonSerializer.Deserialize(
                sse.Data,
                GatehouseJsonContext.Default.ChatCompletionChunk);

            if (chunk is not null)
            {
                await writer.WriteChunkAsync(chunk);
            }
        }

        return output.Length;
    }
}
