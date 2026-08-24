using Gatehouse.Configuration;
using Gatehouse.Resilience;
using Microsoft.Extensions.Time.Testing;

namespace Gatehouse.Tests.Resilience;

/// <summary>Tests for the per-upstream circuit breaker.</summary>
public class CircuitBreakerTests
{
    /// <summary>
    /// A window that divides evenly into <see cref="CircuitBreaker.BucketCount"/> buckets, so
    /// that the tests can advance time by whole buckets and reason about which ones expire.
    /// </summary>
    private const int WindowSeconds = 10;

    [Test]
    public async Task Starts_closed()
    {
        (CircuitBreaker breaker, _) = Build();

        await Assert.That(breaker.State).IsEqualTo(CircuitState.Closed);
        await Assert.That(breaker.TryAcquire()).IsTrue();
    }

    [Test]
    public async Task Stays_closed_below_the_minimum_throughput_however_bad_the_ratio()
    {
        // Nine failures out of nine is a 100% failure rate. It still must not open: on a
        // gateway serving a handful of requests an hour, one bad minute would otherwise take
        // a healthy provider out of rotation for every request that followed.
        (CircuitBreaker breaker, _) = Build(minimumThroughput: 10);

        for (int i = 0; i < 9; i++)
        {
            breaker.RecordFailure();
        }

        await Assert.That(breaker.State).IsEqualTo(CircuitState.Closed);
    }

    [Test]
    public async Task Opens_when_the_window_holds_enough_calls_and_enough_of_them_failed()
    {
        (CircuitBreaker breaker, _) = Build(minimumThroughput: 10, failureRatio: 0.5);

        for (int i = 0; i < 10; i++)
        {
            breaker.RecordFailure();
        }

        await Assert.That(breaker.State).IsEqualTo(CircuitState.Open);
        await Assert.That(breaker.TryAcquire()).IsFalse();
    }

    [Test]
    public async Task Stays_closed_when_the_provider_is_degraded_but_below_the_ratio()
    {
        // 5 failures in 12 calls is 42%: a provider having a bad time, not a provider that is
        // down. Taking it out of rotation would turn a partial degradation into a total one.
        (CircuitBreaker breaker, _) = Build(minimumThroughput: 10, failureRatio: 0.5);

        for (int i = 0; i < 7; i++)
        {
            breaker.RecordSuccess();
        }

        for (int i = 0; i < 5; i++)
        {
            breaker.RecordFailure();
        }

        await Assert.That(breaker.State).IsEqualTo(CircuitState.Closed);
    }

    [Test]
    public async Task Forgets_failures_that_scroll_out_of_the_sampling_window()
    {
        // This is what makes it a rolling window rather than a running total. Nine failures,
        // then a wait longer than the window, then one more failure: a breaker that never
        // expired anything would see 10/10 and open.
        (CircuitBreaker breaker, FakeTimeProvider time) = Build(minimumThroughput: 10);

        for (int i = 0; i < 9; i++)
        {
            breaker.RecordFailure();
        }

        time.Advance(TimeSpan.FromSeconds(WindowSeconds + 1));
        breaker.RecordFailure();

        await Assert.That(breaker.State).IsEqualTo(CircuitState.Closed);
    }

    [Test]
    public async Task Counts_failures_that_are_still_inside_the_window()
    {
        // The other half of the previous test, and the one that would pass if expiry were
        // implemented as "clear everything on any elapsed time". Nine failures, then less
        // than a window later, one more: all ten are still current, so it opens.
        (CircuitBreaker breaker, FakeTimeProvider time) = Build(minimumThroughput: 10);

        for (int i = 0; i < 9; i++)
        {
            breaker.RecordFailure();
        }

        time.Advance(TimeSpan.FromSeconds(WindowSeconds / 2));
        breaker.RecordFailure();

        await Assert.That(breaker.State).IsEqualTo(CircuitState.Open);
    }

    [Test]
    public async Task Admits_one_probe_after_the_break_elapses_and_no_more()
    {
        (CircuitBreaker breaker, FakeTimeProvider time) = Build(breakSeconds: 15);
        Trip(breaker);

        await Assert.That(breaker.TryAcquire()).IsFalse();

        time.Advance(TimeSpan.FromSeconds(15));

        await Assert.That(breaker.TryAcquire()).IsTrue();

        // The second caller is turned away while the first probe is outstanding. Letting
        // every waiting request through at this moment is the thundering herd that took the
        // upstream down in the first place.
        await Assert.That(breaker.TryAcquire()).IsFalse();
    }

