using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Gatehouse.Providers;
using Gatehouse.Routing;
using Gatehouse.Wire;
using BedrockUsage = Amazon.BedrockRuntime.Model.TokenUsage;
using GatehouseUsage = Gatehouse.Wire.TokenUsage;

namespace Gatehouse.Providers.Bedrock;

/// <summary>
/// Translates between the OpenAI-compatible wire format and the Bedrock Converse API.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Converse rather than InvokeModel.</strong> InvokeModel takes each model family's
/// native payload, so supporting Anthropic, Nova, Llama and Mistral through it means four
/// request shapes, four response shapes and four sets of usage fields — inside what is
/// nominally one provider. Converse is Bedrock's own normalisation of all of that, and using
/// it means a new model family on Bedrock needs no Gatehouse change at all. It is the
/// difference between one provider and a provider registry hiding inside one.
/// </para>
/// <para>
/// The cost is that Converse does not expose model-specific parameters. Callers who need those
/// have the passthrough route, which is recorded as unmetered and says so.
/// </para>
/// </remarks>
internal static class BedrockTranslator
{
    /// <summary>
    /// Builds a Converse request.
    /// </summary>
    /// <param name="request">The client request.</param>
    /// <param name="route">The resolved route; its upstream model is the Bedrock model id.</param>
    public static ConverseRequest ToConverseRequest(ChatCompletionRequest request, ModelRoute route)
    {
        (List<Message> messages, List<SystemContentBlock> system) = SplitMessages(request);

        return new ConverseRequest
        {
            ModelId = route.UpstreamModel,
            Messages = messages,

            // Left null rather than empty when there is no system prompt: Bedrock rejects an
            // empty system list on some model families.
            System = system.Count > 0 ? system : null,
            InferenceConfig = ToInferenceConfiguration(request),
        };
    }

    /// <summary>Builds a streamed Converse request.</summary>
    /// <param name="request">The client request.</param>
    /// <param name="route">The resolved route.</param>
    public static ConverseStreamRequest ToConverseStreamRequest(ChatCompletionRequest request, ModelRoute route)
    {
        (List<Message> messages, List<SystemContentBlock> system) = SplitMessages(request);

        return new ConverseStreamRequest
        {
            ModelId = route.UpstreamModel,
            Messages = messages,
            System = system.Count > 0 ? system : null,
            InferenceConfig = ToInferenceConfiguration(request),
        };
    }

    /// <summary>
    /// Splits the OpenAI message list into Bedrock's separate system and conversation lists.
    /// </summary>
    /// <remarks>
    /// Like Anthropic's native API, Bedrock takes the system prompt out of the conversation.
    /// Several system messages are concatenated in order rather than only the first being kept:
    /// callers legitimately build a system prompt in layers, and silently dropping the later
    /// ones would change the model's instructions without any error.
    /// </remarks>
    private static (List<Message> Messages, List<SystemContentBlock> System) SplitMessages(
        ChatCompletionRequest request)
    {
        List<Message> messages = [];
        List<SystemContentBlock> system = [];

        foreach (ChatMessage message in request.Messages)
        {
            if (string.Equals(message.Role, ChatRoles.System, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(message.Content))
                {
                    system.Add(new SystemContentBlock { Text = message.Content });
                }

                continue;
            }

            if (string.Equals(message.Role, ChatRoles.Tool, StringComparison.OrdinalIgnoreCase))
            {
                // Rejected rather than silently dropped or coerced into a user turn. A tool
                // result quietly relabelled as user input produces a plausible answer to the
                // wrong conversation, which is worse than a clear failure.
                throw new ProviderException(
                    BedrockProvider.ProviderName,
                    "Tool messages are not supported by the Bedrock provider yet. Tool calling "
                    + "arrives with the MCP work in Phase 3.",
                    System.Net.HttpStatusCode.BadRequest,
                    isRetryable: false);
            }

            ConversationRole role =
                string.Equals(message.Role, ChatRoles.Assistant, StringComparison.OrdinalIgnoreCase)
                    ? ConversationRole.Assistant
                    : ConversationRole.User;

            messages.Add(new Message
            {
                Role = role,
                Content = [new ContentBlock { Text = message.Content ?? string.Empty }],
            });
        }

        return (messages, system);
    }

