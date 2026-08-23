using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Gatehouse.Configuration;
using Gatehouse.Routing;
using Gatehouse.Streaming;
using Gatehouse.Wire;
using Microsoft.Extensions.Logging;

namespace Gatehouse.Providers.OpenAI;

/// <summary>
/// Serves completions from any upstream that speaks the OpenAI chat completions API.
/// </summary>
/// <remarks>
/// <para>
/// One implementation covers five of the seven providers in the Phase 1 plan — OpenAI,
/// Azure OpenAI, Ollama, vLLM and Foundry Local all accept this wire format. That is why it
/// is the Phase 0 spike: it exercises the whole path end to end while incurring one
/// provider's worth of maintenance rather than five.
/// </para>
/// <para>
/// The two providers it does not cover, Anthropic and Bedrock, need genuine request
/// translation and arrive in Phase 1 as separate implementations rather than as conditionals
/// bolted onto this one.
/// </para>
/// </remarks>
public sealed class OpenAiCompatibleProvider : IChatProvider
{
    /// <summary>The <c>kind</c> value that selects this provider in configuration.</summary>
    public const string Kind = "openai-compatible";

    private const string ChatCompletionsPath = "chat/completions";

    // Upstream error bodies are echoed to the caller to make debugging possible, but they
    // are attacker-influenced and unbounded. Truncating keeps a hostile or broken upstream
    // from turning every failure into a multi-megabyte log line.
    private const int MaxUpstreamErrorLength = 2048;

    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAiCompatibleProvider> _logger;
    private readonly TimeSpan _timeout;

    /// <summary>Creates a provider bound to one configured upstream.</summary>
    /// <param name="name">The configuration key routes refer to this provider by.</param>
    /// <param name="httpClient">
    /// A client whose base address, credentials and headers have already been applied from
    /// <see cref="ProviderOptions"/> at registration. Its <see cref="HttpClient.Timeout"/>
    /// must be <see cref="Timeout.InfiniteTimeSpan"/> — see <paramref name="timeout"/>.
    /// </param>
    /// <param name="timeout">
    /// How long to wait for the upstream. Applied through a linked cancellation token rather
    /// than through <see cref="HttpClient.Timeout"/>, because the built-in timeout covers the
    /// whole exchange including reading the response body. On a streamed completion that
    /// means a generation which legitimately takes longer than the timeout is aborted
    /// part-way through — the caller is billed for tokens they never receive, and the
    /// failure looks like an upstream fault rather than our own configuration.
    /// </param>
    /// <param name="logger">The logger.</param>
    public OpenAiCompatibleProvider(
        string name,
        HttpClient httpClient,
        TimeSpan timeout,
        ILogger<OpenAiCompatibleProvider> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        Name = name;
        _httpClient = httpClient;
        _timeout = timeout;
        _logger = logger;
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

        // The timeout bounds the whole exchange here, which is what a non-streamed caller
        // wants: there is no partial value in half a buffered completion.
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

        ChatCompletionResponse? parsed = await JsonSerializer.DeserializeAsync(
            body,
            GatehouseJsonContext.Default.ChatCompletionResponse,
            timeoutSource.Token);

        if (parsed is null)
        {
            throw new ProviderException(
                Name,
                "The upstream returned an empty body where a completion was expected.",
                response.StatusCode,
                isRetryable: true);
        }

        // The caller asked for an alias; tell them which provider actually answered so that
        // a routing surprise is visible in the response rather than only in our logs.
        return new ChatCompletionResponse
        {
            Id = parsed.Id,
            ObjectType = parsed.ObjectType,
            Created = parsed.Created,
            Model = parsed.Model,
            Choices = parsed.Choices,

            // Stamped as provider-reported: these counts came off the upstream response, and
            // the flag does not survive JSON. See TokenUsage.AsProviderReported.
            Usage = parsed.Usage?.AsProviderReported(),
            GatehouseProvider = Name,
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

        // For a stream the timeout covers getting response headers only, then it is disarmed.
        // A reasoning model can legitimately take minutes to finish, and a total timeout
        // would abort exactly the long, expensive generations the caller most wants — after
        // they have already been billed for the tokens produced so far.
        using CancellationTokenSource headerTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerTimeout.CancelAfter(_timeout);

        // ResponseHeadersRead is what makes this a stream rather than a slow download.
        // The default, ResponseContentRead, buffers the entire body before returning, which
        // silently converts every streamed completion into a non-streamed one that happens
        // to be delivered in SSE framing.
        using HttpResponseMessage response = await _httpClient
            .SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);

        // Disarm. The linked source stays alive for the duration of the enumeration because
        // HttpClient ties the request to the token it was given, so letting the timer fire
        // later would tear down the connection mid-generation.
        headerTimeout.CancelAfter(Timeout.InfiniteTimeSpan);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateUpstreamExceptionAsync(response, cancellationToken);
        }

        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);

        await foreach (ServerSentEvent sse in ServerSentEventReader.ReadAsync(body, cancellationToken))
        {
            if (sse.IsDone)
            {
                yield break;
            }

            ChatCompletionChunk? chunk = ParseChunk(sse.Data);
            if (chunk is not null)
            {
                yield return chunk;
            }
        }
    }

    /// <summary>
    /// Parses one SSE payload into a chunk, or returns null if it is not one.
    /// </summary>
    /// <remarks>
    /// A malformed chunk is dropped rather than thrown. Mid-stream the status code is
    /// already committed, so throwing would abort a generation the caller is already paying
    /// for because of one bad frame — and providers do occasionally emit non-conforming
    /// keep-alive payloads. The drop is logged so it is diagnosable rather than invisible.
    /// </remarks>
    private ChatCompletionChunk? ParseChunk(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            ChatCompletionChunk? chunk = JsonSerializer.Deserialize(
                payload,
                GatehouseJsonContext.Default.ChatCompletionChunk);

            if (chunk?.Usage is not { } usage)
            {
                return chunk;
            }

            // Rebuilt rather than mutated so the wire types stay immutable. Only the final
            // chunk of a stream carries usage, so this copy happens once per completion.
            return new ChatCompletionChunk
            {
                Id = chunk.Id,
                ObjectType = chunk.ObjectType,
                Created = chunk.Created,
                Model = chunk.Model,
                Choices = chunk.Choices,
                Usage = usage.AsProviderReported(),
            };
        }
        catch (JsonException ex)
        {
            _logger.UpstreamChunkUnparseable(Name, ex.Message);
            return null;
        }
    }

    private static HttpRequestMessage BuildRequest(ChatCompletionRequest request, ModelRoute route, bool stream)
    {
        // The upstream is told the real model name, not the alias. Sending the alias is the
        // classic gateway bug: it works in every test where the two happen to match, and
        // 404s in production the first time an operator points an alias somewhere else.
        var upstreamBody = new ChatCompletionRequest
        {
            Model = route.UpstreamModel,
            Messages = request.Messages,
            Stream = stream,
            Temperature = request.Temperature,
            TopP = request.TopP,
            MaxTokens = request.MaxTokens,
            Stop = request.Stop,
            User = request.User,
        };

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            upstreamBody,
            GatehouseJsonContext.Default.ChatCompletionRequest);

        var message = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsPath)
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
            // The upstream failed and then failed again while we were reading why. There is
            // nothing more to learn here, and the status code alone is still useful.
            return string.Empty;
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
    }
}