    [Test]
    public async Task Reading_the_state_does_not_consume_the_probe()
    {
        (CircuitBreaker breaker, FakeTimeProvider time) = Build(breakSeconds: 15);
        Trip(breaker);
        time.Advance(TimeSpan.FromSeconds(15));

        // A health endpoint or a log line asking how the breaker is doing must not spend the
        // one probe a real request is entitled to.
        await Assert.That(breaker.State).IsEqualTo(CircuitState.HalfOpen);
        await Assert.That(breaker.State).IsEqualTo(CircuitState.HalfOpen);

        await Assert.That(breaker.TryAcquire()).IsTrue();
    }

    [Test]
    public async Task A_successful_probe_closes_the_circuit()
    {
        (CircuitBreaker breaker, FakeTimeProvider time) = Build(breakSeconds: 15);
        Trip(breaker);
        time.Advance(TimeSpan.FromSeconds(15));

        breaker.TryAcquire();
        breaker.RecordSuccess();

        await Assert.That(breaker.State).IsEqualTo(CircuitState.Closed);
        await Assert.That(breaker.TryAcquire()).IsTrue();
    }

    [Test]
    public async Task A_successful_probe_clears_the_failures_that_opened_the_circuit()
    {
        // Without this, the failures that tripped the breaker are still sitting in the window
        // when it closes, so the very next failure re-opens it — and a recovered provider
        // would flap between open and closed indefinitely.
        (CircuitBreaker breaker, FakeTimeProvider time) = Build(minimumThroughput: 10, breakSeconds: 15);
        Trip(breaker);
        time.Advance(TimeSpan.FromSeconds(15));

        breaker.TryAcquire();
        breaker.RecordSuccess();
        breaker.RecordFailure();

        await Assert.That(breaker.State).IsEqualTo(CircuitState.Closed);
    }

    [Test]
    public async Task A_failed_probe_re_opens_the_circuit_for_another_full_break()
    {
        (CircuitBreaker breaker, FakeTimeProvider time) = Build(breakSeconds: 15);
        Trip(breaker);
        time.Advance(TimeSpan.FromSeconds(15));

        breaker.TryAcquire();
        breaker.RecordFailure();

        await Assert.That(breaker.State).IsEqualTo(CircuitState.Open);
        await Assert.That(breaker.TryAcquire()).IsFalse();

        // The break clock restarts from the failed probe rather than from the original trip,
        // so a provider that is still down is not re-probed on every request.
        time.Advance(TimeSpan.FromSeconds(14));
        await Assert.That(breaker.TryAcquire()).IsFalse();

        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(breaker.TryAcquire()).IsTrue();
    }

    [Test]
    public async Task An_abandoned_probe_is_released_without_counting_against_the_upstream()
    {
        // A client hanging up during the probe call. The upstream told us nothing, so the
        // circuit must neither close nor re-open — but the probe has to be released, or one
        // cancelled request locks the circuit shut for the life of the process.
        (CircuitBreaker breaker, FakeTimeProvider time) = Build(breakSeconds: 15);
        Trip(breaker);
        time.Advance(TimeSpan.FromSeconds(15));

        breaker.TryAcquire();
        breaker.RecordAbandoned();

        await Assert.That(breaker.State).IsEqualTo(CircuitState.HalfOpen);
        await Assert.That(breaker.TryAcquire()).IsTrue();
    }

    /// <summary>Drives the breaker to <see cref="CircuitState.Open"/>.</summary>
    private static void Trip(CircuitBreaker breaker)
    {
        for (int i = 0; i < 20; i++)
        {
            breaker.RecordFailure();
        }
    }

    private static (CircuitBreaker Breaker, FakeTimeProvider Time) Build(
        int minimumThroughput = 10,
        double failureRatio = 0.5,
        int breakSeconds = 15)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero));

        var options = new ResilienceOptions
        {
            MinimumThroughput = minimumThroughput,
            FailureRatio = failureRatio,
            SamplingWindowSeconds = WindowSeconds,
            BreakDurationSeconds = breakSeconds,
        };

        return (new CircuitBreaker("test/model", options, time), time);
    }
}