    /// <summary>
    /// Maps the sampling parameters Converse accepts.
    /// </summary>
    /// <remarks>
    /// Returns null when the caller set none, so that Bedrock applies each model's own defaults
    /// rather than Gatehouse inventing them. A gateway that substituted its own default
    /// temperature would change the behaviour of every request that did not specify one.
    /// </remarks>
    private static InferenceConfiguration? ToInferenceConfiguration(ChatCompletionRequest request)
    {
        if (request.Temperature is null
            && request.TopP is null
            && request.MaxTokens is null
            && (request.Stop is null || request.Stop.Count == 0))
        {
            return null;
        }

        return new InferenceConfiguration
        {
            Temperature = request.Temperature,
            TopP = request.TopP,
            MaxTokens = request.MaxTokens,
            StopSequences = request.Stop?.Count > 0 ? [.. request.Stop] : null,
        };
    }

    /// <summary>Extracts the assistant text from a Converse response.</summary>
    public static string ExtractText(ConverseOutput? output)
    {
        List<ContentBlock>? blocks = output?.Message?.Content;

        if (blocks is null || blocks.Count == 0)
        {
            return string.Empty;
        }

        if (blocks.Count == 1)
        {
            return blocks[0].Text ?? string.Empty;
        }

        // Concatenated rather than taking the first. A model that emits several text blocks —
        // reasoning models routinely do — would otherwise have most of its answer discarded,
        // and the response would look like a short completion rather than a truncated one.
        var text = new System.Text.StringBuilder();

        foreach (ContentBlock block in blocks)
        {
            text.Append(block.Text);
        }

        return text.ToString();
    }

    /// <summary>
    /// Maps a Bedrock stop reason to the OpenAI finish reason a client expects.
    /// </summary>
    public static string ToOpenAiFinishReason(StopReason? stopReason)
    {
        if (stopReason is null)
        {
            return FinishReasons.Stop;
        }

        string value = stopReason.Value;

        if (string.Equals(value, StopReason.Max_tokens.Value, StringComparison.OrdinalIgnoreCase))
        {
            return FinishReasons.Length;
        }

        if (string.Equals(value, StopReason.Tool_use.Value, StringComparison.OrdinalIgnoreCase))
        {
            // "tool_calls" is the OpenAI spelling and there is no constant for it yet; the
            // Anthropic translator spells it the same way.
            return "tool_calls";
        }

        if (string.Equals(value, StopReason.Content_filtered.Value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, StopReason.Guardrail_intervened.Value, StringComparison.OrdinalIgnoreCase))
        {
            return FinishReasons.ContentFilter;
        }

        // end_turn, stop_sequence and anything Bedrock adds later all mean "it finished".
        return FinishReasons.Stop;
    }

    /// <summary>
    /// Converts Bedrock's usage report into Gatehouse's, deriving the cache semantics rather
    /// than assuming them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The trap this method exists to avoid: providers disagree about whether cache tokens are
    /// <em>additive</em> to the input count or a <em>subset</em> of it. Anthropic's native API
    /// reports them additively; OpenAI reports them as a subset. Getting it wrong silently
    /// double-counts or under-counts every cached request, and the error only surfaces months
    /// later as a reconciliation variance nobody can explain.
    /// </para>
    /// <para>
    /// Bedrock reports <c>TotalTokens</c> alongside the parts, which makes the question
    /// answerable rather than a matter of trusting documentation: if the total equals input
    /// plus output plus the cache counts, they are additive; if it equals input plus output
    /// alone, they are already inside the input figure. Deriving it from the provider's own
    /// arithmetic means a change in Bedrock's semantics is absorbed rather than silently
    /// mis-metered — which matters here more than elsewhere, because this is the one provider
    /// whose behaviour could not be verified against a live endpoint during development.
    /// </para>
    /// <para>
    /// When <c>TotalTokens</c> is absent, additive is assumed: it is what Bedrock documents,
    /// and <c>MeteringConsistency</c> is the backstop that flags the arithmetic if that is
    /// wrong.
    /// </para>
    /// </remarks>
    public static GatehouseUsage? ToTokenUsage(BedrockUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        int input = usage.InputTokens ?? 0;
        int output = usage.OutputTokens ?? 0;
        int cacheRead = usage.CacheReadInputTokens ?? 0;
        int cacheWrite = usage.CacheWriteInputTokens ?? 0;

        if (cacheRead == 0 && cacheWrite == 0)
        {
            return GatehouseUsage.FromProvider(input, output);
        }

        // The provider's own total decides which convention it is using.
        bool subsetSemantics =
            usage.TotalTokens is { } total && total == input + output;

        return subsetSemantics
            ? GatehouseUsage.FromProvider(input, output, cacheRead, cacheWrite)
            : GatehouseUsage.FromProviderWithAdditiveCache(input, output, cacheRead, cacheWrite);
    }
}
