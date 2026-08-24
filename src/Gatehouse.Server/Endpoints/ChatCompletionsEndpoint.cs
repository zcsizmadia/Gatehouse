using System.Text;
using System.Text.Json;
using Gatehouse.Caching;
using Gatehouse.Diagnostics;
using Gatehouse.Providers;
using Gatehouse.Resilience;
using Gatehouse.Routing;
using Gatehouse.Security;
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
        var dispatcher = context.RequestServices.GetRequiredService<IChatDispatcher>();
        var cache = context.RequestServices.GetRequiredService<IResponseCache>();
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

        // Checked here as well as in the dispatcher, and only for the primary route. Startup
        // validation makes it unreachable either way, but reaching it through this branch
        // produces "the gateway is misconfigured" rather than "every upstream is unavailable",
        // and for a configuration bug the first message is the one that saves an hour.
        if (!providers.TryGet(route.Provider, out _))
        {
            // If it happens anyway, it is a Gatehouse bug rather than a caller error, and it
            // says so.
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

        // Null when authentication is disabled, which the request log records faithfully as
        // unattributed rather than inventing an owner for it.
        tracker.AuthenticatedKey =
            context.Items[VirtualKeyAuthenticationMiddleware.AuthenticatedKeyItem] as VirtualKey;

        // Computed once and reused for the lookup and the store, because it is a SHA-256 over
        // the whole conversation and hashing a long prompt twice per request is not free.
        // Null when caching is off, which skips the hashing entirely.
        string? cacheKey = cache.Enabled
            ? CacheKey.Compute(request, route, CacheScopeFor(cache, tracker.AuthenticatedKey))
            : null;

        if (cacheKey is not null && cache.TryGet(cacheKey, out CachedResponse? hit))
        {
            await ServeFromCacheAsync(context, request, hit!.Response, tracker, clientToken);
            return;
        }

        if (request.Stream)
        {
            await HandleStreamingAsync(context, request, route, dispatcher, tracker, cache, cacheKey, clientToken);
        }
        else
        {
            await HandleBufferedAsync(context, request, route, dispatcher, tracker, cache, cacheKey, clientToken);
        }
    }

    /// <summary>
    /// The organisation a cache entry belongs to, or null for a gateway-wide cache.
    /// </summary>
    /// <remarks>
    /// An unattributed request — authentication disabled, or a key with no organisation — gets
    /// a scope of <c>"(unattributed)"</c> rather than null when scoping is on. Falling back to
    /// null would silently place those requests in the shared, cross-organisation pool, which
    /// is exactly the boundary the setting exists to hold.
    /// </remarks>
    private static string? CacheScopeFor(IResponseCache cache, VirtualKey? key)
    {
        return cache.ScopeToOrganisation
            ? key?.Organisation ?? "(unattributed)"
            : null;
    }

    /// <summary>Replays a cached completion in whichever form the caller asked for.</summary>
    private static async Task ServeFromCacheAsync(
        HttpContext context,
        ChatCompletionRequest request,
        ChatCompletionResponse cached,
        CompletionTracker tracker,
        CancellationToken clientToken)
    {
        // Announced in a header so a caller can tell. A cache that is invisible is a cache
        // nobody can debug, and "why is this response identical every time" is a support
        // ticket waiting to happen.
        context.Response.Headers["X-Gatehouse-Cache"] = "hit";

        tracker.ServedFromCache = true;

        if (request.Stream)
        {
            StartEventStream(context);
            var writer = new ServerSentEventWriter(context.Response.Body);

            foreach (ChatCompletionChunk chunk in CachedResponseReplay.ToChunks(
                         cached, tracker.Id, tracker.CreatedUnixSeconds))
            {
                await writer.WriteChunkAsync(chunk, clientToken);
                tracker.MarkFirstChunkFlushed();
            }

            await writer.WriteDoneAsync(clientToken);
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json; charset=utf-8";

            await JsonSerializer.SerializeAsync(
                context.Response.Body,
                CachedResponseReplay.ToResponse(cached, tracker.Id, tracker.CreatedUnixSeconds),
                GatehouseJsonContext.Default.ChatCompletionResponse,
                clientToken);
        }

        await tracker.CompleteAsync(
            StatusCodes.Status200OK,
            cached.Usage,
            cached.Model,
            cached.Choices.Count > 0 ? cached.Choices[0].FinishReason : null,
            errorType: null,
            cancellationToken: CancellationToken.None);
    }

    private static async Task HandleBufferedAsync(
        HttpContext context,
        ChatCompletionRequest request,
        ModelRoute route,
        IChatDispatcher dispatcher,
        CompletionTracker tracker,
        IResponseCache cache,
        string? cacheKey,
        CancellationToken clientToken)
    {
        try
        {
            BufferedDispatch dispatch = await dispatcher.CompleteAsync(request, route, clientToken);
            ChatCompletionResponse response = dispatch.Response;

            // Re-pointed at the route that actually answered, which after a fallback is not
            // the one the caller asked for. The request log is chargeback data: attributing
            // the spend to the primary provider would bill an account that was never called.
            tracker.Route = dispatch.Route;

            // Stored before the response is written rather than after. Writing to the client
            // can fail — a disconnect mid-serialisation — and an answer the provider was paid
            // for is worth keeping whether or not this particular caller received it.
            if (cacheKey is not null)
            {
                cache.Store(cacheKey, response);
            }

            tracker.Route = dispatch.Route;

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
        IChatDispatcher dispatcher,
        CompletionTracker tracker,
        IResponseCache cache,
        string? cacheKey,
        CancellationToken clientToken)
    {
        StreamedDispatch dispatch;

        try
        {
            // The dispatcher pulls the first chunk before returning, deliberately, and before
            // any response header is written here. Until that chunk exists the status line is
            // still ours to choose and a failed upstream can still be swapped for another one;
            // an upstream that rejects the request outright therefore produces a real 4xx or
            // 5xx rather than a 200 whose body immediately announces a failure. Once headers
            // are on the wire both options are gone for good.
            dispatch = await dispatcher.StreamAsync(request, route, clientToken);
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
        catch (OperationCanceledException) when (clientToken.IsCancellationRequested)
        {
            await tracker.CompleteAsync(
                StatusCodes.Status499ClientClosedRequest,
                usage: null,
                errorType: "client_disconnected",
                cancellationToken: CancellationToken.None);
            return;
        }

        // See HandleBufferedAsync: after a fallback this is not the route the caller asked for.
        tracker.Route = dispatch.Route;

        IAsyncEnumerator<ChatCompletionChunk> chunks = dispatch.Chunks;

        try
        {
            bool hasFirst = dispatch.HasFirstChunk;

            StartEventStream(context);
            var writer = new ServerSentEventWriter(context.Response.Body);

            TokenUsage? usage = null;
            string? finishReason = null;
            string? responseModel = null;

            // Only allocated when caching is on, so a deployment with the cache off pays
            // nothing for it.
            StringBuilder? assembled = cacheKey is not null ? new StringBuilder() : null;
            string? assembledRole = null;
            bool cacheable = cacheKey is not null;

            while (hasFirst)
            {
                ChatCompletionChunk current = chunks.Current;

                if (assembled is not null)
                {
                    if (current.Choices.Count > 1)
                    {
                        // A multi-choice stream cannot be reassembled by appending deltas into
                        // one string. Rather than store something subtly wrong, don't store it:
                        // n > 1 is rare, and a cache that returns the wrong number of choices is
                        // worse than a cache that misses.
                        cacheable = false;
                    }
                    else if (current.Choices.Count == 1)
                    {
                        assembled.Append(current.Choices[0].Delta.Content);
                        assembledRole ??= current.Choices[0].Delta.Role;
                    }
                }

                ChunkOutcome outcome = await WriteChunkAsync(writer, current, tracker, clientToken);

                // The final chunk carries usage and the finish reason; earlier ones do not.
                // Keeping the last non-null of each means a provider that reports them early,
                // late, or repeatedly all produce the same recorded result.
                usage = outcome.Usage ?? usage;
                finishReason = outcome.FinishReason ?? finishReason;
                responseModel = outcome.Model;

                hasFirst = await chunks.MoveNextAsync();
            }

            await writer.WriteDoneAsync(clientToken);

            // A finish reason is the proof the upstream finished rather than the connection
            // ending early. Caching a truncated answer would replay half a completion to every
            // caller for the whole TTL — the worst failure this cache can have, because it
            // looks like a model that just stops.
            if (cacheable && assembled is not null && finishReason is not null && responseModel is not null)
            {
                cache.Store(
                    cacheKey!,
                    new ChatCompletionResponse
                    {
                        Id = tracker.Id,
                        Created = tracker.CreatedUnixSeconds,
                        Model = responseModel,
                        Choices =
                        [
                            new ChatChoice
                            {
                                Index = 0,
                                Message = new ChatMessage
                                {
                                    Role = assembledRole ?? ChatRoles.Assistant,
                                    Content = assembled.ToString(),
                                },
                                FinishReason = finishReason,
                            },
                        ],
                        Usage = usage,
                        GatehouseProvider = dispatch.Route.Provider,
                    });
            }

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
            // Disposes the enumerator the dispatcher handed over, and with it the upstream
            // response stream.
            await dispatch.DisposeAsync();
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
