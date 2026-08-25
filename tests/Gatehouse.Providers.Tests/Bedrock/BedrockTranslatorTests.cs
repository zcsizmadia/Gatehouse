using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Gatehouse.Providers;
using Gatehouse.Providers.Bedrock;
using Gatehouse.Routing;
using Gatehouse.Wire;
using BedrockUsage = Amazon.BedrockRuntime.Model.TokenUsage;
using GatehouseUsage = Gatehouse.Wire.TokenUsage;

namespace Gatehouse.Providers.Tests.Bedrock;

/// <summary>Tests for translating between the OpenAI wire format and Bedrock Converse.</summary>
public class BedrockTranslatorTests
{
    private static readonly ModelRoute Route = new()
    {
        Alias = "claude",
        Provider = "amazon-bedrock",
        UpstreamModel = "anthropic.claude-sonnet-4-20250514-v1:0",
    };

    [Test]
    public async Task Sends_the_upstream_model_as_the_bedrock_model_id()
    {
        ConverseRequest converse = BedrockTranslator.ToConverseRequest(Request(), Route);

        await Assert.That(converse.ModelId).IsEqualTo("anthropic.claude-sonnet-4-20250514-v1:0");
    }

    [Test]
    public async Task Lifts_system_messages_out_of_the_conversation()
    {
        // Bedrock takes the system prompt as a separate field, like Anthropic's native API.
        // Leaving it in the message list makes the model treat instructions as dialogue.
        ChatCompletionRequest request = Request(
            new ChatMessage { Role = ChatRoles.System, Content = "Be terse." },
            new ChatMessage { Role = ChatRoles.User, Content = "hello" });

        ConverseRequest converse = BedrockTranslator.ToConverseRequest(request, Route);

        await Assert.That(converse.System!.Count).IsEqualTo(1);
        await Assert.That(converse.System[0].Text).IsEqualTo("Be terse.");
        await Assert.That(converse.Messages.Count).IsEqualTo(1);
        await Assert.That(converse.Messages[0].Role).IsEqualTo(ConversationRole.User);
    }

    [Test]
    public async Task Keeps_every_system_message_in_order()
    {
        // Callers build system prompts in layers. Keeping only the first would change the
        // model's instructions with no error anywhere.
        ChatCompletionRequest request = Request(
            new ChatMessage { Role = ChatRoles.System, Content = "First." },
            new ChatMessage { Role = ChatRoles.System, Content = "Second." },
            new ChatMessage { Role = ChatRoles.User, Content = "hello" });

        ConverseRequest converse = BedrockTranslator.ToConverseRequest(request, Route);

        await Assert.That(converse.System!.Count).IsEqualTo(2);
        await Assert.That(converse.System[0].Text).IsEqualTo("First.");
        await Assert.That(converse.System[1].Text).IsEqualTo("Second.");
    }

    [Test]
    public async Task Leaves_the_system_field_null_when_there_is_no_system_prompt()
    {
        // Bedrock rejects an empty system list on some model families, so absent has to mean
        // null rather than an empty array.
        ConverseRequest converse = BedrockTranslator.ToConverseRequest(Request(), Route);

        await Assert.That(converse.System).IsNull();
    }

    [Test]
    public async Task Maps_assistant_turns_to_the_assistant_role()
    {
        ChatCompletionRequest request = Request(
            new ChatMessage { Role = ChatRoles.User, Content = "hi" },
            new ChatMessage { Role = ChatRoles.Assistant, Content = "hello" },
            new ChatMessage { Role = ChatRoles.User, Content = "again" });

        ConverseRequest converse = BedrockTranslator.ToConverseRequest(request, Route);

        await Assert.That(converse.Messages[1].Role).IsEqualTo(ConversationRole.Assistant);
    }

    [Test]
    public async Task Rejects_a_tool_message_rather_than_relabelling_it()
    {
        // Coercing a tool result into a user turn produces a plausible answer to the wrong
        // conversation, which is worse than a clear failure.
        ChatCompletionRequest request = Request(
            new ChatMessage { Role = ChatRoles.Tool, Content = "{\"result\":42}" });

        ProviderException? thrown = Assert.Throws<ProviderException>(
            () => BedrockTranslator.ToConverseRequest(request, Route));

        await Assert.That(thrown!.IsRetryable).IsFalse();
        await Assert.That(thrown.Message).Contains("Tool messages");
    }

