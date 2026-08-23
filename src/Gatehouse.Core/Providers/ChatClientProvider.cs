using System.Runtime.CompilerServices;
using Gatehouse.Routing;
using Gatehouse.Wire;
using Microsoft.Extensions.AI;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using WireChatMessage = Gatehouse.Wire.ChatMessage;

namespace Gatehouse.Providers;

/// <summary>
/// Adapts any <see cref="IChatClient"/> into a Gatehouse provider.
/// </summary>
/// <remarks>
/// <para>
/// <c>Microsoft.Extensions.AI</c> already defines a provider-neutral chat abstraction that
/// the .NET ecosystem is standardising on, and a growing set of clients implement it. Rather
/// than duplicating that work, Gatehouse consumes it: anything with an
/// <see cref="IChatClient"/> can be wired in as a provider without writing a Gatehouse
/// provider at all.
/// </para>
/// <para>
/// This does not make the hand-written providers redundant. <see cref="IChatClient"/> is a
/// lowest common denominator by design, and the fields it does not model — cached prompt
/// tokens, provider-specific finish reasons, the raw usage block finance needs for invoice
/// reconciliation — are exactly the ones a governance gateway cannot afford to lose. The
/// rule is therefore: the seven first-class providers are hand-written; this adapter is the
/// escape hatch for everything else.
/// </para>
/// </remarks>
public sealed class ChatClientProvider : IChatProvider
{
    private readonly IChatClient _client;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a provider over an <see cref="IChatClient"/>.</summary>
    /// <param name="name">The configuration key routes refer to this provider by.</param>
    /// <param name="client">The underlying client.</param>
    /// <param name="timeProvider">Clock used for response timestamps.</param>
    public ChatClientProvider(string name, IChatClient client, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(timeProvider);

        Name = name;
        _client = client;
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

        ChatResponse response = await _client.GetResponseAsync(
            ToAiMessages(request.Messages),
            ToChatOptions(request, route),
            cancellationToken);

        string id = response.ResponseId ?? NewCompletionId();
        long created = _timeProvider.GetUtcNow().ToUnixTimeSeconds();

        return new ChatCompletionResponse
        {
            Id = id,
            Created = created,
            Model = response.ModelId ?? route.UpstreamModel,
            GatehouseProvider = Name,
            Choices =
            [
                new ChatChoice
                {
                    Index = 0,
                    Message = new WireChatMessage
                    {
                        Role = ChatRoles.Assistant,
                        Content = response.Text,
                    },
                    FinishReason = ToFinishReason(response.FinishReason),
                },
            ],
            Usage = ToTokenUsage(response.Usage),
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

        string id = NewCompletionId();
        long created = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        bool roleSent = false;

        await foreach (ChatResponseUpdate update in _client
            .GetStreamingResponseAsync(ToAiMessages(request.Messages), ToChatOptions(request, route), cancellationToken))
        {
            string? finishReason = ToFinishReason(update.FinishReason);

            // An update with neither text nor a finish reason carries only metadata. Passing
            // it through as an empty delta would be harmless for most clients and confusing
            // for the rest, so it is dropped rather than forwarded.
            if (string.IsNullOrEmpty(update.Text) && finishReason is null)
            {
                continue;
            }

            yield return new ChatCompletionChunk
            {
                Id = update.ResponseId ?? id,
                Created = created,
                Model = update.ModelId ?? route.UpstreamModel,
                Choices =
                [
                    new ChatChunkChoice
                    {
                        Index = 0,
                        Delta = new ChatDelta
                        {
                            // The OpenAI wire format puts the role on the first chunk only.
                            Role = roleSent ? null : ChatRoles.Assistant,
                            Content = string.IsNullOrEmpty(update.Text) ? null : update.Text,
                        },
                        FinishReason = finishReason,
                    },
                ],
            };

            roleSent = true;
        }
    }

    private static IEnumerable<AIChatMessage> ToAiMessages(IReadOnlyList<WireChatMessage> messages)
    {
        foreach (WireChatMessage message in messages)
        {
            yield return new AIChatMessage(ToChatRole(message.Role), message.Content ?? string.Empty)
            {
                AuthorName = message.Name,
            };
        }
    }

    private static ChatRole ToChatRole(string role) => role switch
    {
        ChatRoles.System => ChatRole.System,
        ChatRoles.Assistant => ChatRole.Assistant,
        ChatRoles.Tool => ChatRole.Tool,
        ChatRoles.User => ChatRole.User,

        // An unrecognised role is forwarded rather than rejected. Providers add roles, and
        // failing a request because Gatehouse has not been updated yet would make the
        // gateway the reason a working model became unusable.
        _ => new ChatRole(role),
    };

    private static ChatOptions ToChatOptions(ChatCompletionRequest request, ModelRoute route) => new()
    {
        ModelId = route.UpstreamModel,
        Temperature = request.Temperature,
        TopP = request.TopP,
        MaxOutputTokens = request.MaxTokens,
        StopSequences = request.Stop is { Count: > 0 } stop ? [.. stop] : null,
    };

    private static string? ToFinishReason(ChatFinishReason? reason)
    {
        if (reason is not { } value)
        {
            return null;
        }

        // ChatFinishReason is an open set, so map the ones the OpenAI wire format defines
        // and pass anything else through verbatim rather than flattening it to "stop".
        // A caller that sees "content_filter" behaves differently from one that sees "stop",
        // and silently conflating them hides moderation events.
        if (value == ChatFinishReason.Stop)
        {
            return FinishReasons.Stop;
        }

        if (value == ChatFinishReason.Length)
        {
            return FinishReasons.Length;
        }

        if (value == ChatFinishReason.ContentFilter)
        {
            return FinishReasons.ContentFilter;
        }

        return value.Value;
    }

    private static TokenUsage? ToTokenUsage(UsageDetails? usage)
    {
        if (usage is null)
        {
            return null;
        }

        return TokenUsage.FromProvider(
            promptTokens: (int)(usage.InputTokenCount ?? 0),
            completionTokens: (int)(usage.OutputTokenCount ?? 0));
    }

    private static string NewCompletionId() => $"chatcmpl-{Guid.NewGuid():N}";
}
