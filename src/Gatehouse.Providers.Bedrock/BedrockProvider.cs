using System.Net;
using System.Runtime.CompilerServices;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Amazon.Runtime.EventStreams;
using Gatehouse.Metering;
using Gatehouse.Providers;
using Gatehouse.Routing;
using Gatehouse.Wire;
using Microsoft.Extensions.Logging;
using GatehouseUsage = Gatehouse.Wire.TokenUsage;

namespace Gatehouse.Providers.Bedrock;

/// <summary>
/// Serves chat completions from Amazon Bedrock through the Converse API.
/// </summary>
/// <remarks>
/// <para>
/// The one provider in Gatehouse built on a vendor SDK, and the reasoning is recorded in
/// <see href="https://github.com/zcsizmadia/Gatehouse/blob/main/docs/adr/0002-provider-integration.md">
/// ADR 0002</see>. Two things make it the exception: the SDK is trim-annotated, so the
/// NativeAOT gate still passes — verified by publishing, not assumed — and it removes
/// hand-rolled SigV4 request signing, which is the one piece of provider plumbing where a
/// subtle mistake produces intermittent authentication failures rather than an obvious bug.
/// </para>
/// <para>
/// <strong>Credentials are never stored by Gatehouse.</strong> The default path is the AWS
/// credential chain, which on EC2, ECS or EKS resolves an IAM role — the same shape as Entra
/// managed identity for Azure, and for the same reason: there is no key to rotate, leak, or
/// find in a backup.
/// </para>
/// </remarks>
public sealed class BedrockProvider : IChatProvider, IDisposable
{
    /// <summary>The configuration name that binds a route to this provider.</summary>
    public const string ProviderName = "amazon-bedrock";

    private readonly IAmazonBedrockRuntime _client;
    private readonly ILogger<BedrockProvider> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly bool _ownsClient;