    [Test]
    public async Task Omits_the_inference_config_when_the_caller_set_nothing()
    {
        // So Bedrock applies each model's own defaults. A gateway substituting its own default
        // temperature would change the behaviour of every request that did not specify one.
        ConverseRequest converse = BedrockTranslator.ToConverseRequest(Request(), Route);

        await Assert.That(converse.InferenceConfig).IsNull();
    }

    [Test]
    public async Task Forwards_the_sampling_parameters_that_were_set()
    {
        var request = new ChatCompletionRequest
        {
            Model = "claude",
            Messages = [new ChatMessage { Role = ChatRoles.User, Content = "hi" }],
            Temperature = 0.2f,
            TopP = 0.9f,
            MaxTokens = 128,
            Stop = ["\n\n"],
        };

        ConverseRequest converse = BedrockTranslator.ToConverseRequest(request, Route);

        await Assert.That(converse.InferenceConfig!.Temperature).IsEqualTo(0.2f);
        await Assert.That(converse.InferenceConfig.TopP).IsEqualTo(0.9f);
        await Assert.That(converse.InferenceConfig.MaxTokens).IsEqualTo(128);
        await Assert.That(converse.InferenceConfig.StopSequences!.Single()).IsEqualTo("\n\n");
    }

    [Test]
    public async Task Concatenates_several_text_blocks_rather_than_taking_the_first()
    {
        // Reasoning models routinely emit several text blocks. Taking blocks[0] would discard
        // most of the answer and look like a short completion rather than a truncated one.
        var output = new ConverseOutput
        {
            Message = new Message
            {
                Role = ConversationRole.Assistant,
                Content = [new ContentBlock { Text = "one " }, new ContentBlock { Text = "two" }],
            },
        };

        await Assert.That(BedrockTranslator.ExtractText(output)).IsEqualTo("one two");
    }

    [Test]
    public async Task Maps_stop_reasons_to_the_openai_spelling()
    {
        await Assert.That(BedrockTranslator.ToOpenAiFinishReason(StopReason.End_turn)).IsEqualTo("stop");
        await Assert.That(BedrockTranslator.ToOpenAiFinishReason(StopReason.Stop_sequence)).IsEqualTo("stop");
        await Assert.That(BedrockTranslator.ToOpenAiFinishReason(StopReason.Max_tokens)).IsEqualTo("length");
        await Assert.That(BedrockTranslator.ToOpenAiFinishReason(StopReason.Tool_use)).IsEqualTo("tool_calls");
        await Assert.That(BedrockTranslator.ToOpenAiFinishReason(StopReason.Content_filtered)).IsEqualTo("content_filter");
        await Assert.That(BedrockTranslator.ToOpenAiFinishReason(StopReason.Guardrail_intervened)).IsEqualTo("content_filter");
    }

    [Test]
    public async Task Reads_plain_usage_with_no_cache_involved()
    {
        GatehouseUsage? usage = BedrockTranslator.ToTokenUsage(
            new BedrockUsage { InputTokens = 100, OutputTokens = 20, TotalTokens = 120 });

        await Assert.That(usage!.PromptTokens).IsEqualTo(100);
        await Assert.That(usage.CompletionTokens).IsEqualTo(20);
        await Assert.That(usage.TotalTokens).IsEqualTo(120);
        await Assert.That(usage.IsProviderReported).IsTrue();
    }

    [Test]
    public async Task Treats_cache_tokens_as_additive_when_the_provider_total_says_so()
    {
        // The failure this guards against is silent and expensive. If cache tokens are additive
        // and Gatehouse treats them as a subset, every cached request under-reports its prompt
        // by the entire cached portion — and the error only surfaces months later as a
        // reconciliation variance nobody can explain.
        //
        // 100 + 20 + 500 + 50 = 670, so the parts are additive.
        GatehouseUsage? usage = BedrockTranslator.ToTokenUsage(new BedrockUsage
        {
            InputTokens = 100,
            OutputTokens = 20,
            CacheReadInputTokens = 500,
            CacheWriteInputTokens = 50,
            TotalTokens = 670,
        });

        await Assert.That(usage!.PromptTokens).IsEqualTo(650);
        await Assert.That(usage.CachedPromptTokens).IsEqualTo(500);
        await Assert.That(usage.CacheCreationTokens).IsEqualTo(50);
        await Assert.That(usage.TotalTokens).IsEqualTo(670);
    }

