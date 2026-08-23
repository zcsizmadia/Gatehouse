using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Gatehouse.Server.Tests.Fakes;
using Gatehouse.Storage;
using Gatehouse.Streaming;
using Gatehouse.Wire;

namespace Gatehouse.Server.Tests;

/// <summary>
/// End-to-end tests for the Anthropic provider, against a fake that speaks the real wire
/// format over a real Kestrel.
/// </summary>
/// <remarks>
/// The metering assertions here are the reason this file exists. Both of the mistakes they
/// catch — summing cumulative usage, and treating <c>input_tokens</c> as the whole prompt —
/// produce plausible-looking numbers that are wrong by large margins, and neither throws.
/// </remarks>
public class AnthropicEndToEndTests
{
    [Test]
    public async Task Streams_a_completion_end_to_end()
    {
        await using FakeAnthropicUpstream upstream = await FakeAnthropicUpstream.StartAsync();
        upstream.Chunks = ["The ", "gate ", "holds."];

        await using GatehouseHost gateway = await Host(upstream);

        Streamed result = await ReadStreamAsync(gateway, "gpt-4o-mini");

        await Assert.That(result.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(result.Text).IsEqualTo("The gate holds.");
        await Assert.That(result.SawDone).IsTrue();
        await Assert.That(result.FinishReason).IsEqualTo("stop");
    }

    [Test]
    public async Task Does_not_sum_cumulative_streamed_usage()
    {
        // The headline assertion. The fake reports a rising cumulative output count on every
        // message_delta, ending at 30. Summing them would give 60 across three chunks; taking
        // the last gives 30. Anthropic documents these as cumulative, and a provider that adds
        // them over-bills every streamed completion.
        await using FakeAnthropicUpstream upstream = await FakeAnthropicUpstream.StartAsync();
        upstream.Chunks = ["a", "b", "c"];
        upstream.FinalOutputTokens = 30;

        await using GatehouseHost gateway = await Host(upstream);

        await ReadStreamAsync(gateway, "gpt-4o-mini");
        RequestRecord record = await WaitForRecordAsync(gateway);

        await Assert.That(record.CompletionTokens).IsEqualTo(30);
    }

    [Test]
    public async Task Counts_cached_tokens_as_part_of_the_prompt()
    {
        // Anthropic's input_tokens excludes both cache figures:
        //   total_input = input + cache_read + cache_creation = 100 + 800 + 100 = 1000
        // A provider that forwards input_tokens alone reports 100 — a tenth of the truth, and
        // silently so.
        await using FakeAnthropicUpstream upstream = await FakeAnthropicUpstream.StartAsync();
        upstream.InputTokens = 100;
        upstream.CacheReadTokens = 800;
        upstream.CacheCreationTokens = 100;

        await using GatehouseHost gateway = await Host(upstream);

        await ReadStreamAsync(gateway, "gpt-4o-mini");
        RequestRecord record = await WaitForRecordAsync(gateway);

        await Assert.That(record.PromptTokens).IsEqualTo(1000);
        await Assert.That(record.UsageIsProviderReported).IsTrue();
    }

    [Test]
    public async Task Tolerates_ping_and_unknown_events_without_emitting_content_for_them()
    {
        // Anthropic requires clients to handle unknown event types gracefully. A provider that
        // fails on one aborts a generation the caller has already been billed for; one that
        // forwards them injects empty chunks into the caller's stream.
        await using FakeAnthropicUpstream upstream = await FakeAnthropicUpstream.StartAsync();
        upstream.Chunks = ["one", "two"];
        upstream.EmitPing = true;
        upstream.EmitUnknownEvent = true;

        await using GatehouseHost gateway = await Host(upstream);

        Streamed result = await ReadStreamAsync(gateway, "gpt-4o-mini");

        await Assert.That(result.Text).IsEqualTo("onetwo");

        // Two content chunks plus one terminal chunk carrying the finish reason and usage.
        await Assert.That(result.ContentChunkCount).IsEqualTo(2);
    }

    [Test]
    public async Task Lifts_the_system_message_out_of_the_messages_array()
    {
        await using FakeAnthropicUpstream upstream = await FakeAnthropicUpstream.StartAsync();
        await using GatehouseHost gateway = await Host(upstream);

        using HttpResponseMessage response = await gateway.Client.PostAsync(
            "/v1/chat/completions",
            JsonContent.Create(new
            {
                model = "gpt-4o-mini",
                stream = false,
                messages = new[]
                {
                    new { role = "system", content = "Be terse." },
                    new { role = "user", content = "Hello" },
                },
            }));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // The system prompt must be a top-level field. Anthropic rejects it as a message role.
        await Assert.That(upstream.LastRequestBody!).Contains("\"system\":\"Be terse.\"");
        await Assert.That(upstream.LastRequestBody!).DoesNotContain("\"role\":\"system\"");
    }

    [Test]
    public async Task Supplies_the_required_max_tokens_field()
    {
        // Anthropic rejects a request without max_tokens; OpenAI treats it as optional.
        await using FakeAnthropicUpstream upstream = await FakeAnthropicUpstream.StartAsync();
        await using GatehouseHost gateway = await Host(upstream);

        await ReadStreamAsync(gateway, "gpt-4o-mini");

        await Assert.That(upstream.LastRequestBody!).Contains("\"max_tokens\":");
    }

    [Test]
    public async Task Authenticates_with_the_x_api_key_header_and_a_version()
    {
        // Anthropic uses x-api-key, not Authorization: Bearer. The wrong header yields a 401
        // whose message does not mention which header was wrong.
        await using FakeAnthropicUpstream upstream = await FakeAnthropicUpstream.StartAsync();
        await using GatehouseHost gateway = await Host(upstream, apiKey: "sk-ant-test");

        await ReadStreamAsync(gateway, "gpt-4o-mini");

        await Assert.That(upstream.LastApiKey).IsEqualTo("sk-ant-test");
        await Assert.That(upstream.LastVersion).IsEqualTo("2023-06-01");
    }

    [Test]
    public async Task Maps_max_tokens_stop_reason_to_length()
    {
        await using FakeAnthropicUpstream upstream = await FakeAnthropicUpstream.StartAsync();
        upstream.StopReason = "max_tokens";

        await using GatehouseHost gateway = await Host(upstream);

        Streamed result = await ReadStreamAsync(gateway, "gpt-4o-mini");

        await Assert.That(result.FinishReason).IsEqualTo("length");
    }

    [Test]
    public async Task Returns_a_buffered_completion_with_normalised_usage()
    {
        await using FakeAnthropicUpstream upstream = await FakeAnthropicUpstream.StartAsync();
        upstream.Chunks = ["Buffered."];
        upstream.InputTokens = 10;
        upstream.CacheReadTokens = 40;
        upstream.CacheCreationTokens = 0;
        upstream.FinalOutputTokens = 7;

        await using GatehouseHost gateway = await Host(upstream);

        using HttpResponseMessage response = await gateway.Client.PostAsync(
            "/v1/chat/completions",
            JsonContent.Create(new
            {
                model = "gpt-4o-mini",
                stream = false,
                messages = new[] { new { role = "user", content = "hi" } },
            }));

        ChatCompletionResponse? body = await response.Content.ReadFromJsonAsync(
            GatehouseJsonContext.Default.ChatCompletionResponse);

        await Assert.That(body!.Choices[0].Message.Content).IsEqualTo("Buffered.");
        await Assert.That(body.Usage!.PromptTokens).IsEqualTo(50);
        await Assert.That(body.Usage.CachedPromptTokens).IsEqualTo(40);
        await Assert.That(body.Usage.CompletionTokens).IsEqualTo(7);
        await Assert.That(body.Usage.TotalTokens).IsEqualTo(57);
        await Assert.That(body.GatehouseProvider).IsEqualTo("fake");
    }

    private static Task<GatehouseHost> Host(FakeAnthropicUpstream upstream, string apiKey = "sk-ant-test") =>
        GatehouseHost.StartAsync(upstream.BaseAddress, apiKey: apiKey, kind: "anthropic");

    private sealed record Streamed(
        HttpStatusCode Status,
        string Text,
        bool SawDone,
        string? FinishReason,
        int ContentChunkCount);

    private static async Task<Streamed> ReadStreamAsync(GatehouseHost gateway, string model)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model,
                stream = true,
                messages = new[] { new { role = "user", content = "Say something." } },
            }),
        };

        using HttpResponseMessage response = await gateway.Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);

        if (!response.IsSuccessStatusCode)
        {
            return new Streamed(response.StatusCode, string.Empty, false, null, 0);
        }

        var text = new StringBuilder();
        bool sawDone = false;
        string? finishReason = null;
        int contentChunks = 0;

        await using Stream body = await response.Content.ReadAsStreamAsync();

        await foreach (ServerSentEvent sse in ServerSentEventReader.ReadAsync(body))
        {
            if (sse.IsDone)
            {
                sawDone = true;
                break;
            }

            ChatCompletionChunk? chunk = JsonSerializer.Deserialize(
                sse.Data,
                GatehouseJsonContext.Default.ChatCompletionChunk);

            if (chunk is null || chunk.Choices.Count == 0)
            {
                continue;
            }

            if (chunk.Choices[0].Delta.Content is { Length: > 0 } content)
            {
                text.Append(content);
                contentChunks++;
            }

            finishReason = chunk.Choices[0].FinishReason ?? finishReason;
        }

        return new Streamed(response.StatusCode, text.ToString(), sawDone, finishReason, contentChunks);
    }

    private static async Task<RequestRecord> WaitForRecordAsync(GatehouseHost gateway)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            IReadOnlyList<RequestRecord> records = await gateway.RequestLog.GetRecentAsync(1);
            if (records.Count > 0)
            {
                return records[0];
            }

            await Task.Delay(20);
        }

        throw new InvalidOperationException("No request record was written within one second.");
    }
}
