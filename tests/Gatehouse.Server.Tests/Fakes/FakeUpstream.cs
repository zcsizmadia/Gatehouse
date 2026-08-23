using System.Net;
using System.Text;
using System.Text.Json;
using Gatehouse.Wire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gatehouse.Server.Tests.Fakes;

/// <summary>
/// A minimal OpenAI-compatible provider, hosted on a real Kestrel for tests.
/// </summary>
/// <remarks>
/// A stubbed <c>HttpMessageHandler</c> would be simpler and would prove much less. The
/// behaviours under test — that a chunk reaches the client as soon as the upstream emits it,
/// that a pre-stream failure still yields a real status code — only exist over a real socket
/// with real response buffering on both sides.
/// </remarks>
internal sealed class FakeUpstream : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeUpstream(WebApplication app, string baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
    }

    /// <summary>The address Gatehouse should be pointed at.</summary>
    public string BaseAddress { get; }

    /// <summary>Text fragments to emit, one per streamed chunk.</summary>
    public IReadOnlyList<string> Chunks { get; set; } = ["Hello", " there"];

    /// <summary>Delay inserted before each chunk, so ordering can be observed.</summary>
    public TimeSpan ChunkDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Status to return instead of a completion. Null means succeed.</summary>
    public HttpStatusCode? FailWith { get; set; }

    /// <summary>Whether the stream should report usage on its final chunk.</summary>
    public bool ReportUsage { get; set; } = true;

    /// <summary>Fails after this many chunks, simulating an upstream dying mid-generation.</summary>
    public int? FailAfterChunk { get; set; }

    /// <summary>The model name the upstream received, for asserting alias translation.</summary>
    public string? LastRequestedModel { get; private set; }

    /// <summary>The Authorization header the upstream received.</summary>
    public string? LastAuthorization { get; private set; }

    /// <summary>Starts a fake upstream on an ephemeral loopback port.</summary>
    public static async Task<FakeUpstream> StartAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        WebApplication app = builder.Build();

        // Assigned after the app object exists so handlers can close over it.
        FakeUpstream? instance = null;

        app.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            await instance!.HandleAsync(context);
        });

        await app.StartAsync();

        string address = app.Urls.First();
        instance = new FakeUpstream(app, $"{address}/v1");
        return instance;
    }

    private async Task HandleAsync(HttpContext context)
    {
        ChatCompletionRequest? request = await JsonSerializer.DeserializeAsync(
            context.Request.Body,
            GatehouseJsonContext.Default.ChatCompletionRequest);

        LastRequestedModel = request?.Model;
        LastAuthorization = context.Request.Headers.Authorization.ToString();

        if (FailWith is { } failure)
        {
            context.Response.StatusCode = (int)failure;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync($"{{\"error\":{{\"message\":\"fake upstream returned {(int)failure}\"}}}}");
            return;
        }

        if (request?.Stream == true)
        {
            await StreamAsync(context, request);
        }
        else
        {
            await CompleteAsync(context, request!);
        }
    }

    private async Task StreamAsync(HttpContext context, ChatCompletionRequest request)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        for (int i = 0; i < Chunks.Count; i++)
        {
            if (FailAfterChunk == i)
            {
                // Abort without the terminating sentinel, the way a real upstream failure looks.
                context.Abort();
                return;
            }

            if (ChunkDelay > TimeSpan.Zero)
            {
                await Task.Delay(ChunkDelay, context.RequestAborted);
            }

            bool isLast = i == Chunks.Count - 1;

            var chunk = new ChatCompletionChunk
            {
                Id = "chatcmpl-fake",
                Created = 1_700_000_000,
                Model = request.Model,
                Choices =
                [
                    new ChatChunkChoice
                    {
                        Index = 0,
                        Delta = new ChatDelta
                        {
                            Role = i == 0 ? ChatRoles.Assistant : null,
                            Content = Chunks[i],
                        },
                        FinishReason = isLast ? FinishReasons.Stop : null,
                    },
                ],
                Usage = isLast && ReportUsage ? TokenUsage.FromProvider(11, 7) : null,
            };

            string payload = JsonSerializer.Serialize(chunk, GatehouseJsonContext.Default.ChatCompletionChunk);
            await context.Response.WriteAsync($"data: {payload}\n\n", Encoding.UTF8);
            await context.Response.Body.FlushAsync();
        }

        await context.Response.WriteAsync("data: [DONE]\n\n", Encoding.UTF8);
        await context.Response.Body.FlushAsync();
    }

    private async Task CompleteAsync(HttpContext context, ChatCompletionRequest request)
    {
        var response = new ChatCompletionResponse
        {
            Id = "chatcmpl-fake",
            Created = 1_700_000_000,
            Model = request.Model,
            Choices =
            [
                new ChatChoice
                {
                    Index = 0,
                    Message = new Wire.ChatMessage
                    {
                        Role = ChatRoles.Assistant,
                        Content = string.Concat(Chunks),
                    },
                    FinishReason = FinishReasons.Stop,
                },
            ],
            Usage = ReportUsage ? TokenUsage.FromProvider(11, 7) : null,
        };

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            response,
            GatehouseJsonContext.Default.ChatCompletionResponse);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
