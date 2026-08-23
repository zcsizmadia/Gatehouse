using Gatehouse.Providers;
using Gatehouse.Providers.Google;
using Gatehouse.Providers.Google.Wire;
using Gatehouse.Wire;
using WireChatMessage = Gatehouse.Wire.ChatMessage;

namespace Gatehouse.Providers.Tests.Google;

/// <summary>Tests for OpenAI-to-Gemini translation.</summary>
public class GeminiTranslatorTests
{
    // ---------------------------------------------------------------- roles

    [Test]
    public async Task Renames_the_assistant_role_to_model()
    {
        // Gemini rejects "assistant". This is the kind of mismatch that works in every test
        // with a single user turn and fails the first time a conversation has history.
        GeminiRequest result = Translate(
            new WireChatMessage { Role = ChatRoles.User, Content = "Hi" },
            new WireChatMessage { Role = ChatRoles.Assistant, Content = "Hello" },
            new WireChatMessage { Role = ChatRoles.User, Content = "Again" });

        await Assert.That(result.Contents.Count).IsEqualTo(3);
        await Assert.That(result.Contents[0].Role).IsEqualTo("user");
        await Assert.That(result.Contents[1].Role).IsEqualTo("model");
        await Assert.That(result.Contents[2].Role).IsEqualTo("user");
    }

    [Test]
    public async Task Wraps_message_text_in_a_parts_array()
    {
        GeminiRequest result = Translate(new WireChatMessage { Role = ChatRoles.User, Content = "Hi" });

        await Assert.That(result.Contents[0].Parts.Count).IsEqualTo(1);
        await Assert.That(result.Contents[0].Parts[0].Text).IsEqualTo("Hi");
    }

    // ---------------------------------------------------------------- system instruction

