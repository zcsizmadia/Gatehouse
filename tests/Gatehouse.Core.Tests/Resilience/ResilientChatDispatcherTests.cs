using Gatehouse.Configuration;
using Gatehouse.Providers;
using Gatehouse.Resilience;
using Gatehouse.Routing;
using Gatehouse.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Gatehouse.Tests.Resilience;

/// <summary>Tests for fallback chains and their interaction with circuit breakers.</summary>
public class ResilientChatDispatcherTests
{
    [Test]
    public async Task Falls_back_to_the_next_route_when_the_primary_fails_retryably()
    {
        var primaryProvider = new ScriptedProvider("p1").ThenFailsRetryably();
        var backupProvider = new ScriptedProvider("p2").ThenSucceeds("from the backup");

        (IChatDispatcher dispatcher, ModelRoute primary) = Build(primaryProvider, backupProvider);

        BufferedDispatch dispatch = await dispatcher.CompleteAsync(Request(), primary, default);

        await Assert.That(dispatch.Route.Provider).IsEqualTo("p2");
        await Assert.That(dispatch.Response.Choices[0].Message.Content).IsEqualTo("from the backup");
        await Assert.That(primaryProvider.Calls).IsEqualTo(1);
        await Assert.That(backupProvider.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Reports_the_route_that_answered_rather_than_the_one_that_was_asked_for()
    {
        // This is what the request log is attributed to. Reporting the primary route after a
        // fallback would bill an account that was never called.
        var primaryProvider = new ScriptedProvider("p1").ThenFailsRetryably();
        var backupProvider = new ScriptedProvider("p2").ThenSucceeds();

        (IChatDispatcher dispatcher, ModelRoute primary) = Build(primaryProvider, backupProvider);

        BufferedDispatch dispatch = await dispatcher.CompleteAsync(Request(), primary, default);

        await Assert.That(dispatch.Route.Alias).IsEqualTo("backup");
        await Assert.That(dispatch.Attempts.Count).IsEqualTo(2);
        await Assert.That(dispatch.Attempts[0].Outcome).IsEqualTo(AttemptOutcome.RetryableFailure);
        await Assert.That(dispatch.Attempts[1].Outcome).IsEqualTo(AttemptOutcome.Succeeded);
    }

    [Test]
    public async Task Does_not_fall_back_from_a_failure_the_caller_has_to_fix()
    {
        // A malformed request will be malformed at the next provider too. Trying it there
        // bills a second account to produce the same rejection.
        var primaryProvider = new ScriptedProvider("p1").ThenFailsTerminally();
        var backupProvider = new ScriptedProvider("p2").ThenSucceeds();

        (IChatDispatcher dispatcher, ModelRoute primary) = Build(primaryProvider, backupProvider);

        ProviderException? thrown = await Assert.ThrowsAsync<ProviderException>(
            async () => await dispatcher.CompleteAsync(Request(), primary, default));

        await Assert.That(thrown!.Message).Contains("malformed");
        await Assert.That(backupProvider.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Reports_the_last_upstream_error_when_every_route_fails()
    {
        var primaryProvider = new ScriptedProvider("p1").ThenFailsRetryably("primary is down");
        var backupProvider = new ScriptedProvider("p2").ThenFailsRetryably("backup is down too");

        (IChatDispatcher dispatcher, ModelRoute primary) = Build(primaryProvider, backupProvider);

        ProviderException? thrown = await Assert.ThrowsAsync<ProviderException>(
            async () => await dispatcher.CompleteAsync(Request(), primary, default));

        // The last real error, not a generic "all routes failed" that discards the only
        // information anyone reading the log actually needs.
        await Assert.That(thrown!.Message).Contains("backup is down too");
        await Assert.That(thrown.Message).Contains("All 2 routes");
    }

    [Test]
    public async Task Honours_a_single_route_when_fallbacks_are_switched_off()
    {
        var primaryProvider = new ScriptedProvider("p1").ThenFailsRetryably();
        var backupProvider = new ScriptedProvider("p2").ThenSucceeds();

        (IChatDispatcher dispatcher, ModelRoute primary) =
            Build(primaryProvider, backupProvider, options: new ResilienceOptions { FallbacksEnabled = false });

        await Assert.ThrowsAsync<ProviderException>(
            async () => await dispatcher.CompleteAsync(Request(), primary, default));

        await Assert.That(backupProvider.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task A_client_disconnect_stops_the_chain_instead_of_billing_a_second_provider()
    {
        var primaryProvider = new ScriptedProvider("p1").ThenCancels();
        var backupProvider = new ScriptedProvider("p2").ThenSucceeds();

        (IChatDispatcher dispatcher, ModelRoute primary) = Build(primaryProvider, backupProvider);

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await dispatcher.CompleteAsync(Request(), primary, default));

        await Assert.That(backupProvider.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Skips_a_route_whose_circuit_is_open_without_calling_it()
    {
        // The point of the breaker: the dead provider is not called at all, so the caller
        // does not pay its timeout before reaching the healthy one.
        var primaryProvider = new ScriptedProvider("p1");
        var backupProvider = new ScriptedProvider("p2").ThenSucceeds();

        for (int i = 0; i < 12; i++)
        {
            primaryProvider.ThenFailsRetryably();
        }

        (IChatDispatcher dispatcher, ModelRoute primary) = Build(primaryProvider, backupProvider);

        // Drive the primary's circuit open through the dispatcher itself, then confirm the
        // next request never reaches it.
        for (int i = 0; i < 12; i++)
        {
            backupProvider.ThenSucceeds();
            await dispatcher.CompleteAsync(Request(), primary, default);
        }

        int callsBefore = primaryProvider.Calls;
        BufferedDispatch dispatch = await dispatcher.CompleteAsync(Request(), primary, default);

        await Assert.That(primaryProvider.Calls).IsEqualTo(callsBefore);
        await Assert.That(dispatch.Route.Provider).IsEqualTo("p2");
        await Assert.That(dispatch.Attempts[0].Outcome).IsEqualTo(AttemptOutcome.CircuitOpen);
    }

    [Test]
    public async Task Reports_a_retryable_service_unavailable_when_every_circuit_is_open()
    {
        var onlyProvider = new ScriptedProvider("p1");

        for (int i = 0; i < 12; i++)
        {
            onlyProvider.ThenFailsRetryably();
        }

        var router = new StubRouter();
        ModelRoute primary = router.Add("fast", "p1");
        ResilientChatDispatcher dispatcher = Dispatcher(router, new ResilienceOptions(), onlyProvider);

        for (int i = 0; i < 12; i++)
        {
            await Assert.ThrowsAsync<ProviderException>(
                async () => await dispatcher.CompleteAsync(Request(), primary, default));
        }

        ProviderException? thrown = await Assert.ThrowsAsync<ProviderException>(
            async () => await dispatcher.CompleteAsync(Request(), primary, default));

        // The message has to say this is the gateway shedding load, not the request being
        // wrong, or the caller spends the outage debugging their own payload.
        await Assert.That(thrown!.StatusCode).IsEqualTo(System.Net.HttpStatusCode.ServiceUnavailable);
        await Assert.That(thrown.Message).Contains("circuit breaker");
    }

    [Test]
    public async Task A_rejected_request_does_not_count_against_upstream_health()
    {
        // One caller sending malformed requests must not be able to open the circuit for
        // everyone else. The upstream answered every time; it just said no.
        var onlyProvider = new ScriptedProvider("p1");

        for (int i = 0; i < 30; i++)
        {
            onlyProvider.ThenFailsTerminally();
        }

        var router = new StubRouter();
        ModelRoute primary = router.Add("fast", "p1");
        ResilientChatDispatcher dispatcher = Dispatcher(router, new ResilienceOptions(), onlyProvider);

        for (int i = 0; i < 25; i++)
        {
            await Assert.ThrowsAsync<ProviderException>(
                async () => await dispatcher.CompleteAsync(Request(), primary, default));
        }

        // Still being called, so the circuit never opened.
        await Assert.That(onlyProvider.Calls).IsEqualTo(25);
    }

    [Test]
    public async Task Streams_from_the_fallback_when_the_primary_fails_before_its_first_chunk()
    {
        var primaryProvider = new ScriptedProvider("p1").ThenFailsRetryably();
        var backupProvider = new ScriptedProvider("p2").ThenSucceeds("streamed from backup");

        (IChatDispatcher dispatcher, ModelRoute primary) = Build(primaryProvider, backupProvider);

        await using StreamedDispatch dispatch = await dispatcher.StreamAsync(Request(), primary, default);

        await Assert.That(dispatch.Route.Provider).IsEqualTo("p2");
        await Assert.That(dispatch.HasFirstChunk).IsTrue();

        // Positioned on the first chunk, not before it. A dispatcher that advanced the
        // enumerator again here would silently drop the first token of every completion.
        await Assert.That(dispatch.Chunks.Current.Choices[0].Delta.Content).IsEqualTo("streamed from backup");
    }

    [Test]
    public async Task Disposes_the_enumerator_of_a_stream_attempt_that_failed()
    {
        // Each failed attempt holds an upstream response stream, and therefore a pooled
        // connection. Leaking one per attempt is worst during exactly the outage that causes
        // the attempts.
        var primaryProvider = new ScriptedProvider("p1").ThenFailsRetryably();
        var backupProvider = new ScriptedProvider("p2").ThenSucceeds();

        (IChatDispatcher dispatcher, ModelRoute primary) = Build(primaryProvider, backupProvider);

        await using StreamedDispatch dispatch = await dispatcher.StreamAsync(Request(), primary, default);

        await Assert.That(primaryProvider.Disposals).IsEqualTo(1);
    }

    [Test]
    public async Task Does_not_fall_back_once_the_first_chunk_has_been_handed_over()
    {
        // The response status is committed and bytes are on their way to the client. Failing
        // over here would mean either replaying tokens the caller already saw or splicing two
        // completions together, so the mid-stream failure has to surface as itself.
        var primaryProvider = new ScriptedProvider("p1").ThenFailsMidStream();
        var backupProvider = new ScriptedProvider("p2").ThenSucceeds();

        (IChatDispatcher dispatcher, ModelRoute primary) = Build(primaryProvider, backupProvider);

        await using StreamedDispatch dispatch = await dispatcher.StreamAsync(Request(), primary, default);

        await Assert.That(dispatch.Route.Provider).IsEqualTo("p1");
        await Assert.That(dispatch.HasFirstChunk).IsTrue();

        // The failure arrives on the next advance, from the provider, un-intercepted.
        await Assert.ThrowsAsync<ProviderException>(async () => await dispatch.Chunks.MoveNextAsync());
        await Assert.That(backupProvider.Calls).IsEqualTo(0);
    }

    private static ChatCompletionRequest Request() => new()
    {
        Model = "fast",
        Messages = [new ChatMessage { Role = ChatRoles.User, Content = "hello" }],
    };

    /// <summary>Builds a two-link chain: alias 'fast' on p1, falling back to 'backup' on p2.</summary>
    private static (IChatDispatcher Dispatcher, ModelRoute Primary) Build(
        ScriptedProvider primaryProvider,
        ScriptedProvider backupProvider,
        ResilienceOptions? options = null)
    {
        var router = new StubRouter();
        router.Add("backup", backupProvider.Name);
        ModelRoute primary = router.Add("fast", primaryProvider.Name, "backup");

        return (Dispatcher(router, options ?? new ResilienceOptions(), primaryProvider, backupProvider), primary);
    }

    private static ResilientChatDispatcher Dispatcher(
        StubRouter router,
        ResilienceOptions resilience,
        params IChatProvider[] providers)
    {
        var gatehouseOptions = Options.Create(new GatehouseOptions { Resilience = resilience });
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero));

        return new ResilientChatDispatcher(
            router,
            new ProviderRegistry(providers),
            new CircuitBreakerRegistry(gatehouseOptions, time),
            gatehouseOptions,
            NullLogger<ResilientChatDispatcher>.Instance);
    }
}
