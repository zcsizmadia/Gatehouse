using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Gatehouse.Metering;
using Gatehouse.Providers.Anthropic.Wire;
using Gatehouse.Routing;
using Gatehouse.Streaming;
using Gatehouse.Wire;
using Microsoft.Extensions.Logging;
using WireChatMessage = Gatehouse.Wire.ChatMessage;

namespace Gatehouse.Providers.Anthropic;

/// <summary>
/// Serves completions from the Anthropic Messages API.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than built on the official <c>Anthropic</c> SDK, which is verified
/// present and vendor-owned but carries no trimming annotations and so cannot be used from the
/// NativeAOT build this project gates on. The wire format is small enough that owning it costs
/// less than the alternative, and owning it is what keeps the cache-token fields that invoice
/// reconciliation depends on. See ADR 0002.
/// </para>
/// <para>
/// Two provider-specific hazards are handled here and documented at the point of use: the
/// system prompt is a top-level field rather than a message role, and streamed token usage is
/// cumulative rather than incremental.
/// </para>
/// </remarks>
public sealed class AnthropicProvider : IChatProvider
{
    /// <summary>The <c>kind</c> value that selects this provider in configuration.</summary>
    public const string Kind = "anthropic";

    /// <summary>The API version header value this implementation is written against.</summary>
    public const string AnthropicVersion = "2023-06-01";

    private const string MessagesPath = "v1/messages";
    private const int MaxUpstreamErrorLength = 2048;

    private readonly HttpClient _httpClient;
    private readonly ILogger<AnthropicProvider> _logger;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a provider bound to one configured Anthropic endpoint.</summary>
    /// <param name="name">The configuration key routes refer to this provider by.</param>
    /// <param name="httpClient">
    /// A client whose base address and <c>x-api-key</c> and <c>anthropic-version</c> headers
    /// have already been applied, and whose <see cref="HttpClient.Timeout"/> is infinite.
    /// </param>
    /// <param name="timeout">Applied per call through a linked token, never via the client.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">Clock, for response timestamps.</param>
    public AnthropicProvider(
        string name,
        HttpClient httpClient,
        TimeSpan timeout,
        ILogger<AnthropicProvider> logger,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        Name = name;
        _httpClient = httpClient;
        _timeout = timeout;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public async Task<ChatCompletionResponse> CompleteAsync(
        ChatCompletionRequest request,
        ModelRoute route,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(route);

        using HttpRequestMessage upstream = BuildRequest(request, route, stream: false);

        using CancellationTokenSource timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        using HttpResponseMessage response = await _httpClient
            .SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateUpstreamExceptionAsync(response, timeoutSource.Token);
        }

        await using Stream body = await response.Content.ReadAsStreamAsync(timeoutSource.Token);

        AnthropicMessage? message = await JsonSerializer.DeserializeAsync(
            body,
            AnthropicJsonContext.Default.AnthropicMessage,
            timeoutSource.Token);

        if (message is null)
        {
            throw new ProviderException(
                Name,
                "The upstream returned an empty body where a message was expected.",
                response.StatusCode,
                isRetryable: true);
        }

        string text = ConcatenateText(message.Content);

        return new ChatCompletionResponse
        {
            Id = message.Id ?? NewCompletionId(),
            Created = _timeProvider.GetUtcNow().ToUnixTimeSeconds(),
            Model = message.Model ?? route.UpstreamModel,
            GatehouseProvider = Name,
            Choices =
            [
                new ChatChoice
                {
                    Index = 0,
                    Message = new WireChatMessage { Role = ChatRoles.Assistant, Content = text },
                    FinishReason = AnthropicTranslator.ToOpenAiFinishReason(message.StopReason),
                },
            ],
            Usage = VetUsage(AnthropicTranslator.ToTokenUsage(message.Usage)),
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatCompletionChunk> StreamAsync(
        ChatCompletionRequest request,
        ModelRoute route,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(route);

        using HttpRequestMessage upstream = BuildRequest(request, route, stream: true);

        // The timeout covers response headers only, then it is disarmed. See the
        // OpenAI-compatible provider for why a total timeout is wrong for a stream.
        using CancellationTokenSource headerTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerTimeout.CancelAfter(_timeout);

        using HttpResponseMessage response = await _httpClient
            .SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);

        headerTimeout.CancelAfter(Timeout.InfiniteTimeSpan);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateUpstreamExceptionAsync(response, cancellationToken);
        }

        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);

        string completionId = NewCompletionId();
        long created = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        string model = route.UpstreamModel;
        bool roleSent = false;

        TokenUsage? usage = null;
        string? finishReason = null;

