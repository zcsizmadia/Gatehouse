using System.Text;
using Gatehouse.Providers.Anthropic.Wire;
using Gatehouse.Routing;
using Gatehouse.Wire;
using WireChatMessage = Gatehouse.Wire.ChatMessage;

namespace Gatehouse.Providers.Anthropic;

/// <summary>
/// Translates between the OpenAI-compatible contract and the Anthropic Messages API.
/// </summary>
/// <remarks>
/// Kept free of I/O so the translation rules can be tested directly. The rules are where the
/// provider-specific mistakes live, and they are all silent when wrong: a mis-mapped stop
/// reason hides a content-filter event, and a mis-summed usage record produces a plausible
/// but incorrect invoice line.
/// </remarks>
internal static class AnthropicTranslator
{
    /// <summary>
    /// The <c>max_tokens</c> used when the caller does not specify one.
    /// </summary>
    /// <remarks>
    /// Anthropic requires the field; OpenAI does not. Something has to be chosen, and the
    /// choice is a trade: too low silently truncates long answers, too high has no cost but
    /// removes a guard rail. 4096 is generous for chat while still bounded, and an operator who
    /// needs more can set it per request.
    /// </remarks>
    public const int DefaultMaxTokens = 4096;

    /// <summary>
    /// Builds an Anthropic request from an OpenAI-compatible one.
    /// </summary>
    /// <exception cref="ProviderException">
    /// The request uses a feature Anthropic cannot express, such as a tool-result message.
    /// </exception>
    public static AnthropicRequest ToAnthropicRequest(
        ChatCompletionRequest request,
        ModelRoute route,
        string providerName,
        bool stream)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(route);

        // System messages are lifted out of the array and concatenated. Anthropic takes a
        // single top-level system prompt, whereas OpenAI permits several system messages
        // anywhere in the conversation; joining them preserves the instructions rather than
        // silently keeping only one.
        StringBuilder? system = null;
        List<AnthropicMessageParam> messages = [];

        foreach (WireChatMessage message in request.Messages)
        {
            switch (message.Role)
            {
                case ChatRoles.System:
                    if (!string.IsNullOrEmpty(message.Content))
                    {
                        system ??= new StringBuilder();
                        if (system.Length > 0)
                        {
                            system.Append("\n\n");
                        }

                        system.Append(message.Content);
                    }

                    break;

                case ChatRoles.User:
                case ChatRoles.Assistant:
                    messages.Add(new AnthropicMessageParam
                    {
                        Role = message.Role,
                        Content = message.Content ?? string.Empty,
                    });
                    break;

                case ChatRoles.Tool:
                    // Rejected rather than coerced. Forwarding a tool result as a user message
                    // would let the request succeed while telling the model something
                    // materially different from what the caller sent, and the caller would have
                    // no way to know. Tool calling is a post-Phase-1 concern.
                    throw new ProviderException(
                        providerName,
                        "Tool messages are not yet supported by the Anthropic provider. "
                        + "Tool calling arrives after Phase 1.");

                default:
                    throw new ProviderException(
                        providerName,
                        $"Message role '{message.Role}' is not supported by the Anthropic provider.");
            }
        }

        return new AnthropicRequest
        {
            Model = route.UpstreamModel,
            MaxTokens = request.MaxTokens ?? DefaultMaxTokens,
            System = system?.ToString(),
            Messages = messages,
            Stream = stream,
            Temperature = request.Temperature,
            TopP = request.TopP,
            StopSequences = request.Stop is { Count: > 0 } stop ? [.. stop] : null,
        };
    }

    /// <summary>
    /// Maps an Anthropic stop reason onto the OpenAI vocabulary.
    /// </summary>
    /// <remarks>
    /// <c>refusal</c> and any future safety-related reason map to <c>content_filter</c> rather
    /// than <c>stop</c>. A caller that sees <c>stop</c> believes it received a complete answer;
    /// flattening a moderation event into it hides the one outcome an application most needs to
    /// branch on.
    /// </remarks>
    public static string? ToOpenAiFinishReason(string? stopReason) => stopReason switch
    {
        null => null,
        "end_turn" => FinishReasons.Stop,
        "stop_sequence" => FinishReasons.Stop,
        "max_tokens" => FinishReasons.Length,
        "tool_use" => "tool_calls",
        "refusal" => FinishReasons.ContentFilter,

        // Unknown reasons are passed through rather than flattened to "stop", so a new
        // Anthropic outcome is visible to the caller instead of being misreported as success.
        _ => stopReason,
    };

    /// <summary>
    /// Converts Anthropic usage into Gatehouse's normalised, subset-semantics form.
    /// </summary>
    /// <remarks>
    /// The important line is the one that sums three fields. Anthropic's <c>input_tokens</c>
    /// excludes both cache figures, so the billable prompt is
    /// <c>input + cache_read + cache_creation</c>. Using <c>input_tokens</c> alone under-reports
    /// the prompt by the whole cached portion, which on a cache-heavy workload is most of it.
    /// </remarks>
    public static TokenUsage? ToTokenUsage(AnthropicUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        return TokenUsage.FromProviderWithAdditiveCache(
            uncachedPromptTokens: usage.InputTokens ?? 0,
            completionTokens: usage.OutputTokens ?? 0,
            cacheReadTokens: usage.CacheReadInputTokens ?? 0,
            cacheCreationTokens: usage.CacheCreationInputTokens ?? 0);
    }

    /// <summary>
    /// Merges a later usage report over an earlier one, using replace-not-add semantics.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Anthropic's streamed usage is <strong>cumulative</strong>: every <c>message_delta</c>
    /// reports the running total, not an increment. Adding them together over-counts every
    /// streamed completion, and the documentation warns about this explicitly.
    /// </para>
    /// <para>
    /// So each field takes the later non-zero value rather than a sum. Field-wise rather than
    /// wholesale because <c>message_start</c> carries the input and cache counts while
    /// <c>message_delta</c> may report only <c>output_tokens</c> — replacing the whole record
    /// would discard the input side.
    /// </para>
    /// </remarks>
    public static TokenUsage MergeCumulative(TokenUsage? earlier, TokenUsage later)
    {
        ArgumentNullException.ThrowIfNull(later);

        if (earlier is null)
        {
            return later;
        }

        int promptTokens = later.PromptTokens > 0 ? later.PromptTokens : earlier.PromptTokens;
        int completionTokens = later.CompletionTokens > 0 ? later.CompletionTokens : earlier.CompletionTokens;

        return new TokenUsage
        {
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = promptTokens + completionTokens,
            CachedPromptTokens = later.CachedPromptTokens > 0 ? later.CachedPromptTokens : earlier.CachedPromptTokens,
            CacheCreationTokens = later.CacheCreationTokens > 0 ? later.CacheCreationTokens : earlier.CacheCreationTokens,
            IsProviderReported = true,
        };
    }
}
