using System.Net;
using System.Net.Http.Json;
using Gatehouse.Metering;
using Gatehouse.Server.Tests.Fakes;
using Gatehouse.Storage;
using Gatehouse.Wire;

namespace Gatehouse.Server.Tests;

/// <summary>
/// Exact-match caching through the real pipeline.
/// </summary>
/// <remarks>
/// The assertion that matters most here is not "the second request was fast". It is that a
/// cache hit does not enter the billed totals: a hit has real token counts that no provider
/// charged for, and counting them as consumption would inflate recorded usage by exactly the
/// amount the cache saved — inverting a cost win into an apparent overspend on the very
/// reconciliation report that exists to catch overspend.
/// </remarks>
public class CachingEndToEndTests
{
    [Test]
    public async Task Serves_a_repeated_request_without_calling_the_provider_again()
    {
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        await using GatehouseHost host = await GatehouseHost.StartAsync(upstream.BaseAddress, cacheEnabled: true);

        HttpResponseMessage first = await PostAsync(host);
        HttpResponseMessage second = await PostAsync(host);

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // One upstream call for two client requests. This is the whole point.
        await Assert.That(upstream.Requests).IsEqualTo(1);
    }

    [Test]
    public async Task Announces_a_cache_hit_in_a_response_header()
    {
        // A cache nobody can see is a cache nobody can debug, and "why is this response
        // identical every time" is otherwise a support ticket with no evidence attached.
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        await using GatehouseHost host = await GatehouseHost.StartAsync(upstream.BaseAddress, cacheEnabled: true);

        HttpResponseMessage first = await PostAsync(host);
        HttpResponseMessage second = await PostAsync(host);

        await Assert.That(first.Headers.Contains("X-Gatehouse-Cache")).IsFalse();
        await Assert.That(second.Headers.GetValues("X-Gatehouse-Cache").Single()).IsEqualTo("hit");
    }

    [Test]
    public async Task Keeps_cache_hits_out_of_the_billed_usage_totals()
    {
        // The point of the whole slice. Two identical requests, one upstream call: the usage
        // report must show one request's worth of billed tokens and the other as avoided.
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        await using GatehouseHost host = await GatehouseHost.StartAsync(upstream.BaseAddress, cacheEnabled: true);

        await PostAsync(host);
        await PostAsync(host);

        UsageSummary usage = await WaitForUsageAsync(host, expectedRequests: 2);

        await Assert.That(usage.Requests).IsEqualTo(2);
        await Assert.That(usage.CacheHits).IsEqualTo(1);
        await Assert.That(usage.BillableRequests).IsEqualTo(1);

        // Billed tokens are one request's worth, and the hit's tokens are reported as the
        // saving rather than as consumption.
        await Assert.That(usage.TotalTokens).IsGreaterThan(0);
        await Assert.That(usage.TokensAvoided).IsEqualTo(usage.TotalTokens);

        // A cache hit is not a measurement failure, so confidence must stay at 100%: the one
        // billable request had provider-reported counts.
        await Assert.That(usage.Confidence).IsEqualTo(1);
    }

