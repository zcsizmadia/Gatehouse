using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gatehouse.Server.Tests.Fakes;

/// <summary>
/// A stub Anthropic Messages API, emitting the real event sequence over a real Kestrel.
/// </summary>
/// <remarks>
/// <para>
/// The point of this fake is the token accounting. It reports usage exactly the way Anthropic
/// documents: <c>message_start</c> carries the input and cache figures, and every
/// <c>message_delta</c> carries a <strong>cumulative</strong> output count. A provider that
/// sums those deltas — the obvious implementation — produces a visibly wrong total against
/// this fake, which is the entire reason it exists.
/// </para>
/// <para>
/// It also reports <c>input_tokens</c> the way Anthropic does: excluding the cache figures, so
/// the billable prompt is the sum of all three. A provider that maps <c>input_tokens</c>
/// straight through under-reports the prompt here by an obvious margin.
/// </para>
/// </remarks>
internal sealed class FakeAnthropicUpstream : IAsyncDisposable
{
    /// <summary>
    /// The JSON null Anthropic sends for <c>stop_reason</c> until a message actually stops.
    /// </summary>
    /// <remarks>
    /// A named constant rather than a <c>(string?)null</c> cast at each use. The cast was only
    /// there to give an anonymous-type member a type, which is a mechanical reason that told a
    /// reader nothing; this says what the null means.
    /// </remarks>
    private const string? NotStoppedYet = null;

    private readonly WebApplication _app;

    private FakeAnthropicUpstream(WebApplication app, string baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
    }

    /// <summary>The address Gatehouse should be pointed at.</summary>
    public string BaseAddress { get; }

    /// <summary>Text fragments emitted as <c>text_delta</c> events.</summary>
    public IReadOnlyList<string> Chunks { get; set; } = ["The ", "gate ", "holds."];

    // --- usage, reported the way Anthropic reports it -------------------------------------

    /// <summary>Tokens after the last cache breakpoint. Excludes both cache figures.</summary>
    public int InputTokens { get; set; } = 100;

    /// <summary>Tokens served from the cache. Billed at a tenth of the input rate.</summary>
    public int CacheReadTokens { get; set; } = 800;

    /// <summary>Tokens written to the cache. Billed at a premium.</summary>
    public int CacheCreationTokens { get; set; } = 100;

    /// <summary>The final cumulative output count reported on the last message_delta.</summary>
    public int FinalOutputTokens { get; set; } = 30;

    /// <summary>Emit a ping event, which the provider must tolerate and not forward.</summary>
    public bool EmitPing { get; set; } = true;

    /// <summary>Emit an unknown event type, which the provider must ignore.</summary>
    public bool EmitUnknownEvent { get; set; } = true;

    /// <summary>The stop reason on the final message_delta.</summary>
    public string StopReason { get; set; } = "end_turn";

    /// <summary>The request body the upstream received, for asserting on translation.</summary>
    public string? LastRequestBody { get; private set; }

    /// <summary>The <c>x-api-key</c> header received.</summary>
    public string? LastApiKey { get; private set; }

    /// <summary>The <c>anthropic-version</c> header received.</summary>
    public string? LastVersion { get; private set; }

    /// <summary>Starts the fake on an ephemeral loopback port.</summary>
    public static async Task<FakeAnthropicUpstream> StartAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        WebApplication app = builder.Build();
        FakeAnthropicUpstream? instance = null;

        app.MapPost("/v1/messages", async (HttpContext context) => await instance!.HandleAsync(context));

        await app.StartAsync();

        instance = new FakeAnthropicUpstream(app, app.Urls.First());
        return instance;
    }

    private async Task HandleAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        LastRequestBody = await reader.ReadToEndAsync();
        LastApiKey = context.Request.Headers["x-api-key"].ToString();
        LastVersion = context.Request.Headers["anthropic-version"].ToString();

        bool stream = LastRequestBody.Contains("\"stream\":true", StringComparison.Ordinal);

        if (stream)
        {
            await StreamAsync(context);
        }
        else
        {
            await CompleteAsync(context);
        }
    }

    private async Task StreamAsync(HttpContext context)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        // message_start: input and cache counts, plus a small partial output count.
        await WriteEventAsync(context, "message_start", new
        {
            type = "message_start",
            message = new
            {
                id = "msg_fake",
                type = "message",
                role = "assistant",
                model = "claude-fake-1",
                content = Array.Empty<object>(),
                stop_reason = NotStoppedYet,
                usage = new
                {
                    input_tokens = InputTokens,
                    cache_creation_input_tokens = CacheCreationTokens,
                    cache_read_input_tokens = CacheReadTokens,
                    output_tokens = 2,
                },
            },
        });

        if (EmitPing)
        {
            await WriteEventAsync(context, "ping", new { type = "ping" });
        }

        await WriteEventAsync(context, "content_block_start", new
        {
            type = "content_block_start",
            index = 0,
            content_block = new { type = "text", text = string.Empty },
        });

        // Cumulative output counts, rising with each delta. A provider that sums these arrives
        // at far more than FinalOutputTokens, which is exactly what the tests assert against.
        for (int i = 0; i < Chunks.Count; i++)
        {
            await WriteEventAsync(context, "content_block_delta", new
            {
                type = "content_block_delta",
                index = 0,
                delta = new { type = "text_delta", text = Chunks[i] },
            });

            int cumulative = (int)Math.Round((double)FinalOutputTokens * (i + 1) / Chunks.Count);
            await WriteEventAsync(context, "message_delta", new
            {
                type = "message_delta",
                delta = new { stop_reason = NotStoppedYet },
                usage = new { output_tokens = cumulative },
            });
        }

        if (EmitUnknownEvent)
        {
            // Anthropic documents that new event types may be added and clients must tolerate
            // them. A provider that fails here would abort a paid-for generation because the
            // upstream added something.
            await WriteEventAsync(context, "some_future_event", new
            {
                type = "some_future_event",
                payload = new { unexpected = true },
            });
        }

        await WriteEventAsync(context, "content_block_stop", new { type = "content_block_stop", index = 0 });

        await WriteEventAsync(context, "message_delta", new
        {
            type = "message_delta",
            delta = new { stop_reason = StopReason },
            usage = new { output_tokens = FinalOutputTokens },
        });

        await WriteEventAsync(context, "message_stop", new { type = "message_stop" });
    }

    private async Task CompleteAsync(HttpContext context)
    {
        var payload = new
        {
            id = "msg_fake",
            type = "message",
            role = "assistant",
            model = "claude-fake-1",
            content = new[] { new { type = "text", text = string.Concat(Chunks) } },
            stop_reason = StopReason,
            usage = new
            {
                input_tokens = InputTokens,
                cache_creation_input_tokens = CacheCreationTokens,
                cache_read_input_tokens = CacheReadTokens,
                output_tokens = FinalOutputTokens,
            },
        };

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(payload), Encoding.UTF8);
    }

    private static async Task WriteEventAsync(HttpContext context, string eventName, object payload)
    {
        // Serialized rather than hand-written: raw-string JSON and interpolation brace counting
        // fight each other, and a fake that emits subtly malformed JSON tests the wrong thing.
        string json = JsonSerializer.Serialize(payload);
        await context.Response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", Encoding.UTF8);
        await context.Response.Body.FlushAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
