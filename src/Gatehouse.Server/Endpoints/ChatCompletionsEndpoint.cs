using System.Text.Json;
using Gatehouse.Diagnostics;
using Gatehouse.Providers;
using Gatehouse.Routing;
using Gatehouse.Server.Infrastructure;
using Gatehouse.Storage;
using Gatehouse.Streaming;
using Gatehouse.Wire;
using Microsoft.AspNetCore.Http.Features;

namespace Gatehouse.Server.Endpoints;

/// <summary>
/// The OpenAI-compatible <c>/v1/chat/completions</c> endpoint.
/// </summary>
internal static class ChatCompletionsEndpoint
{
    /// <summary>Maps the endpoint.</summary>
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/chat/completions", HandleAsync)
           .WithName("CreateChatCompletion")
           .WithSummary("Creates a chat completion, optionally streamed as server-sent events.");
    }

    private static async Task HandleAsync(HttpContext context)
    {
        CancellationToken clientToken = context.RequestAborted;

        var router = context.RequestServices.GetRequiredService<IModelRouter>();
        var providers = context.RequestServices.GetRequiredService<IProviderRegistry>();
        var store = context.RequestServices.GetRequiredService<IRequestLogStore>();
        var timeProvider = context.RequestServices.GetRequiredService<TimeProvider>();
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger("Gatehouse.Server.ChatCompletions");

        ChatCompletionRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                GatehouseJsonContext.Default.ChatCompletionRequest,
                clientToken);
        }
        catch (JsonException ex)
        {
            // The parser message names the offending path, which is far more useful to the
            // caller than "bad request" and discloses nothing about Gatehouse internals.
            await WriteErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                ErrorResponse.Create($"The request body could not be parsed: {ex.Message}", ErrorTypes.InvalidRequest));
            return;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Model) || request.Messages.Count == 0)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                ErrorResponse.Create(
                    "A chat completion requires a non-empty 'model' and at least one message.",
                    ErrorTypes.InvalidRequest));
            return;
        }

        if (!router.TryResolve(request.Model, out ModelRoute? route))
        {
            // Rejected before any tracker is started: nothing was billed and no provider was
            // contacted, so this belongs on the rejection counter rather than in the request
            // log as a zero-token completion.
            GatehouseTelemetry.RequestsRejected.Add(
                1,
                new KeyValuePair<string, object?>(GatehouseTelemetry.Attributes.ErrorType, "model_not_found"));

            await WriteErrorAsync(
                context,
                StatusCodes.Status404NotFound,
                ErrorResponse.Create(
                    $"The model '{request.Model}' is not configured on this gateway.",
                    ErrorTypes.InvalidRequest,
                    "model_not_found"));
            return;
        }

        if (!providers.TryGet(route.Provider, out IChatProvider? provider))
        {
            // Startup validation should make this unreachable. If it happens anyway, it is a
            // Gatehouse bug rather than a caller error, and it says so.
            logger.LogError(
                "Model '{Alias}' resolved to provider '{Provider}', which is not registered. "
                + "This indicates a configuration validation gap.",
                route.Alias,
                route.Provider);

            await WriteErrorAsync(
                context,
                StatusCodes.Status500InternalServerError,
                ErrorResponse.Create(
                    "The gateway is misconfigured for this model.",
                    ErrorTypes.Internal));
            return;
        }

        CompletionTracker tracker = CompletionTracker.Start(request, store, timeProvider);
        tracker.Route = route;

        if (request.Stream)
        {
            await HandleStreamingAsync(context, request, route, provider, tracker, clientToken);
        }
        else
        {
            await HandleBufferedAsync(context, request, route, provider, tracker, clientToken);
        }
    }

    private static async Task HandleBufferedAsync(
        HttpContext context,
        ChatCompletionRequest request,
        ModelRoute route,
        IChatProvider provider,
        CompletionTracker tracker,
        CancellationToken clientToken)
    {
        try
        {
            ChatCompletionResponse response = await provider.CompleteAsync(request, route, clientToken);

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(
                context.Response.Body,
                response,
                GatehouseJsonContext.Default.ChatCompletionResponse,
                clientToken);

            await tracker.CompleteAsync(
                StatusCodes.Status200OK,
                response.Usage,
                response.Model,
                response.Choices.Count > 0 ? response.Choices[0].FinishReason : null,
                errorType: null,
                cancellationToken: CancellationToken.None);
        }
        catch (ProviderException ex)
        {
            int status = ex.StatusCode is { } upstream && (int)upstream >= 400
                ? (int)upstream
                : StatusCodes.Status502BadGateway;

            await WriteErrorAsync(context, status, ex.ToErrorResponse());
            await tracker.CompleteAsync(status, usage: null, errorType: ErrorTypes.Upstream, cancellationToken: CancellationToken.None);
        }
        catch (OperationCanceledException) when (clientToken.IsCancellationRequested)
        {
            // The caller hung up. There is no response to write, but the request still
            // consumed upstream tokens and still belongs in the log.
            await tracker.CompleteAsync(
                StatusCodes.Status499ClientClosedRequest,
                usage: null,
                errorType: "client_disconnected",
                cancellationToken: CancellationToken.None);
        }
    }

    private static async Task HandleStreamingAsync(
        HttpContext context,
        ChatCompletionRequest request,
        ModelRoute route,
        IChatProvider provider,
        CompletionTracker tracker,
        CancellationToken clientToken)
    {
        IAsyncEnumerator<ChatCompletionChunk> chunks =
            provider.StreamAsync(request, route, clientToken).GetAsyncEnumerator(clientToken);

        try
        {
            bool hasFirst;
            try
            {
                // Deliberately pulled before any response header is written. Until the first
                // chunk exists the status line is still ours to choose, so an upstream that
                // rejects the request outright produces a real 4xx or 5xx rather than a
                // 200 whose body immediately announces a failure. Once headers are on the
                // wire that option is gone for good.
                hasFirst = await chunks.MoveNextAsync();
            }
            catch (ProviderException ex)
            {
                int status = ex.StatusCode is { } upstream && (int)upstream >= 400
                    ? (int)upstream
                    : StatusCodes.Status502BadGateway;

                await WriteErrorAsync(context, status, ex.ToErrorResponse());
                await tracker.CompleteAsync(status, usage: null, errorType: ErrorTypes.Upstream, cancellationToken: CancellationToken.None);
                return;
            }

            StartEventStream(context);
            var writer = new ServerSentEventWriter(context.Response.Body);

            TokenUsage? usage = null;
            string? finishReason = null;
            string? responseModel = null;

            while (hasFirst)
            {
                ChunkOutcome outcome = await WriteChunkAsync(writer, chunks.Current, tracker, clientToken);

                // The final chunk carries usage and the finish reason; earlier ones do not.
                // Keeping the last non-null of each means a provider that reports them early,
                // late, or repeatedly all produce the same recorded result.
                usage = outcome.Usage ?? usage;
                finishReason = outcome.FinishReason ?? finishReason;
                responseModel = outcome.Model;

                hasFirst = await chunks.MoveNextAsync();
            }

            await writer.WriteDoneAsync(clientToken);
            await tracker.CompleteAsync(
                StatusCodes.Status200OK,
                usage,
                responseModel,
                finishReason,
                errorType: null,
                cancellationToken: CancellationToken.None);
        }
        catch (ProviderException ex)
        {
            // Failed mid-stream. The 200 is already committed, so the only way to tell the
            // caller apart from a clean finish is an in-band error event.
            var writer = new ServerSentEventWriter(context.Response.Body);
            await writer.WriteErrorAsync(ex.ToErrorResponse(), CancellationToken.None);
            await writer.WriteDoneAsync(CancellationToken.None);
            await tracker.CompleteAsync(
                StatusCodes.Status200OK,
                usage: null,
                errorType: ErrorTypes.Upstream,
                cancellationToken: CancellationToken.None);
        }
        catch (OperationCanceledException) when (clientToken.IsCancellationRequested)
        {
            await tracker.CompleteAsync(
                StatusCodes.Status499ClientClosedRequest,
                usage: null,
                errorType: "client_disconnected",
                cancellationToken: CancellationToken.None);
        }
        finally
        {
            await chunks.DisposeAsync();
        }
    }

    /// <summary>What one streamed chunk contributed to the recorded result.</summary>
    private readonly record struct ChunkOutcome(TokenUsage? Usage, string? FinishReason, string Model);

    private static async Task<ChunkOutcome> WriteChunkAsync(
        ServerSentEventWriter writer,
        ChatCompletionChunk chunk,
        CompletionTracker tracker,
        CancellationToken cancellationToken)
    {
        await writer.WriteChunkAsync(chunk, cancellationToken);

        // Marked after the flush completes, so the measurement includes the write rather
        // than only the time spent waiting for the provider.
        tracker.MarkFirstChunkFlushed();

        return new ChunkOutcome(
            chunk.Usage,
            chunk.Choices.Count > 0 ? chunk.Choices[0].FinishReason : null,
            chunk.Model);
    }

    /// <summary>
    /// Sets the response headers for a server-sent event stream and disables buffering.
    /// </summary>
    /// <remarks>
    /// <c>X-Accel-Buffering: no</c> is not decoration. nginx buffers proxied responses by
    /// default, which collects the whole completion and delivers it at once — the gateway
    /// streams correctly, the user sees nothing for twenty seconds, and every test passes.
    /// The header is the documented way to switch that off, and shops running nginx in front
    /// of the gateway are the common case rather than the exception.
    /// </remarks>
    private static void StartEventStream(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache, no-transform";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        // Kestrel's own response buffering has to go too, for the same reason.
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, ErrorResponse error)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";

        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            error,
            GatehouseJsonContext.Default.ErrorResponse,
            CancellationToken.None);
    }
}
