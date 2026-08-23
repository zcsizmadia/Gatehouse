using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Gatehouse.Metering;
using Gatehouse.Providers.Google.Wire;
using Gatehouse.Routing;
using Gatehouse.Streaming;
using Gatehouse.Wire;
using Microsoft.Extensions.Logging;
using WireChatMessage = Gatehouse.Wire.ChatMessage;

namespace Gatehouse.Providers.Google;

/// <summary>
/// Serves completions from the Google Gemini Developer API.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written because Google publishes no official .NET SDK for this API. Its official
/// package, <c>Google.Cloud.AIPlatform.V1</c>, covers Vertex AI instead, targets only
/// netstandard2.0 and net462, and carries no trimming annotations — so it would not serve this
/// endpoint and could not be used from the NativeAOT build regardless. The community packages
/// are unverified. See ADR 0002.
/// </para>
/// <para>
/// The API key travels in the <c>x-goog-api-key</c> header rather than the <c>?key=</c> query
/// parameter that the documentation also permits. A credential in a query string ends up in
/// access logs, proxy logs and error reports.
/// </para>
/// </remarks>
public sealed class GeminiProvider : IChatProvider
{
    /// <summary>The <c>kind</c> value that selects this provider in configuration.</summary>
    public const string Kind = "google-gemini";

    private const int MaxUpstreamErrorLength = 2048;

    private readonly HttpClient _httpClient;
    private readonly ILogger<GeminiProvider> _logger;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a provider bound to one configured Gemini endpoint.</summary>
    /// <param name="name">The configuration key routes refer to this provider by.</param>
    /// <param name="httpClient">
    /// A client whose base address and <c>x-goog-api-key</c> header have already been applied,
    /// and whose <see cref="HttpClient.Timeout"/> is infinite.
    /// </param>
    /// <param name="timeout">Applied per call through a linked token, never via the client.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">Clock, for response timestamps.</param>
    public GeminiProvider(
        string name,
        HttpClient httpClient,
        TimeSpan timeout,
        ILogger<GeminiProvider> logger,
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

        GeminiResponse? parsed = await JsonSerializer.DeserializeAsync(
            body,
            GeminiJsonContext.Default.GeminiResponse,
            timeoutSource.Token);

        if (parsed is null)
        {
            throw new ProviderException(
                Name,
                "The upstream returned an empty body where a completion was expected.",
                response.StatusCode,
                isRetryable: true);
        }

        GeminiCandidate? candidate = parsed.Candidates is { Count: > 0 } c ? c[0] : null;

        return new ChatCompletionResponse
        {
            Id = parsed.ResponseId ?? NewCompletionId(),
            Created = _timeProvider.GetUtcNow().ToUnixTimeSeconds(),
            Model = parsed.ModelVersion ?? route.UpstreamModel,
            GatehouseProvider = Name,
            Choices =
            [
                new ChatChoice
                {
                    Index = 0,
                    Message = new WireChatMessage
                    {
                        Role = ChatRoles.Assistant,
                        Content = GeminiTranslator.ExtractText(candidate),
                    },
                    FinishReason = GeminiTranslator.ToOpenAiFinishReason(candidate?.FinishReason),
                },
            ],
            Usage = VetUsage(GeminiTranslator.ToTokenUsage(parsed.UsageMetadata)),
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
            GeminiResponse? chunk = ParseChunk(sse.Data);
            if (chunk is null)
            {
                continue;
            }

            model = chunk.ModelVersion ?? model;
            completionId = chunk.ResponseId ?? completionId;

            // Taken rather than summed. Whether Gemini's streamed usageMetadata is cumulative
            // or per-chunk is not documented; totalTokenCount being a total rather than a delta
            // implies cumulative, and that is the assumption. MeteringConsistency turns a wrong
            // assumption into a logged discrepancy instead of a wrong invoice.
            usage = GeminiTranslator.ToTokenUsage(chunk.UsageMetadata) ?? usage;

            GeminiCandidate? candidate = chunk.Candidates is { Count: > 0 } c ? c[0] : null;
            finishReason = GeminiTranslator.ToOpenAiFinishReason(candidate?.FinishReason) ?? finishReason;

            string text = GeminiTranslator.ExtractText(candidate);
            if (text.Length == 0)
            {
                // A metadata-only chunk — usage, or a finish reason with no new text.
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
                            Content = text,
                        },
                    },
                ],
            };

            roleSent = true;
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
                    Delta = ChatDelta.Empty,
                    FinishReason = finishReason ?? FinishReasons.Stop,
                },
            ],
            Usage = VetUsage(usage),
        };
    }

    private TokenUsage? VetUsage(TokenUsage? usage) =>
        usage is null
            ? null
            : MeteringConsistency.Vet(usage, discrepancy => _logger.MeteringDiscrepancy(Name, discrepancy));

    private GeminiResponse? ParseChunk(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(payload, GeminiJsonContext.Default.GeminiResponse);
        }
        catch (JsonException ex)
        {
            _logger.UnparseableStreamChunk(Name, ex.Message);
            return null;
        }
    }

    private HttpRequestMessage BuildRequest(ChatCompletionRequest request, ModelRoute route, bool stream)
    {
        GeminiRequest body = GeminiTranslator.ToGeminiRequest(request, Name);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(body, GeminiJsonContext.Default.GeminiRequest);

        // The model goes in the path, and the method is part of it. alt=sse is required for
        // streaming: without it the response is a fragmented JSON array rather than server-sent
        // events, which cannot be read incrementally by an SSE parser.
        string model = Uri.EscapeDataString(route.UpstreamModel);
        string uri = stream
            ? $"v1beta/models/{model}:streamGenerateContent?alt=sse"
            : $"v1beta/models/{model}:generateContent";

        var message = new HttpRequestMessage(HttpMethod.Post, uri)
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
