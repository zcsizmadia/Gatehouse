using System.Diagnostics;
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
/// End-to-end tests for the OpenAI-compatible endpoint.
/// </summary>
/// <remarks>
/// This is the Phase 0 gate expressed as tests: a real client, a real Gatehouse, a real
/// upstream, and a completion that streams all the way through.
/// </remarks>
public class ChatCompletionsEndpointTests
{
    [Test]
    public async Task Streams_a_completion_end_to_end()
    {
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        upstream.Chunks = ["The ", "gate ", "is ", "open."];

        await using GatehouseHost gateway = await GatehouseHost.StartAsync(upstream.BaseAddress);

        StreamedCompletion result = await ReadStreamAsync(gateway, "gpt-4o-mini");

        await Assert.That(result.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(result.Text).IsEqualTo("The gate is open.");
        await Assert.That(result.SawDoneSentinel).IsTrue();
    }

    [Test]
    public async Task Delivers_chunks_as_they_are_produced_rather_than_in_one_burst()
    {
        // The assertion the whole streaming design exists for. If anything on the path
        // buffers — the provider, the SSE writer, Kestrel — the text still arrives intact and
        // every other test in this file still passes. Only the arrival *spacing* changes.
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        upstream.Chunks = ["one", "two", "three"];
        upstream.ChunkDelay = TimeSpan.FromMilliseconds(200);

        await using GatehouseHost gateway = await GatehouseHost.StartAsync(upstream.BaseAddress);

        StreamedCompletion result = await ReadStreamAsync(gateway, "gpt-4o-mini");

        await Assert.That(result.ChunkOffsets.Count).IsGreaterThanOrEqualTo(3);

        TimeSpan spread = result.ChunkOffsets[^1] - result.ChunkOffsets[0];

        // Two 200 ms gaps separate the first and last chunk upstream. A buffering regression
        // collapses that spread to near zero; 250 ms leaves generous room for scheduling noise
        // while still failing decisively if the stream is batched.
        await Assert.That(spread).IsGreaterThan(TimeSpan.FromMilliseconds(250));
    }

    [Test]
    public async Task Sets_the_headers_that_keep_intermediaries_from_buffering()
    {
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        await using GatehouseHost gateway = await GatehouseHost.StartAsync(upstream.BaseAddress);

        using HttpRequestMessage request = StreamRequest("gpt-4o-mini");
        using HttpResponseMessage response = await gateway.Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);

        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("text/event-stream");
        await Assert.That(response.Headers.CacheControl!.NoCache).IsTrue();

        // nginx buffers proxied responses by default; this header is the documented opt-out.
        await Assert.That(response.Headers.GetValues("X-Accel-Buffering").First()).IsEqualTo("no");
    }

    [Test]
    public async Task Returns_a_buffered_completion_when_streaming_is_not_requested()
    {
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        upstream.Chunks = ["Buffered", " reply"];

        await using GatehouseHost gateway = await GatehouseHost.StartAsync(upstream.BaseAddress);

        using HttpResponseMessage response = await gateway.Client.PostAsync(
            "/v1/chat/completions",
            JsonContent.Create(new { model = "gpt-4o-mini", stream = false, messages = new[] { new { role = "user", content = "hi" } } }));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        ChatCompletionResponse? body = await response.Content.ReadFromJsonAsync(
            GatehouseJsonContext.Default.ChatCompletionResponse);

        await Assert.That(body!.Choices[0].Message.Content).IsEqualTo("Buffered reply");
        await Assert.That(body.Usage!.PromptTokens).IsEqualTo(11);
        await Assert.That(body.Usage.CompletionTokens).IsEqualTo(7);

        // The non-standard field that tells a caller which provider answered, so a routing
        // surprise is visible without reading server logs.
        await Assert.That(body.GatehouseProvider).IsEqualTo("fake");
    }

    [Test]
    public async Task Sends_the_upstream_model_name_not_the_alias()
    {
        // The classic gateway bug: it works in every test where alias and upstream name
        // happen to match, and 404s the first time an operator repoints an alias.
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        await using GatehouseHost gateway = await GatehouseHost.StartAsync(upstream.BaseAddress);

        await ReadStreamAsync(gateway, "fast");

        await Assert.That(upstream.LastRequestedModel).IsEqualTo("upstream-model-name");
    }

