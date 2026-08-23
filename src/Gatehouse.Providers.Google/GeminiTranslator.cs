using System.Text;
using Gatehouse.Providers.Google.Wire;
using Gatehouse.Routing;
using Gatehouse.Wire;
using WireChatMessage = Gatehouse.Wire.ChatMessage;

namespace Gatehouse.Providers.Google;

/// <summary>
/// Translates between the OpenAI-compatible contract and the Gemini generateContent API.
/// </summary>
internal static class GeminiTranslator
{
    /// <summary>
    /// Builds a Gemini request from an OpenAI-compatible one.
    /// </summary>
    /// <exception cref="ProviderException">The request uses a feature Gemini cannot express.</exception>
    public static GeminiRequest ToGeminiRequest(
        ChatCompletionRequest request,
        string providerName)
    {
        ArgumentNullException.ThrowIfNull(request);

        StringBuilder? system = null;
        List<GeminiContent> contents = [];

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
                    contents.Add(NewContent(GeminiRoles.User, message.Content));
                    break;

                case ChatRoles.Assistant:
                    // Gemini calls this turn "model". Sending "assistant" is rejected.
                    contents.Add(NewContent(GeminiRoles.Model, message.Content));
                    break;

                case ChatRoles.Tool:
                    throw new ProviderException(
                        providerName,
                        "Tool messages are not yet supported by the Gemini provider. "
                        + "Tool calling arrives after Phase 1.");

                default:
                    throw new ProviderException(
                        providerName,
                        $"Message role '{message.Role}' is not supported by the Gemini provider.");
            }
        }

        GeminiGenerationConfig? config = null;
        if (request.Temperature is not null
            || request.TopP is not null
            || request.MaxTokens is not null
            || request.Stop is { Count: > 0 })
        {
            config = new GeminiGenerationConfig
            {
                Temperature = request.Temperature,
                TopP = request.TopP,
                MaxOutputTokens = request.MaxTokens,
                StopSequences = request.Stop is { Count: > 0 } stop ? [.. stop] : null,
            };
        }

        return new GeminiRequest
        {
            Contents = contents,

            // No role on the system instruction: Gemini rejects one here.
            SystemInstruction = system is null
                ? null
                : new GeminiContent { Parts = [new GeminiPart { Text = system.ToString() }] },
            GenerationConfig = config,
        };
    }

    /// <summary>
    /// Maps a Gemini finish reason onto the OpenAI vocabulary.
    /// </summary>
    /// <remarks>
    /// <c>SAFETY</c> and <c>RECITATION</c> both become <c>content_filter</c>. Recitation is a
    /// copyright-driven refusal rather than a safety one, but from a caller's perspective both
    /// mean "the model declined to finish", which is the distinction that matters and the one
    /// <c>stop</c> would erase.
    /// </remarks>
    public static string? ToOpenAiFinishReason(string? finishReason) => finishReason switch
    {
        null => null,
        "STOP" => FinishReasons.Stop,
        "MAX_TOKENS" => FinishReasons.Length,
        "SAFETY" => FinishReasons.ContentFilter,
        "RECITATION" => FinishReasons.ContentFilter,
        "PROHIBITED_CONTENT" => FinishReasons.ContentFilter,
        "BLOCKLIST" => FinishReasons.ContentFilter,

        // Lower-cased rather than flattened, so an unrecognised reason stays visible.
        _ => finishReason.ToLowerInvariant(),
    };

    /// <summary>
    /// Converts Gemini usage metadata into Gatehouse's normalised form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two deliberate departures from what the payload says.
    /// </para>
    /// <para>
    /// Thinking tokens are added to the completion count. Gemini bills them as output but
    /// reports them outside <c>candidatesTokenCount</c>, so a provider that forwards only the
    /// candidates count under-bills every request to a thinking model.
    /// </para>
    /// <para>
    /// The total is derived rather than forwarded. Gemini's <c>totalTokenCount</c> includes
    /// thinking tokens, so it does not equal prompt plus candidates — and forwarding it would
    /// make every thinking-model request look inconsistent to
    /// <c>MeteringConsistency</c>. Deriving keeps the invariant true while the thinking tokens
    /// are still counted, because they are now inside the completion figure.
    /// </para>
    /// </remarks>
    public static TokenUsage? ToTokenUsage(GeminiUsageMetadata? usage)
    {
        if (usage is null)
        {
            return null;
        }

        int promptTokens = usage.PromptTokenCount ?? 0;
        int completionTokens = (usage.CandidatesTokenCount ?? 0) + (usage.ThoughtsTokenCount ?? 0);

        return new TokenUsage
        {
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = promptTokens + completionTokens,

            // Gemini reports cached content as a subset of the prompt, like OpenAI and unlike
            // Anthropic. MeteringConsistency asserts that, so if it ever changes we find out.
            CachedPromptTokens = usage.CachedContentTokenCount ?? 0,
            IsProviderReported = true,
        };
    }

    /// <summary>Extracts the text from a candidate, skipping non-text parts.</summary>
    public static string ExtractText(GeminiCandidate? candidate)
    {
        IReadOnlyList<GeminiPart>? parts = candidate?.Content?.Parts;
        if (parts is null || parts.Count == 0)
        {
            return string.Empty;
        }

        if (parts.Count == 1)
        {
            return parts[0].Text ?? string.Empty;
        }

        var builder = new StringBuilder();
        foreach (GeminiPart part in parts)
        {
            if (!string.IsNullOrEmpty(part.Text))
            {
                builder.Append(part.Text);
            }
        }

        return builder.ToString();
    }

    private static GeminiContent NewContent(string role, string? text) => new()
    {
        Role = role,
        Parts = [new GeminiPart { Text = text ?? string.Empty }],
    };
}
