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
/// End-to-end tests for the Gemini provider against a fake speaking the real wire format.
/// </summary>
public class GeminiEndToEndTests
{
    [Test]
    public async Task Streams_a_completion_end_to_end()
    {
        await using FakeGeminiUpstream upstream = await FakeGeminiUpstream.StartAsync();
        upstream.Chunks = ["The ", "gate ", "answers."];

        await using GatehouseHost gateway = await Host(upstream);

        Streamed result = await ReadStreamAsync(gateway);

        await Assert.That(result.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(result.Text).IsEqualTo("The gate answers.");
        await Assert.That(result.SawDone).IsTrue();
        await Assert.That(result.FinishReason).IsEqualTo("stop");
    }

    [Test]
    public async Task Requests_sse_streaming_and_puts_the_model_in_the_path()
    {
        // Without alt=sse the response is a fragmented JSON array rather than server-sent
        // events, which cannot be read incrementally — the stream would appear to work and
        // arrive all at once.
        await using FakeGeminiUpstream upstream = await FakeGeminiUpstream.StartAsync();
        await using GatehouseHost gateway = await Host(upstream);

        await ReadStreamAsync(gateway);

        await Assert.That(upstream.LastPath!).Contains(":streamGenerateContent");
        await Assert.That(upstream.LastPath!).Contains("upstream-model-name");
        await Assert.That(upstream.LastQuery!).Contains("alt=sse");
    }

    [Test]
    public async Task Counts_thinking_tokens_as_output()
    {
        // Gemini bills thinking tokens as output but reports them outside
        // candidatesTokenCount. Forwarding only the candidates count under-bills every
        // thinking-model request — here by 160 of 200 output tokens.
        await using FakeGeminiUpstream upstream = await FakeGeminiUpstream.StartAsync();
        upstream.CandidatesTokenCount = 40;
        upstream.ThoughtsTokenCount = 160;

        await using GatehouseHost gateway = await Host(upstream);

        await ReadStreamAsync(gateway);
        RequestRecord record = await WaitForRecordAsync(gateway);

        await Assert.That(record.CompletionTokens).IsEqualTo(200);
    }

    [Test]
    public async Task Records_usage_as_measured_despite_geminis_own_total_disagreeing()
    {
        // Gemini's totalTokenCount includes thinking tokens, so it does not equal
        // prompt + candidates. Gatehouse derives its own total instead of forwarding that one;
        // if it forwarded it, MeteringConsistency would flag every thinking-model request and
        // downgrade perfectly good usage data to "estimated".
        await using FakeGeminiUpstream upstream = await FakeGeminiUpstream.StartAsync();
        upstream.PromptTokenCount = 1000;
        upstream.CandidatesTokenCount = 40;
        upstream.ThoughtsTokenCount = 160;

        await using GatehouseHost gateway = await Host(upstream);

        await ReadStreamAsync(gateway);
        RequestRecord record = await WaitForRecordAsync(gateway);

        await Assert.That(record.UsageIsProviderReported).IsTrue();
        await Assert.That(record.PromptTokens).IsEqualTo(1000);
        await Assert.That(record.CompletionTokens).IsEqualTo(200);
    }

    [Test]
    public async Task Renames_the_assistant_role_and_lifts_the_system_instruction()
    {
        await using FakeGeminiUpstream upstream = await FakeGeminiUpstream.StartAsync();
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
                    new { role = "user", content = "Hi" },
                    new { role = "assistant", content = "Hello" },
                    new { role = "user", content = "Again" },
                },
            }));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = upstream.LastRequestBody!;
        await Assert.That(body).Contains("systemInstruction");
        await Assert.That(body).Contains("\"role\":\"model\"");
        await Assert.That(body).DoesNotContain("\"role\":\"assistant\"");
        await Assert.That(body).DoesNotContain("\"role\":\"system\"");
    }

    [Test]
    public async Task Sends_the_api_key_as_a_header_not_a_query_parameter()
    {
        // A credential in a query string ends up in access logs, proxy logs and error reports.
        await using FakeGeminiUpstream upstream = await FakeGeminiUpstream.StartAsync();
        await using GatehouseHost gateway = await Host(upstream, apiKey: "AIza-test-key");

        await ReadStreamAsync(gateway);

        await Assert.That(upstream.LastApiKey).IsEqualTo("AIza-test-key");
        await Assert.That(upstream.LastQuery!).DoesNotContain("AIza-test-key");
    }

    [Test]
    public async Task Maps_a_safety_finish_reason_to_content_filter()
    {
        // A caller that sees "stop" believes it got a complete answer. Flattening a moderation
        // outcome into it hides the one result an application most needs to branch on.
        await using FakeGeminiUpstream upstream = await FakeGeminiUpstream.StartAsync();
        upstream.FinishReason = "SAFETY";

        await using GatehouseHost gateway = await Host(upstream);

        Streamed result = await ReadStreamAsync(gateway);

        await Assert.That(result.FinishReason).IsEqualTo("content_filter");
    }

    private static Task<GatehouseHost> Host(FakeGeminiUpstream upstream, string apiKey = "AIza-test-key") =>
        GatehouseHost.StartAsync(upstream.BaseAddress, apiKey: apiKey, kind: "google-gemini");

    private sealed record Streamed(HttpStatusCode Status, string Text, bool SawDone, string? FinishReason);

    private static async Task<Streamed> ReadStreamAsync(GatehouseHost gateway)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = "fast",
                stream = true,
                messages = new[] { new { role = "user", content = "Say something." } },
            }),
        };

        using HttpResponseMessage response = await gateway.Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);

        if (!response.IsSuccessStatusCode)
        {
            return new Streamed(response.StatusCode, string.Empty, false, null);
        }

        var text = new StringBuilder();
        bool sawDone = false;
        string? finishReason = null;

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
            }

            finishReason = chunk.Choices[0].FinishReason ?? finishReason;
        }

        return new Streamed(response.StatusCode, text.ToString(), sawDone, finishReason);
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
