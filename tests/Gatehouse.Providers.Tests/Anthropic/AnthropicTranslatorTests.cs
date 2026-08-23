using Gatehouse.Providers;
using Gatehouse.Providers.Anthropic;
using Gatehouse.Providers.Anthropic.Wire;
using Gatehouse.Routing;
using Gatehouse.Wire;
using WireChatMessage = Gatehouse.Wire.ChatMessage;

namespace Gatehouse.Providers.Tests.Anthropic;

/// <summary>
/// Tests for OpenAI-to-Anthropic translation.
/// </summary>
/// <remarks>
/// Every failure mode covered here is silent in production: a system prompt dropped instead of
/// lifted, a moderation event reported as a clean stop, or a cached prompt under-counted by an
/// order of magnitude. None of them throws, and none is visible in a response body.
/// </remarks>
public class AnthropicTranslatorTests
{
    private static readonly ModelRoute Route = new()
    {
        Alias = "claude",
        Provider = "anthropic",
        UpstreamModel = "claude-sonnet-5",
    };

    // ---------------------------------------------------------------- system prompt

    [Test]
    public async Task Lifts_the_system_message_into_the_top_level_field()
    {
        // Anthropic rejects "role": "system" inside messages. Leaving it there is a 400; and
        // silently dropping it changes the model's behaviour with no error at all.
        AnthropicRequest result = Translate(
            new WireChatMessage { Role = ChatRoles.System, Content = "You are terse." },
            new WireChatMessage { Role = ChatRoles.User, Content = "Hello" });

        await Assert.That(result.System).IsEqualTo("You are terse.");
        await Assert.That(result.Messages.Count).IsEqualTo(1);
        await Assert.That(result.Messages[0].Role).IsEqualTo(ChatRoles.User);
    }

    [Test]
    public async Task Joins_multiple_system_messages()
    {
        // OpenAI allows several system messages anywhere in the conversation; Anthropic takes
        // one string. Keeping only the first or last would drop instructions the caller sent.
        AnthropicRequest result = Translate(
            new WireChatMessage { Role = ChatRoles.System, Content = "Be terse." },
            new WireChatMessage { Role = ChatRoles.User, Content = "Hi" },
            new WireChatMessage { Role = ChatRoles.System, Content = "Answer in French." });

        await Assert.That(result.System).IsEqualTo("Be terse.\n\nAnswer in French.");
    }

    [Test]
    public async Task Omits_the_system_field_when_there_is_no_system_message()
    {
        AnthropicRequest result = Translate(new WireChatMessage { Role = ChatRoles.User, Content = "Hi" });

        await Assert.That(result.System).IsNull();
    }

    [Test]
    public async Task Ignores_an_empty_system_message()
    {
        AnthropicRequest result = Translate(
            new WireChatMessage { Role = ChatRoles.System, Content = "" },
            new WireChatMessage { Role = ChatRoles.User, Content = "Hi" });

        await Assert.That(result.System).IsNull();
    }

    // ---------------------------------------------------------------- required fields

    [Test]
    public async Task Supplies_a_default_max_tokens_because_anthropic_requires_it()
    {
        // OpenAI treats max_tokens as optional; Anthropic rejects a request without it.
        AnthropicRequest result = Translate(new WireChatMessage { Role = ChatRoles.User, Content = "Hi" });

        await Assert.That(result.MaxTokens).IsEqualTo(AnthropicTranslator.DefaultMaxTokens);
    }

    [Test]
    public async Task Honours_a_caller_supplied_max_tokens()
    {
        var request = new ChatCompletionRequest
        {
            Model = "claude",
            MaxTokens = 128,
            Messages = [new WireChatMessage { Role = ChatRoles.User, Content = "Hi" }],
        };

        AnthropicRequest result = AnthropicTranslator.ToAnthropicRequest(request, Route, "anthropic", stream: false);

        await Assert.That(result.MaxTokens).IsEqualTo(128);
    }

    [Test]
    public async Task Sends_the_upstream_model_not_the_alias()
    {
        AnthropicRequest result = Translate(new WireChatMessage { Role = ChatRoles.User, Content = "Hi" });

        await Assert.That(result.Model).IsEqualTo("claude-sonnet-5");
    }

    [Test]
    public async Task Forwards_sampling_parameters()
    {
        var request = new ChatCompletionRequest
        {
            Model = "claude",
            Temperature = 0.3f,
            TopP = 0.9f,
            Stop = ["END"],
            Messages = [new WireChatMessage { Role = ChatRoles.User, Content = "Hi" }],
        };

        AnthropicRequest result = AnthropicTranslator.ToAnthropicRequest(request, Route, "anthropic", stream: true);

        await Assert.That(result.Temperature).IsEqualTo(0.3f);
        await Assert.That(result.TopP).IsEqualTo(0.9f);
        await Assert.That(result.StopSequences!.Count).IsEqualTo(1);
        await Assert.That(result.Stream).IsTrue();
    }

    // ---------------------------------------------------------------- unsupported input

    [Test]
    public async Task Rejects_a_tool_message_rather_than_coercing_it()
    {
        // Forwarding a tool result as a user message would let the request succeed while
        // telling the model something different from what the caller sent.
        var request = new ChatCompletionRequest
        {
            Model = "claude",
            Messages = [new WireChatMessage { Role = ChatRoles.Tool, Content = "{\"result\":42}" }],
        };

        await Assert.That(() => AnthropicTranslator.ToAnthropicRequest(request, Route, "anthropic", stream: false))
            .Throws<ProviderException>();
    }

    // ---------------------------------------------------------------- finish reasons