    [Test]
    public async Task Serves_a_streamed_request_from_an_entry_created_by_a_buffered_one()
    {
        // 'stream' is deliberately not in the cache key: the content of an answer does not
        // depend on how it is delivered. This is what that buys.
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        upstream.Chunks = ["cached", " across", " modes"];

        await using GatehouseHost host = await GatehouseHost.StartAsync(upstream.BaseAddress, cacheEnabled: true);

        await PostAsync(host, stream: false);
        HttpResponseMessage streamed = await PostAsync(host, stream: true);

        await Assert.That(streamed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(streamed.Content.Headers.ContentType!.MediaType).IsEqualTo("text/event-stream");
        await Assert.That(streamed.Headers.GetValues("X-Gatehouse-Cache").Single()).IsEqualTo("hit");

        // Reassembled from the replayed SSE, and identical to what the provider said.
        await Assert.That(await ReadStreamedTextAsync(streamed)).IsEqualTo("cached across modes");
        await Assert.That(upstream.Requests).IsEqualTo(1);
    }

    [Test]
    public async Task Populates_the_cache_from_a_streamed_response()
    {
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        upstream.Chunks = ["from", " a", " stream"];

        await using GatehouseHost host = await GatehouseHost.StartAsync(upstream.BaseAddress, cacheEnabled: true);

        await ReadStreamedTextAsync(await PostAsync(host, stream: true));
        HttpResponseMessage buffered = await PostAsync(host, stream: false);

        ChatCompletionResponse? body = await buffered.Content.ReadFromJsonAsync<ChatCompletionResponse>();

        // The streamed chunks were reassembled into one canonical response on the way in.
        await Assert.That(body!.Choices[0].Message.Content).IsEqualTo("from a stream");
        await Assert.That(upstream.Requests).IsEqualTo(1);
    }

    [Test]
    public async Task Does_not_serve_a_different_prompt_from_the_cache()
    {
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        await using GatehouseHost host = await GatehouseHost.StartAsync(upstream.BaseAddress, cacheEnabled: true);

        await PostAsync(host, prompt: "first question");
        await PostAsync(host, prompt: "second question");

        await Assert.That(upstream.Requests).IsEqualTo(2);
    }

    [Test]
    public async Task Does_not_share_an_entry_between_two_model_aliases_on_different_upstreams()
    {
        // 'fast' resolves to upstream-model-name and 'gpt-4o-mini' to itself. Same prompt, two
        // upstream models, so two upstream calls.
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        await using GatehouseHost host = await GatehouseHost.StartAsync(upstream.BaseAddress, cacheEnabled: true);

        await PostAsync(host, model: "fast");
        await PostAsync(host, model: "gpt-4o-mini");

        await Assert.That(upstream.Requests).IsEqualTo(2);
    }

    [Test]
    public async Task Calls_the_provider_every_time_when_caching_is_off()
    {
        // The shipped default. Caching changes observable behaviour, so a gateway must not
        // start doing it because someone upgraded.
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        await using GatehouseHost host = await GatehouseHost.StartAsync(upstream.BaseAddress);

        await PostAsync(host);
        await PostAsync(host);

        await Assert.That(upstream.Requests).IsEqualTo(2);
    }

    [Test]
    public async Task Does_not_cache_a_failed_response()
    {
        // A 503 must not be replayed for the whole TTL. That would turn a momentary provider
        // blip into an outage lasting an hour, long after the provider recovered.
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        upstream.FailWith = HttpStatusCode.ServiceUnavailable;

        await using GatehouseHost host = await GatehouseHost.StartAsync(upstream.BaseAddress, cacheEnabled: true);

        await PostAsync(host);
        upstream.FailWith = null;
        HttpResponseMessage second = await PostAsync(host);

        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(upstream.Requests).IsEqualTo(2);
    }

    [Test]
    public async Task Does_not_cache_a_stream_that_died_before_finishing()
    {
        // Caching a truncated answer is the worst failure this cache can have: it replays half
        // a completion to every caller for the whole TTL, and it looks like a model that just
        // stops mid-sentence.
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        upstream.Chunks = ["one", "two", "three"];
        upstream.FailAfterChunk = 2;

        await using GatehouseHost host = await GatehouseHost.StartAsync(upstream.BaseAddress, cacheEnabled: true);

        try
        {
            await ReadStreamedTextAsync(await PostAsync(host, stream: true));
        }
        catch (HttpRequestException)
        {
            // An upstream dying mid-stream can end the response body abruptly. What this test
            // is about is what happens next, not how gracefully the first call ended.
        }

        upstream.FailAfterChunk = null;
        await PostAsync(host, stream: false);

        await Assert.That(upstream.Requests).IsEqualTo(2);
    }

    private static Task<HttpResponseMessage> PostAsync(
        GatehouseHost host,
        string model = "fast",
        string prompt = "hello",
        bool stream = false) =>
        host.Client.PostAsJsonAsync(
            "/v1/chat/completions",
            new
            {
                model,
                stream,
                messages = new[] { new { role = "user", content = prompt } },
            });

    private static async Task<UsageSummary> WaitForUsageAsync(GatehouseHost host, int expectedRequests)
    {
        var window = new UsageWindow(
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddHours(1));

        for (int attempt = 0; attempt < 50; attempt++)
        {
            IReadOnlyList<UsageSummary> summaries = await host.Usage.SummariseAsync(window);

            if (summaries.Count > 0 && summaries[0].Requests >= expectedRequests)
            {
                return summaries[0];
            }

            await Task.Delay(20);
        }

        throw new InvalidOperationException(
            $"The request log did not record {expectedRequests} request(s) within one second.");
    }

    private static async Task<string> ReadStreamedTextAsync(HttpResponseMessage response)
    {
        var text = new System.Text.StringBuilder();

        await using Stream stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            string payload = line["data: ".Length..];

            if (payload == "[DONE]")
            {
                break;
            }

            ChatCompletionChunk? chunk = System.Text.Json.JsonSerializer.Deserialize(
                payload,
                GatehouseJsonContext.Default.ChatCompletionChunk);

            if (chunk is { Choices.Count: > 0 })
            {
                text.Append(chunk.Choices[0].Delta.Content);
            }
        }

        return text.ToString();
    }
}
