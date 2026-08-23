using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Gatehouse.Server.Tests.Fakes;
using Gatehouse.Storage;
using Gatehouse.Wire;

namespace Gatehouse.Server.Tests;

/// <summary>
/// End-to-end tests for virtual key authentication.
/// </summary>
/// <remarks>
/// The gateway holds provider credentials, so the questions here are the ones an evaluator asks
/// first: does an unauthenticated request get in, does a revoked key stop working immediately,
/// and does the request log attribute spend to somebody.
/// </remarks>
public class AuthenticationTests
{
    [Test]
    public async Task Accepts_a_request_bearing_a_valid_key()
    {
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        await using GatehouseHost gateway = await GatehouseHost.StartAsync(upstream.BaseAddress);

        using HttpResponseMessage response = await Complete(gateway.Client);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Rejects_a_request_with_no_credential()
    {
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        await using GatehouseHost gateway = await GatehouseHost.StartAsync(upstream.BaseAddress);

        using var anonymous = new HttpClient { BaseAddress = gateway.Client.BaseAddress };
        using HttpResponseMessage response = await Complete(anonymous);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        ErrorResponse? error = await response.Content.ReadFromJsonAsync(
            GatehouseJsonContext.Default.ErrorResponse);

        await Assert.That(error!.Error.Type).IsEqualTo(ErrorTypes.Authentication);
        await Assert.That(error.Error.Message).Contains("Authorization: Bearer");
    }

    [Test]
    public async Task Advertises_the_bearer_scheme_on_rejection()
    {
        // So a compliant client knows what to send next, rather than guessing.
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        await using GatehouseHost gateway = await GatehouseHost.StartAsync(upstream.BaseAddress);

        using var anonymous = new HttpClient { BaseAddress = gateway.Client.BaseAddress };
        using HttpResponseMessage response = await Complete(anonymous);

        await Assert.That(response.Headers.WwwAuthenticate.ToString()).Contains("Bearer");
    }

    [Test]
    [Arguments("sk-an-openai-key")]
    [Arguments("gh-sk-tooshort")]
    [Arguments("not-a-key-at-all")]
    public async Task Rejects_a_malformed_credential(string credential)
    {
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        await using GatehouseHost gateway = await GatehouseHost.StartAsync(upstream.BaseAddress);

        using var client = new HttpClient { BaseAddress = gateway.Client.BaseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);

        using HttpResponseMessage response = await Complete(client);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Rejects_a_well_formed_but_unknown_key()
    {
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        await using GatehouseHost gateway = await GatehouseHost.StartAsync(upstream.BaseAddress);

        using var client = new HttpClient { BaseAddress = gateway.Client.BaseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            Gatehouse.Security.VirtualKeySecret.Generate().Secret);

        using HttpResponseMessage response = await Complete(client);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Does_not_reach_the_upstream_when_authentication_fails()
    {
        // The point of authenticating before routing: a rejected caller must not cost a
        // provider call, or an unauthenticated client could spend the organisation's money.
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        await using GatehouseHost gateway = await GatehouseHost.StartAsync(upstream.BaseAddress);

        using var anonymous = new HttpClient { BaseAddress = gateway.Client.BaseAddress };
        await Complete(anonymous);

        await Assert.That(upstream.LastRequestedModel).IsNull();
    }

    [Test]
    public async Task Leaves_the_health_endpoints_open()
    {
        // An orchestrator probing liveness has no credential and should not need one. A probe
        // that requires a secret is a probe that fails during credential rotation.
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        await using GatehouseHost gateway = await GatehouseHost.StartAsync(upstream.BaseAddress);

        using var anonymous = new HttpClient { BaseAddress = gateway.Client.BaseAddress };

        using HttpResponseMessage live = await anonymous.GetAsync("/health/live");
        using HttpResponseMessage ready = await anonymous.GetAsync("/health/ready");

        await Assert.That(live.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Protects_the_models_listing_too()
    {
        // It reveals which models an organisation has configured, which is not public
        // information.
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        await using GatehouseHost gateway = await GatehouseHost.StartAsync(upstream.BaseAddress);

        using var anonymous = new HttpClient { BaseAddress = gateway.Client.BaseAddress };
        using HttpResponseMessage response = await anonymous.GetAsync("/v1/models");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Attributes_the_request_to_the_key_that_authorised_it()
    {
        // Without this, a chargeback report has no owner to bill.
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        await using GatehouseHost gateway = await GatehouseHost.StartAsync(upstream.BaseAddress);

        await Complete(gateway.Client);
        RequestRecord record = await WaitForRecordAsync(gateway);

        await Assert.That(record.VirtualKeyId).IsEqualTo(gateway.VirtualKeyId);
        await Assert.That(record.Organisation).IsEqualTo("acme");
        await Assert.That(record.Team).IsEqualTo("platform");
        await Assert.That(record.Application).IsEqualTo("integration-tests");
    }

    [Test]
    public async Task Accepts_unauthenticated_requests_when_authentication_is_disabled()
    {
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        await using GatehouseHost gateway = await GatehouseHost.StartAsync(
            upstream.BaseAddress,
            authenticationMode: "Disabled");

        using HttpResponseMessage response = await Complete(gateway.Client);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(gateway.VirtualKeySecret).IsNull();
    }

    [Test]
    public async Task Records_an_unattributed_request_when_authentication_is_disabled()
    {
        // Recorded as having no owner rather than being assigned a placeholder one. A
        // chargeback report should show unattributed spend as a visible gap, not invent an
        // owner for it.
        await using FakeUpstream upstream = await FakeUpstream.StartAsync();
        await using GatehouseHost gateway = await GatehouseHost.StartAsync(
            upstream.BaseAddress,
            authenticationMode: "Disabled");

        await Complete(gateway.Client);
        RequestRecord record = await WaitForRecordAsync(gateway);

        await Assert.That(record.VirtualKeyId).IsNull();
        await Assert.That(record.Organisation).IsNull();
    }

    private static Task<HttpResponseMessage> Complete(HttpClient client) =>
        client.PostAsync(
            "/v1/chat/completions",
            JsonContent.Create(new
            {
                model = "gpt-4o-mini",
                stream = false,
                messages = new[] { new { role = "user", content = "hi" } },
            }));

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