        await foreach (ServerSentEvent sse in ServerSentEventReader.ReadAsync(body, cancellationToken))
        {
            AnthropicStreamEvent? evt = ParseEvent(sse.Data);
            if (evt is null)
            {
                continue;
            }

            switch (evt.Type)
            {
                case AnthropicEventTypes.Ping:
                    // Keep-alive. Carries nothing; forwarding it would inject a phantom chunk.
                    continue;

                case AnthropicEventTypes.Error:
                    throw new ProviderException(
                        Name,
                        $"Anthropic reported a stream error: {evt.Error?.Type} {evt.Error?.Message}".TrimEnd(),
                        statusCode: null,
                        isRetryable: IsRetryableStreamError(evt.Error?.Type));

                case AnthropicEventTypes.MessageStart:
                    // Carries the input and cache token counts, plus the resolved model.
                    model = evt.Message?.Model ?? model;
                    completionId = evt.Message?.Id ?? completionId;
                    usage = MergeUsage(usage, evt.Message?.Usage);
                    continue;

                case AnthropicEventTypes.ContentBlockDelta:
                    if (evt.Delta?.Type != AnthropicDeltaTypes.TextDelta
                        || string.IsNullOrEmpty(evt.Delta.Text))
                    {
                        // A non-text delta — thinking, tool input — has no OpenAI equivalent in
                        // Phase 1. Dropped rather than emitted as empty content.
                        continue;
                    }

                    yield return new ChatCompletionChunk
                    {
                        Id = completionId,
                        Created = created,
                        Model = model,
                        Choices =
                        [
                            new ChatChunkChoice
                            {
                                Index = 0,
                                Delta = new ChatDelta
                                {
                                    Role = roleSent ? null : ChatRoles.Assistant,
                                    Content = evt.Delta.Text,
                                },
                            },
                        ],
                    };

                    roleSent = true;
                    continue;

                case AnthropicEventTypes.MessageDelta:
                    finishReason = AnthropicTranslator.ToOpenAiFinishReason(evt.Delta?.StopReason) ?? finishReason;

                    // Cumulative, not incremental. MergeUsage replaces rather than adds; see
                    // AnthropicTranslator.MergeCumulative.
                    usage = MergeUsage(usage, evt.Usage);
                    continue;

                case AnthropicEventTypes.MessageStop:
                    break;

                default:
                    // Anthropic documents that new event types may be added and that clients
                    // must tolerate them. Ignoring the unknown keeps a forward-compatible
                    // addition from aborting a generation the caller is already paying for.
                    continue;
            }

            break;
        }

        // A terminal chunk carrying the finish reason and the final usage, matching what an
        // OpenAI client expects on the last event of a stream.
        yield return new ChatCompletionChunk
        {
            Id = completionId,
            Created = created,
            Model = model,
            Choices =
            [
                new ChatChunkChoice
                {
                    Index = 0,
                    Delta = ChatDelta.Empty,
                    FinishReason = finishReason ?? FinishReasons.Stop,
                },
            ],
            Usage = VetUsage(usage),
        };
    }

    private static TokenUsage? MergeUsage(TokenUsage? existing, AnthropicUsage? reported)
    {
        TokenUsage? mapped = AnthropicTranslator.ToTokenUsage(reported);
        return mapped is null ? existing : AnthropicTranslator.MergeCumulative(existing, mapped);
    }

    /// <summary>
    /// Runs the metering consistency check, logging rather than throwing on a discrepancy.
    /// </summary>
    private TokenUsage? VetUsage(TokenUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        return MeteringConsistency.Vet(usage, discrepancy => _logger.MeteringDiscrepancy(Name, discrepancy));
    }

    private AnthropicStreamEvent? ParseEvent(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(payload, AnthropicJsonContext.Default.AnthropicStreamEvent);
        }
        catch (JsonException ex)
        {
            _logger.UnparseableStreamEvent(Name, ex.Message);
            return null;
        }
    }

    private HttpRequestMessage BuildRequest(ChatCompletionRequest request, ModelRoute route, bool stream)
    {
        AnthropicRequest body = AnthropicTranslator.ToAnthropicRequest(request, route, Name, stream);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(body, AnthropicJsonContext.Default.AnthropicRequest);

        var message = new HttpRequestMessage(HttpMethod.Post, MessagesPath)
        {
            Content = new ByteArrayContent(json),
        };

        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        if (stream)
        {
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        }

        return message;
    }

    /// <summary>
    /// Whether an in-band stream error is worth retrying against a fallback route.
    /// </summary>
    /// <remarks>
    /// <c>overloaded_error</c> is Anthropic's capacity signal and is exactly what a fallback
    /// chain exists for. An <c>invalid_request_error</c> will fail identically on every retry,
    /// so retrying it only multiplies the failure.
    /// </remarks>
    private static bool IsRetryableStreamError(string? errorType) => errorType switch
    {
        "overloaded_error" => true,
        "api_error" => true,
        "rate_limit_error" => true,
        _ => false,
    };

    private static string ConcatenateText(IReadOnlyList<AnthropicContentBlock>? blocks)
    {
        if (blocks is null || blocks.Count == 0)
        {
            return string.Empty;
        }

        if (blocks.Count == 1)
        {
            return blocks[0].Text ?? string.Empty;
        }

        // A response may contain several blocks. Only text contributes to an OpenAI-shaped
        // message; thinking and tool blocks are skipped rather than stringified.
        var builder = new System.Text.StringBuilder();
        foreach (AnthropicContentBlock block in blocks)
        {
            if (block.Type == "text" && !string.IsNullOrEmpty(block.Text))
            {
                builder.Append(block.Text);
            }
        }

        return builder.ToString();
    }

    private async Task<ProviderException> CreateUpstreamExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string detail = await ReadErrorBodyAsync(response, cancellationToken);
        bool retryable = ProviderException.IsRetryableStatus(response.StatusCode);

        _logger.UpstreamCallFailed(Name, (int)response.StatusCode, retryable, detail);

        return new ProviderException(
            Name,
            $"Upstream provider '{Name}' returned {(int)response.StatusCode} {response.StatusCode}. {detail}".TrimEnd(),
            response.StatusCode,
            retryable);
    }

    private static async Task<string> ReadErrorBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                return string.Empty;
            }

            return body.Length <= MaxUpstreamErrorLength
                ? body
                : string.Concat(body.AsSpan(0, MaxUpstreamErrorLength), "… (truncated)");
        }
        catch (HttpRequestException)
        {
            return string.Empty;
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static string NewCompletionId() => $"chatcmpl-{Guid.NewGuid():N}";
}
