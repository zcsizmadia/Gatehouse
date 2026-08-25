using System.Net;
using System.Net.Http.Json;
using Gatehouse.Server.Tests.Fakes;
using Gatehouse.Storage;
using Gatehouse.Wire;

namespace Gatehouse.Server.Tests;

/// <summary>
/// Fallback behaviour through the real pipeline, over real sockets.
/// </summary>
/// <remarks>
/// The dispatcher's own tests cover the decision logic against scripted providers. These
/// cover the two things only an end-to-end test can: that a failing upstream really does get
/// swapped for a working one over HTTP, and that the request log — which is chargeback data —
/// names the provider that was actually billed rather than the one the caller asked for.
/// </remarks>
public class ResilienceEndToEndTests
{
    [Test]
    public async Task Falls_back_to_the_second_provider_when_the_first_returns_503()
    {
        await using FakeUpstream primary = await FakeUpstream.StartAsync();
        await using FakeUpstream backup = await FakeUpstream.StartAsync();

        primary.FailWith = HttpStatusCode.ServiceUnavailable;
        backup.Chunks = ["from", " the", " backup"];

        await using GatehouseHost host =
            await GatehouseHost.StartAsync(primary.BaseAddress, fallbackBaseUrl: backup.BaseAddress);

        HttpResponseMessage response = await host.Client.PostAsJsonAsync(
            "/v1/chat/completions",
            new { model = "fast", messages = new[] { new { role = "user", content = "hello" } } });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        ChatCompletionResponse? body = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>();

        await Assert.That(body!.Choices[0].Message.Content).IsEqualTo("from the backup");
        await Assert.That(primary.Requests).IsEqualTo(1);
        await Assert.That(backup.Requests).IsEqualTo(1);
    }

    [Test]
    public async Task Attributes_the_request_to_the_provider_that_was_actually_billed()
    {
        // The point of the whole slice. A fallback that succeeds while the request log still
        // names the primary produces a chargeback report that bills an account which was never
        // called, and nothing in the response would reveal it.
        await using FakeUpstream primary = await FakeUpstream.StartAsync();
        await using FakeUpstream backup = await FakeUpstream.StartAsync();

        primary.FailWith = HttpStatusCode.ServiceUnavailable;

        await using GatehouseHost host =
            await GatehouseHost.StartAsync(primary.BaseAddress, fallbackBaseUrl: backup.BaseAddress);

        await host.Client.PostAsJsonAsync(
            "/v1/chat/completions",
            new { model = "fast", messages = new[] { new { role = "user", content = "hello" } } });

        RequestRecord record = await WaitForRecordAsync(host);

        await Assert.That(record.Provider).IsEqualTo("fallback");
        await Assert.That(record.UpstreamModel).IsEqualTo("backup-model-name");

        // Still recorded against the alias the caller asked for: that is what they will look
        // the request up by, and rewriting it would hide the fallback rather than explain it.
        await Assert.That(record.RequestedModel).IsEqualTo("fast");
    }

    [Test]
    public async Task Streams_from_the_fallback_when_the_primary_fails_before_the_first_chunk()
    {
        await using FakeUpstream primary = await FakeUpstream.StartAsync();
        await using FakeUpstream backup = await FakeUpstream.StartAsync();

        primary.FailWith = HttpStatusCode.BadGateway;
        backup.Chunks = ["streamed", " from", " backup"];

        await using GatehouseHost host =
            await GatehouseHost.StartAsync(primary.BaseAddress, fallbackBaseUrl: backup.BaseAddress);

        HttpResponseMessage response = await host.Client.PostAsJsonAsync(
            "/v1/chat/completions",
            new
            {
                model = "fast",
                stream = true,
                messages = new[] { new { role = "user", content = "hello" } },
            });

        // A real 200 with a real event stream, not a 200 whose body announces a failure.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("text/event-stream");

        string text = await ReadStreamedTextAsync(response);

        // Every chunk, including the first. A dispatcher that advanced the enumerator once
        // more before handing it over would lose "streamed" and pass a laxer assertion.
        await Assert.That(text).IsEqualTo("streamed from backup");
    }

    [Test]
    public async Task Does_not_fall_back_from_a_failure_the_caller_has_to_fix()
    {
        // 400 is the caller's problem. Trying the backup bills a second provider to produce
        // the same rejection, so the primary's status has to reach the client unchanged.
        await using FakeUpstream primary = await FakeUpstream.StartAsync();
        await using FakeUpstream backup = await FakeUpstream.StartAsync();

        primary.FailWith = HttpStatusCode.BadRequest;

        await using GatehouseHost host =
            await GatehouseHost.StartAsync(primary.BaseAddress, fallbackBaseUrl: backup.BaseAddress);

        HttpResponseMessage response = await host.Client.PostAsJsonAsync(
            "/v1/chat/completions",
            new { model = "fast", messages = new[] { new { role = "user", content = "hello" } } });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(backup.Requests).IsEqualTo(0);
    }

    [Test]
    public async Task Reports_the_upstream_failure_when_a_route_has_no_fallback_configured()
    {
        // The single-route gateway must behave exactly as it did before fallbacks existed.
        await using FakeUpstream primary = await FakeUpstream.StartAsync();

        primary.FailWith = HttpStatusCode.ServiceUnavailable;

        await using GatehouseHost host = await GatehouseHost.StartAsync(primary.BaseAddress);

        HttpResponseMessage response = await host.Client.PostAsJsonAsync(
            "/v1/chat/completions",
            new { model = "fast", messages = new[] { new { role = "user", content = "hello" } } });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    }

    /// <summary>
    /// Waits for the asynchronous request log to catch up.
    /// </summary>
    /// <remarks>
    /// Recording is off the request path by design, so reading immediately after the response
    /// would race the writer.
    /// </remarks>
    private static async Task<RequestRecord> WaitForRecordAsync(GatehouseHost host)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            IReadOnlyList<RequestRecord> records = await host.RequestLog.GetRecentAsync(1);
            if (records.Count > 0)
            {
                return records[0];
            }

            await Task.Delay(20);
        }

        throw new InvalidOperationException("No request record was written within one second.");
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
