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
/// A stub Gemini generateContent API over a real Kestrel.
/// </summary>
/// <remarks>
/// Reports <c>thoughtsTokenCount</c> alongside <c>candidatesTokenCount</c>, and a
/// <c>totalTokenCount</c> that includes both — which is what Gemini does and what makes naive
/// forwarding of its total inconsistent with prompt-plus-completion.
/// </remarks>
internal sealed class FakeGeminiUpstream : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeGeminiUpstream(WebApplication app, string baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
    }

    /// <summary>The address Gatehouse should be pointed at.</summary>
    public string BaseAddress { get; }

    /// <summary>Text fragments emitted, one per streamed chunk.</summary>
    public IReadOnlyList<string> Chunks { get; set; } = ["The ", "gate ", "answers."];

    /// <summary>Prompt tokens, inclusive of cached content.</summary>
    public int PromptTokenCount { get; set; } = 1000;

    /// <summary>Cached content tokens — a subset of the prompt, unlike Anthropic.</summary>
    public int CachedContentTokenCount { get; set; } = 900;

    /// <summary>Visible output tokens.</summary>
    public int CandidatesTokenCount { get; set; } = 40;

    /// <summary>Thinking tokens, billed as output but reported separately.</summary>
    public int ThoughtsTokenCount { get; set; } = 160;

    /// <summary>Upper-case finish reason, as Gemini reports it.</summary>
    public string FinishReason { get; set; } = "STOP";

    /// <summary>The request path the upstream received, for asserting on addressing.</summary>
    public string? LastPath { get; private set; }

    /// <summary>The query string received, for asserting <c>alt=sse</c>.</summary>
    public string? LastQuery { get; private set; }

    /// <summary>The request body received, for asserting on translation.</summary>
    public string? LastRequestBody { get; private set; }

    /// <summary>The <c>x-goog-api-key</c> header received.</summary>
    public string? LastApiKey { get; private set; }

    /// <summary>Starts the fake on an ephemeral loopback port.</summary>
    public static async Task<FakeGeminiUpstream> StartAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        WebApplication app = builder.Build();
        FakeGeminiUpstream? instance = null;

        // A catch-all: the model and the method are both in the path, so the route cannot be
        // fixed. Capturing the whole path is also how the tests assert on addressing.
        app.MapPost("/{**path}", async (HttpContext context) => await instance!.HandleAsync(context));

        await app.StartAsync();

        instance = new FakeGeminiUpstream(app, app.Urls.First());
        return instance;
    }

    private async Task HandleAsync(HttpContext context)
    {
        LastPath = context.Request.Path.Value;
        LastQuery = context.Request.QueryString.Value;
        LastApiKey = context.Request.Headers["x-goog-api-key"].ToString();

        using var reader = new StreamReader(context.Request.Body);
        LastRequestBody = await reader.ReadToEndAsync();

        bool stream = LastPath?.Contains(":streamGenerateContent", StringComparison.Ordinal) == true;

        if (stream)
        {
            await StreamAsync(context);
        }
        else
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                JsonSerializer.Serialize(BuildChunk(string.Concat(Chunks), includeUsage: true, final: true)),
                Encoding.UTF8);
        }
    }

    private async Task StreamAsync(HttpContext context)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        for (int i = 0; i < Chunks.Count; i++)
        {
            bool final = i == Chunks.Count - 1;

            // Usage on the final chunk only, which is the shape Gemini uses in practice.
            object chunk = BuildChunk(Chunks[i], includeUsage: final, final: final);

            await context.Response.WriteAsync(
                $"data: {JsonSerializer.Serialize(chunk)}\n\n",
                Encoding.UTF8);
            await context.Response.Body.FlushAsync();
        }
    }

    private object BuildChunk(string text, bool includeUsage, bool final)
    {
        var candidate = new
        {
            content = new { role = "model", parts = new[] { new { text } } },
            finishReason = final ? FinishReason : null,
        };

        if (!includeUsage)
        {
            return new
            {
                candidates = new[] { candidate },
                modelVersion = "gemini-fake-1",
                responseId = "resp_fake",
            };
        }

        return new
        {
            candidates = new[] { candidate },
            usageMetadata = new
            {
                promptTokenCount = PromptTokenCount,
                cachedContentTokenCount = CachedContentTokenCount,
                candidatesTokenCount = CandidatesTokenCount,
                thoughtsTokenCount = ThoughtsTokenCount,

                // Gemini's own total includes thinking tokens, so it does not equal
                // prompt + candidates. Forwarding it verbatim would look inconsistent.
                totalTokenCount = PromptTokenCount + CandidatesTokenCount + ThoughtsTokenCount,
            },
            modelVersion = "gemini-fake-1",
            responseId = "resp_fake",
        };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