    [Test]
    public async Task Presents_the_configured_credential_upstream_and_not_the_callers()
    {
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        await using GatehouseHost gateway = await GatehouseHost.StartAsync(
            upstream.BaseAddress,
            apiKey: "the-real-upstream-key");

        using HttpRequestMessage request = StreamRequest("gpt-4o-mini");
        request.Headers.Add("Authorization", "Bearer gh-sk-caller-virtual-key");

        using HttpResponseMessage response = await gateway.Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);
        await response.Content.ReadAsStringAsync();

        await Assert.That(upstream.LastAuthorization).IsEqualTo("Bearer the-real-upstream-key");
        await Assert.That(upstream.LastAuthorization!).DoesNotContain("gh-sk-caller-virtual-key");
    }

    [Test]
    public async Task Rejects_an_unconfigured_model_with_404()
    {
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        await using GatehouseHost gateway = await GatehouseHost.StartAsync(upstream.BaseAddress);

        using HttpResponseMessage response = await gateway.Client.PostAsync(
            "/v1/chat/completions",
            JsonContent.Create(new { model = "claude-sonnet-5", messages = new[] { new { role = "user", content = "hi" } } }));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        ErrorResponse? error = await response.Content.ReadFromJsonAsync(
            GatehouseJsonContext.Default.ErrorResponse);

        await Assert.That(error!.Error.Type).IsEqualTo(ErrorTypes.InvalidRequest);
        await Assert.That(error.Error.Code).IsEqualTo("model_not_found");
    }

    [Test]
    [Arguments(HttpStatusCode.Unauthorized)]
    [Arguments(HttpStatusCode.TooManyRequests)]
    [Arguments(HttpStatusCode.InternalServerError)]
    public async Task Surfaces_a_pre_stream_upstream_failure_as_a_real_status_code(HttpStatusCode upstreamStatus)
    {
        // The reason the endpoint pulls the first chunk before writing any header. A gateway
        // that commits a 200 and then reports the failure in the body makes every upstream
        // rejection look like a successful call to load balancers, retry policies and metrics.
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        upstream.FailWith = upstreamStatus;

        await using GatehouseHost gateway = await GatehouseHost.StartAsync(upstream.BaseAddress);

        using HttpRequestMessage request = StreamRequest("gpt-4o-mini");
        using HttpResponseMessage response = await gateway.Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);

        await Assert.That(response.StatusCode).IsEqualTo(upstreamStatus);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/json");

        ErrorResponse? error = await response.Content.ReadFromJsonAsync(
            GatehouseJsonContext.Default.ErrorResponse);

        await Assert.That(error!.Error.Type).IsEqualTo(ErrorTypes.Upstream);
    }

    [Test]
    public async Task Rejects_a_malformed_body_with_400()
    {
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        await using GatehouseHost gateway = await GatehouseHost.StartAsync(upstream.BaseAddress);

        using var content = new StringContent("{ not json", Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await gateway.Client.PostAsync("/v1/chat/completions", content);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Rejects_a_request_with_no_messages()
    {
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        await using GatehouseHost gateway = await GatehouseHost.StartAsync(upstream.BaseAddress);

        using HttpResponseMessage response = await gateway.Client.PostAsync(
            "/v1/chat/completions",
            JsonContent.Create(new { model = "gpt-4o-mini", messages = Array.Empty<object>() }));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Records_the_request_with_provider_reported_usage()
    {
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        await using GatehouseHost gateway = await GatehouseHost.StartAsync(upstream.BaseAddress);

        await ReadStreamAsync(gateway, "fast");

        RequestRecord record = await WaitForRecordAsync(gateway);

        await Assert.That(record.RequestedModel).IsEqualTo("fast");
        await Assert.That(record.Provider).IsEqualTo("fake");
        await Assert.That(record.Streamed).IsTrue();
        await Assert.That(record.StatusCode).IsEqualTo(200);
        await Assert.That(record.PromptTokens).IsEqualTo(11);
        await Assert.That(record.CompletionTokens).IsEqualTo(7);
        await Assert.That(record.UsageIsProviderReported).IsTrue();

        // Time to first chunk is recorded only for streamed requests, and is the number a
        // buffering regression would move while total duration stayed flat.
        await Assert.That(record.TimeToFirstChunk).IsNotNull();
    }

    [Test]
    public async Task Records_a_failed_request_rather_than_dropping_it()
    {
        // The requests an operator most wants in the log are the ones that failed.
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        upstream.FailWith = HttpStatusCode.ServiceUnavailable;

        await using GatehouseHost gateway = await GatehouseHost.StartAsync(upstream.BaseAddress);

        using HttpResponseMessage response = await gateway.Client.PostAsync(
            "/v1/chat/completions",
            JsonContent.Create(new { model = "gpt-4o-mini", messages = new[] { new { role = "user", content = "hi" } } }));

        RequestRecord record = await WaitForRecordAsync(gateway);

        await Assert.That(record.ErrorType).IsEqualTo(ErrorTypes.Upstream);
        await Assert.That(record.StatusCode).IsEqualTo((int)HttpStatusCode.ServiceUnavailable);
        await Assert.That(record.UsageIsProviderReported).IsFalse();
    }

    [Test]
    public async Task Does_not_claim_authoritative_usage_when_the_upstream_reports_none()
    {
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        upstream.ReportUsage = false;

        await using GatehouseHost gateway = await GatehouseHost.StartAsync(upstream.BaseAddress);

        await ReadStreamAsync(gateway, "gpt-4o-mini");

        RequestRecord record = await WaitForRecordAsync(gateway);

        await Assert.That(record.UsageIsProviderReported).IsFalse();
        await Assert.That(record.PromptTokens).IsEqualTo(0);
    }

    [Test]
    public async Task Lists_the_configured_aliases()
    {
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        await using GatehouseHost gateway = await GatehouseHost.StartAsync(upstream.BaseAddress);

        ModelListResponse? models = await gateway.Client.GetFromJsonAsync(
            "/v1/models",
            GatehouseJsonContext.Default.ModelListResponse);

        await Assert.That(models!.Data.Select(m => m.Id)).IsEquivalentTo(new[] { "fast", "gpt-4o-mini" });
        await Assert.That(models.ObjectType).IsEqualTo("list");
    }

    [Test]
    public async Task Reports_healthy()
    {
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        await using GatehouseHost gateway = await GatehouseHost.StartAsync(upstream.BaseAddress);

        using HttpResponseMessage live = await gateway.Client.GetAsync("/health/live");
        using HttpResponseMessage ready = await gateway.Client.GetAsync("/health/ready");

        await Assert.That(live.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private static HttpRequestMessage StreamRequest(string model)
    {
        var payload = new
        {
            model,
            stream = true,
            messages = new[] { new { role = "user", content = "Say something." } },
        };

        return new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(payload),
        };
    }

    /// <summary>Reads a streamed completion, noting when each chunk arrived.</summary>
    private static async Task<StreamedCompletion> ReadStreamAsync(GatehouseHost gateway, string model)
    {
        using HttpRequestMessage request = StreamRequest(model);
        using HttpResponseMessage response = await gateway.Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);

        if (!response.IsSuccessStatusCode)
        {
            return new StreamedCompletion(response.StatusCode, string.Empty, [], false);
        }

        long start = Stopwatch.GetTimestamp();
        var text = new StringBuilder();
        List<TimeSpan> offsets = [];
        bool sawDone = false;

        await using Stream body = await response.Content.ReadAsStreamAsync();

        await foreach (ServerSentEvent sse in ServerSentEventReader.ReadAsync(body))
        {
            if (sse.IsDone)
            {
                sawDone = true;
                break;
            }

            offsets.Add(Stopwatch.GetElapsedTime(start));

            ChatCompletionChunk? chunk = JsonSerializer.Deserialize(
                sse.Data,
                GatehouseJsonContext.Default.ChatCompletionChunk);

            // Spelled out rather than written as `chunk?.Choices.Count > 0 && chunk.Choices[0]…`.
            // That form is correct — the compiler narrows chunk in the second operand — but it
            // reads as an unguarded dereference to both a human and a static analyser.
            if (chunk is null || chunk.Choices.Count == 0)
            {
                continue;
            }

            if (chunk.Choices[0].Delta.Content is { } content)
            {
                text.Append(content);
            }
        }

        return new StreamedCompletion(response.StatusCode, text.ToString(), offsets, sawDone);
    }

    /// <summary>
    /// Waits for the asynchronous request log to catch up.
    /// </summary>
    /// <remarks>
    /// Recording is deliberately off the request path, so a test that reads immediately after
    /// the response would be racing the writer. Polling with a ceiling keeps the test honest
    /// about that without making it slow or flaky.
    /// </remarks>
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

    private sealed record StreamedCompletion(
        HttpStatusCode Status,
        string Text,
        IReadOnlyList<TimeSpan> ChunkOffsets,
        bool SawDoneSentinel);
}