    [Test]
    [Arguments("end_turn", "stop")]
    [Arguments("stop_sequence", "stop")]
    [Arguments("max_tokens", "length")]
    [Arguments("tool_use", "tool_calls")]
    [Arguments("refusal", "content_filter")]
    public async Task Maps_stop_reasons_to_the_openai_vocabulary(string anthropic, string expected)
    {
        await Assert.That(AnthropicTranslator.ToOpenAiFinishReason(anthropic)).IsEqualTo(expected);
    }

    [Test]
    public async Task Passes_an_unknown_stop_reason_through_rather_than_calling_it_stop()
    {
        // Flattening an unrecognised outcome to "stop" tells the caller the answer completed
        // normally when it may not have.
        await Assert.That(AnthropicTranslator.ToOpenAiFinishReason("some_future_reason"))
            .IsEqualTo("some_future_reason");
    }

    [Test]
    public async Task Maps_a_null_stop_reason_to_null()
    {
        await Assert.That(AnthropicTranslator.ToOpenAiFinishReason(null)).IsNull();
    }

    // ---------------------------------------------------------------- usage: the important part

    [Test]
    public async Task Sums_the_three_input_categories_into_the_prompt_count()
    {
        // The single most consequential assertion in this file.
        //
        // Anthropic's input_tokens counts only the tokens after the last cache breakpoint:
        //   total_input = cache_read + cache_creation + input
        // Mapping input_tokens straight onto PromptTokens under-reports the prompt by the
        // whole cached portion — here by 900 of 1000 tokens.
        TokenUsage? usage = AnthropicTranslator.ToTokenUsage(new AnthropicUsage
        {
            InputTokens = 100,
            CacheReadInputTokens = 800,
            CacheCreationInputTokens = 100,
            OutputTokens = 50,
        });

        await Assert.That(usage!.PromptTokens).IsEqualTo(1000);
        await Assert.That(usage.CachedPromptTokens).IsEqualTo(800);
        await Assert.That(usage.CacheCreationTokens).IsEqualTo(100);
        await Assert.That(usage.CompletionTokens).IsEqualTo(50);
        await Assert.That(usage.TotalTokens).IsEqualTo(1050);
        await Assert.That(usage.IsProviderReported).IsTrue();
    }

    [Test]
    public async Task Keeps_cache_reads_and_writes_distinct()
    {
        // Reads bill at 0.1x and writes at 1.25x or 2x. Collapsing them into one number
        // misprices a cache-warming request by more than an order of magnitude.
        TokenUsage? usage = AnthropicTranslator.ToTokenUsage(new AnthropicUsage
        {
            InputTokens = 0,
            CacheReadInputTokens = 0,
            CacheCreationInputTokens = 500,
            OutputTokens = 10,
        });

        await Assert.That(usage!.CacheCreationTokens).IsEqualTo(500);
        await Assert.That(usage.CachedPromptTokens).IsEqualTo(0);
    }

    [Test]
    public async Task Treats_absent_usage_fields_as_zero()
    {
        TokenUsage? usage = AnthropicTranslator.ToTokenUsage(new AnthropicUsage { InputTokens = 5 });

        await Assert.That(usage!.PromptTokens).IsEqualTo(5);
        await Assert.That(usage.CompletionTokens).IsEqualTo(0);
    }

    [Test]
    public async Task Maps_null_usage_to_null()
    {
        await Assert.That(AnthropicTranslator.ToTokenUsage(null)).IsNull();
    }

    // ---------------------------------------------------------------- cumulative merge

    [Test]
    public async Task Replaces_rather_than_sums_because_streamed_usage_is_cumulative()
    {
        // Anthropic's message_delta reports a running total. Adding successive reports
        // over-counts every streamed completion, and the documentation warns about it
        // explicitly. 5 then 15 must yield 15, not 20.
        TokenUsage first = TokenUsage.FromProviderWithAdditiveCache(100, 5, 0, 0);
        TokenUsage second = TokenUsage.FromProviderWithAdditiveCache(0, 15, 0, 0);

        TokenUsage merged = AnthropicTranslator.MergeCumulative(first, second);

        await Assert.That(merged.CompletionTokens).IsEqualTo(15);
    }

    [Test]
    public async Task Keeps_the_input_side_when_a_later_report_omits_it()
    {
        // message_start carries the input and cache counts; message_delta often reports only
        // output_tokens. Replacing the record wholesale would discard the prompt count and
        // bill the request as though it had no input at all.
        TokenUsage fromMessageStart = TokenUsage.FromProviderWithAdditiveCache(
            uncachedPromptTokens: 200,
            completionTokens: 1,
            cacheReadTokens: 300,
            cacheCreationTokens: 0);

        TokenUsage fromMessageDelta = TokenUsage.FromProviderWithAdditiveCache(0, 87, 0, 0);

        TokenUsage merged = AnthropicTranslator.MergeCumulative(fromMessageStart, fromMessageDelta);

        await Assert.That(merged.PromptTokens).IsEqualTo(500);
        await Assert.That(merged.CachedPromptTokens).IsEqualTo(300);
        await Assert.That(merged.CompletionTokens).IsEqualTo(87);
        await Assert.That(merged.TotalTokens).IsEqualTo(587);
    }

    [Test]
    public async Task Returns_the_later_report_when_there_is_no_earlier_one()
    {
        TokenUsage only = TokenUsage.FromProviderWithAdditiveCache(10, 2, 0, 0);

        await Assert.That(AnthropicTranslator.MergeCumulative(null, only)).IsEqualTo(only);
    }

    private static AnthropicRequest Translate(params WireChatMessage[] messages)
    {
        var request = new ChatCompletionRequest { Model = "claude", Messages = messages };
        return AnthropicTranslator.ToAnthropicRequest(request, Route, "anthropic", stream: false);
    }
}