    [Test]
    public async Task Lifts_system_messages_into_the_system_instruction()
    {
        GeminiRequest result = Translate(
            new WireChatMessage { Role = ChatRoles.System, Content = "Be terse." },
            new WireChatMessage { Role = ChatRoles.User, Content = "Hi" });

        await Assert.That(result.SystemInstruction!.Parts[0].Text).IsEqualTo("Be terse.");
        await Assert.That(result.Contents.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Omits_the_role_on_the_system_instruction()
    {
        // Gemini rejects a role on systemInstruction.
        GeminiRequest result = Translate(
            new WireChatMessage { Role = ChatRoles.System, Content = "Be terse." },
            new WireChatMessage { Role = ChatRoles.User, Content = "Hi" });

        await Assert.That(result.SystemInstruction!.Role).IsNull();
    }

    [Test]
    public async Task Omits_the_system_instruction_when_there_is_none()
    {
        GeminiRequest result = Translate(new WireChatMessage { Role = ChatRoles.User, Content = "Hi" });

        await Assert.That(result.SystemInstruction).IsNull();
    }

    // ---------------------------------------------------------------- generation config

    [Test]
    public async Task Maps_sampling_parameters_into_generation_config()
    {
        var request = new ChatCompletionRequest
        {
            Model = "gemini",
            Temperature = 0.4f,
            TopP = 0.8f,
            MaxTokens = 256,
            Stop = ["END"],
            Messages = [new WireChatMessage { Role = ChatRoles.User, Content = "Hi" }],
        };

        GeminiRequest result = GeminiTranslator.ToGeminiRequest(request, "google");

        await Assert.That(result.GenerationConfig!.Temperature).IsEqualTo(0.4f);
        await Assert.That(result.GenerationConfig.TopP).IsEqualTo(0.8f);
        await Assert.That(result.GenerationConfig.MaxOutputTokens).IsEqualTo(256);
        await Assert.That(result.GenerationConfig.StopSequences!.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Omits_generation_config_entirely_when_nothing_was_requested()
    {
        // Sending an empty object is harmless but noisy; omitting it keeps the request
        // identical to what a plain Gemini client would send.
        GeminiRequest result = Translate(new WireChatMessage { Role = ChatRoles.User, Content = "Hi" });

        await Assert.That(result.GenerationConfig).IsNull();
    }

    // ---------------------------------------------------------------- unsupported input

    [Test]
    public async Task Rejects_a_tool_message()
    {
        var request = new ChatCompletionRequest
        {
            Model = "gemini",
            Messages = [new WireChatMessage { Role = ChatRoles.Tool, Content = "{}" }],
        };

        await Assert.That(() => GeminiTranslator.ToGeminiRequest(request, "google"))
            .Throws<ProviderException>();
    }

    // ---------------------------------------------------------------- finish reasons

    [Test]
    [Arguments("STOP", "stop")]
    [Arguments("MAX_TOKENS", "length")]
    [Arguments("SAFETY", "content_filter")]
    [Arguments("RECITATION", "content_filter")]
    [Arguments("PROHIBITED_CONTENT", "content_filter")]
    public async Task Maps_finish_reasons_to_the_openai_vocabulary(string gemini, string expected)
    {
        await Assert.That(GeminiTranslator.ToOpenAiFinishReason(gemini)).IsEqualTo(expected);
    }

    [Test]
    public async Task Lower_cases_an_unrecognised_finish_reason_rather_than_calling_it_stop()
    {
        await Assert.That(GeminiTranslator.ToOpenAiFinishReason("SOME_NEW_REASON"))
            .IsEqualTo("some_new_reason");
    }

    // ---------------------------------------------------------------- usage

    [Test]
    public async Task Counts_thinking_tokens_as_completion_tokens()
    {
        // Gemini bills thinking tokens as output but reports them outside
        // candidatesTokenCount. Forwarding only the candidates count under-bills every request
        // to a thinking model — here by 200 of 250 output tokens.
        TokenUsage? usage = GeminiTranslator.ToTokenUsage(new GeminiUsageMetadata
        {
            PromptTokenCount = 100,
            CandidatesTokenCount = 50,
            ThoughtsTokenCount = 200,
            TotalTokenCount = 350,
        });

        await Assert.That(usage!.CompletionTokens).IsEqualTo(250);
        await Assert.That(usage.PromptTokens).IsEqualTo(100);
    }

    [Test]
    public async Task Derives_the_total_so_the_consistency_invariant_holds()
    {
        // Gemini's own totalTokenCount includes thinking tokens, so it does not equal
        // prompt + candidates. Forwarding it would make every thinking-model request look
        // inconsistent. Deriving keeps the invariant while still counting the thinking tokens,
        // because they are inside the completion figure.
        TokenUsage? usage = GeminiTranslator.ToTokenUsage(new GeminiUsageMetadata
        {
            PromptTokenCount = 100,
            CandidatesTokenCount = 50,
            ThoughtsTokenCount = 200,
            TotalTokenCount = 350,
        });

        await Assert.That(usage!.TotalTokens).IsEqualTo(usage.PromptTokens + usage.CompletionTokens);
        await Assert.That(usage.TotalTokens).IsEqualTo(350);
    }

    [Test]
    public async Task Treats_cached_content_as_a_subset_of_the_prompt()
    {
        // Unlike Anthropic, Gemini reports cached content inside promptTokenCount. If that
        // ever changes, MeteringConsistency catches it because cached would exceed prompt.
        TokenUsage? usage = GeminiTranslator.ToTokenUsage(new GeminiUsageMetadata
        {
            PromptTokenCount = 1000,
            CachedContentTokenCount = 900,
            CandidatesTokenCount = 20,
        });

        await Assert.That(usage!.PromptTokens).IsEqualTo(1000);
        await Assert.That(usage.CachedPromptTokens).IsEqualTo(900);
    }

    [Test]
    public async Task Maps_null_usage_to_null()
    {
        await Assert.That(GeminiTranslator.ToTokenUsage(null)).IsNull();
    }

    // ---------------------------------------------------------------- text extraction

    [Test]
    public async Task Concatenates_multiple_text_parts()
    {
        var candidate = new GeminiCandidate
        {
            Content = new GeminiContent
            {
                Role = "model",
                Parts = [new GeminiPart { Text = "Hello " }, new GeminiPart { Text = "world" }],
            },
        };

        await Assert.That(GeminiTranslator.ExtractText(candidate)).IsEqualTo("Hello world");
    }

    [Test]
    public async Task Returns_empty_for_a_candidate_with_no_content()
    {
        await Assert.That(GeminiTranslator.ExtractText(new GeminiCandidate())).IsEqualTo(string.Empty);
        await Assert.That(GeminiTranslator.ExtractText(null)).IsEqualTo(string.Empty);
    }

    private static GeminiRequest Translate(params WireChatMessage[] messages)
    {
        var request = new ChatCompletionRequest { Model = "gemini", Messages = messages };
        return GeminiTranslator.ToGeminiRequest(request, "google");
    }
}