    [Test]
    public async Task Treats_cache_tokens_as_a_subset_when_the_provider_total_says_so()
    {
        // The other convention, derived rather than assumed. 100 + 20 = 120, so the cache
        // counts are already inside the input figure and must not be added again.
        GatehouseUsage? usage = BedrockTranslator.ToTokenUsage(new BedrockUsage
        {
            InputTokens = 100,
            OutputTokens = 20,
            CacheReadInputTokens = 80,
            CacheWriteInputTokens = 0,
            TotalTokens = 120,
        });

        await Assert.That(usage!.PromptTokens).IsEqualTo(100);
        await Assert.That(usage.CachedPromptTokens).IsEqualTo(80);
        await Assert.That(usage.TotalTokens).IsEqualTo(120);
    }

    [Test]
    public async Task Assumes_additive_cache_tokens_when_the_provider_omits_the_total()
    {
        // What Bedrock documents, and the safer guess: assuming a subset would under-report the
        // prompt, and under-reporting spend is the direction that goes unnoticed.
        GatehouseUsage? usage = BedrockTranslator.ToTokenUsage(new BedrockUsage
        {
            InputTokens = 100,
            OutputTokens = 20,
            CacheReadInputTokens = 500,
        });

        await Assert.That(usage!.PromptTokens).IsEqualTo(600);
        await Assert.That(usage.CachedPromptTokens).IsEqualTo(500);
    }

    [Test]
    public async Task Produces_usage_that_passes_the_metering_consistency_check()
    {
        // Whichever branch is taken, the result has to satisfy the invariant the reconciliation
        // depends on: the cache subsets fit inside the prompt count, and the total adds up.
        // Written out as four calls rather than a loop over an array, so a failure names the
        // shape that broke: the stack points at the line, and the line has a comment on it.
        // A loop would report "one of four" and leave the reader to work out which.

        // No cache involved.
        await AssertConsistent(new BedrockUsage { InputTokens = 100, OutputTokens = 20, TotalTokens = 120 });

        // Additive: the total accounts for the cache counts on top of the input.
        await AssertConsistent(new BedrockUsage
        {
            InputTokens = 100,
            OutputTokens = 20,
            CacheReadInputTokens = 500,
            CacheWriteInputTokens = 50,
            TotalTokens = 670,
        });

        // Subset: the total is input plus output, so the cache count is already inside it.
        await AssertConsistent(new BedrockUsage
        {
            InputTokens = 100,
            OutputTokens = 20,
            CacheReadInputTokens = 80,
            TotalTokens = 120,
        });

        // No total reported, so additive is assumed — what AWS documents.
        await AssertConsistent(new BedrockUsage
        {
            InputTokens = 100,
            OutputTokens = 20,
            CacheReadInputTokens = 500,
        });

        static async Task AssertConsistent(BedrockUsage reported)
        {
            GatehouseUsage? usage = BedrockTranslator.ToTokenUsage(reported);

            await Assert.That(usage).IsNotNull();

            bool consistent = Gatehouse.Metering.MeteringConsistency.TryValidate(usage!, out string? discrepancy);

            await Assert.That(consistent).IsTrue();
            await Assert.That(discrepancy).IsNull();
        }
    }

    [Test]
    public async Task Returns_null_when_the_provider_reported_no_usage()
    {
        // Null rather than a zeroed record: a zero that claims to be authoritative cannot be
        // reconciled later, and the metering layer records absent usage as unknown.
        await Assert.That(BedrockTranslator.ToTokenUsage(null)).IsNull();
    }

    [Test]
    public async Task The_provider_kind_matches_the_one_the_validator_checks_for()
    {
        // The constant is duplicated because Core must not depend on a provider assembly — the
        // dependency runs the other way. This is the test that keeps the copies honest; without
        // it, renaming one leaves Bedrock silently failing validation as an unknown kind.
        await Assert.That(Gatehouse.Configuration.GatehouseOptionsValidator.BedrockProviderKind)
            .IsEqualTo(BedrockProvider.ProviderName);
    }

    private static ChatCompletionRequest Request(params ChatMessage[] messages) => new()
    {
        Model = "claude",
        Messages = messages.Length > 0
            ? messages
            : [new ChatMessage { Role = ChatRoles.User, Content = "hello" }],
    };
}