    /// <summary>Creates the provider.</summary>
    /// <param name="client">The Bedrock client.</param>
    /// <param name="logger">The log.</param>
    /// <param name="timeProvider">The clock, for response timestamps.</param>
    /// <param name="ownsClient">
    /// Whether disposing this provider should dispose the client. False when the container owns
    /// the client's lifetime, which is the normal case.
    /// </param>
    public BedrockProvider(
        IAmazonBedrockRuntime client,
        ILogger<BedrockProvider> logger,
        TimeProvider timeProvider,
        bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _client = client;
        _logger = logger;
        _timeProvider = timeProvider;
        _ownsClient = ownsClient;
    }

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public async Task<ChatCompletionResponse> CompleteAsync(
        ChatCompletionRequest request,
        ModelRoute route,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(route);

        ConverseRequest converse = BedrockTranslator.ToConverseRequest(request, route);

        ConverseResponse response;
        try
        {
            response = await _client.ConverseAsync(converse, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonBedrockRuntimeException ex)
        {
            throw Translate(ex, route);
        }
        catch (AmazonServiceException ex)
        {
            throw Translate(ex, route);
        }

        GatehouseUsage? usage = BedrockTranslator.ToTokenUsage(response.Usage);
        MeteringConsistency.Vet(usage, discrepancy => _logger.UsageInconsistent(route.UpstreamModel, discrepancy));

        return new ChatCompletionResponse
        {
            Id = $"chatcmpl-{Guid.NewGuid():N}",
            Created = _timeProvider.GetUtcNow().ToUnixTimeSeconds(),
            Model = route.UpstreamModel,
            GatehouseProvider = ProviderName,
            Usage = usage,
            Choices =
            [
                new ChatChoice
                {
                    Index = 0,
                    Message = new ChatMessage
                    {
                        Role = ChatRoles.Assistant,
                        Content = BedrockTranslator.ExtractText(response.Output),
                    },
                    FinishReason = BedrockTranslator.ToOpenAiFinishReason(response.StopReason),
                },
            ],
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Bedrock's event stream is exposed as an <see cref="IAsyncEnumerable{T}"/>, which is what
    /// makes this provider viable at all: the SDK also offers a synchronous enumerator and an
    /// event-callback API, and either would pin a thread per in-flight completion. On a gateway
    /// holding hundreds of concurrent streams that is thread-pool starvation, and it presents
    /// as the whole process going slow rather than as anything to do with Bedrock.
    /// </para>
    /// <para>
    /// Usage arrives on a metadata event which Bedrock sends <em>after</em> the content is
    /// finished, so it is attached to a final chunk emitted once the stream ends — the same
    /// shape OpenAI uses, so a client needs no special handling.
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<ChatCompletionChunk> StreamAsync(
        ChatCompletionRequest request,
        ModelRoute route,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(route);

        ConverseStreamRequest converse = BedrockTranslator.ToConverseStreamRequest(request, route);

        ConverseStreamResponse response;
        try
        {
            response = await _client.ConverseStreamAsync(converse, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonBedrockRuntimeException ex)
        {
            throw Translate(ex, route);
        }
        catch (AmazonServiceException ex)
        {
            throw Translate(ex, route);
        }

        using (response)
        {
            IAsyncEnumerable<ChatCompletionChunk> chunks = TranslateEvents(
                response.Stream,
                route,
                $"chatcmpl-{Guid.NewGuid():N}",
                _timeProvider.GetUtcNow().ToUnixTimeSeconds(),
                discrepancy => _logger.UsageInconsistent(route.UpstreamModel, discrepancy),
                cancellationToken);

            await foreach (ChatCompletionChunk chunk in chunks.ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
    }

    /// <summary>
    /// Turns Bedrock's event stream into OpenAI-compatible chunks.
    /// </summary>
    /// <remarks>
    /// Split out from <see cref="StreamAsync"/> so it can be tested. The SDK's
    /// <c>ConverseStreamOutput</c> wraps a live socket and cannot reasonably be constructed in a
    /// test, but the events it yields are ordinary model objects — so taking the sequence as a
    /// parameter makes the part with the actual logic in it reachable, rather than leaving the
    /// event handling covered only by whatever an integration test happens to exercise.
    /// </remarks>
    /// <param name="events">The Bedrock event sequence.</param>
    /// <param name="route">The resolved route.</param>
    /// <param name="id">The completion id to stamp on every chunk.</param>
    /// <param name="created">The creation timestamp to stamp on every chunk.</param>
    /// <param name="onDiscrepancy">Called when the reported usage does not add up.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    internal static async IAsyncEnumerable<ChatCompletionChunk> TranslateEvents(
        IAsyncEnumerable<IEventStreamEvent> events,
        ModelRoute route,
        string id,
        long created,
        Action<string> onDiscrepancy,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        GatehouseUsage? usage = null;
        string? finishReason = null;

        await foreach (IEventStreamEvent evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            switch (evt)
            {
                case ContentBlockDeltaEvent delta when delta.Delta?.Text is { Length: > 0 } text:
                    // Yielded immediately. Accumulating even a couple of these to simplify the
                    // accounting below would convert a responsive stream into a stuttering one,
                    // and no test that asserts on the concatenated text would catch it.
                    yield return Chunk(id, created, route, new ChatDelta { Content = text });
                    break;

                case MessageStopEvent stop:
                    finishReason = BedrockTranslator.ToOpenAiFinishReason(stop.StopReason);
                    break;

                case ConverseStreamMetadataEvent metadata:
                    usage = BedrockTranslator.ToTokenUsage(metadata.Usage);
                    break;

                default:
                    // MessageStart, ContentBlockStart and ContentBlockStop carry no text and no
                    // accounting. Ignored rather than treated as an error, so a new event type
                    // from Bedrock does not break the stream.
                    break;
            }
        }

        MeteringConsistency.Vet(usage, onDiscrepancy);

        // The final chunk: an empty delta carrying the finish reason and the usage, which is
        // where an OpenAI client expects to find both.
        yield return Chunk(id, created, route, ChatDelta.Empty, finishReason ?? FinishReasons.Stop, usage);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private static ChatCompletionChunk Chunk(
        string id,
        long created,
        ModelRoute route,
        ChatDelta delta,
        string? finishReason = null,
        GatehouseUsage? usage = null) => new()
    {
        Id = id,
        Created = created,
        Model = route.UpstreamModel,
        Usage = usage,
        Choices =
        [
            new ChatChunkChoice
            {
                Index = 0,
                Delta = delta,
                FinishReason = finishReason,
            },
        ],
    };

    /// <summary>
    /// Maps an AWS exception onto the retry classification the fallback chain reads.
    /// </summary>
    /// <remarks>
    /// The SDK already distinguishes throttling and transient service faults from the rest, so
    /// this consults <see cref="AmazonServiceException.StatusCode"/> and the SDK's own retry
    /// hint rather than matching on message text. The classification matters: retrying a
    /// validation error bills a second provider to produce the same rejection, and failing to
    /// retry a throttle hands the caller an outage Bedrock did not have.
    /// </remarks>
    private static ProviderException Translate(AmazonServiceException exception, ModelRoute route)
    {
        HttpStatusCode? status = exception.StatusCode == 0 ? null : exception.StatusCode;

        bool retryable = exception.Retryable is not null
            || (status is { } code && ProviderException.IsRetryableStatus(code));

        // The AWS message names the model and the operation, and never contains the
        // credential — it is a signature, not a secret, and the SDK does not echo it.
        return new ProviderException(
            ProviderName,
            $"Bedrock rejected the request for model '{route.UpstreamModel}': {exception.Message}",
            status,
            retryable,
            exception);
    }
}
