using Gatehouse.Configuration;

namespace Gatehouse.Resilience;

/// <summary>
/// Tracks the health of one upstream and stops sending it traffic when it is failing.
/// </summary>
/// <remarks>
/// <para>
/// The point of a breaker in a gateway is not to be clever about failure. It is to stop
/// paying the timeout. When a provider is down, every request that reaches it costs the
/// caller the full upstream timeout before failing, and with a fallback chain configured
/// that cost is paid once per link. A breaker converts a 100-second failure into an
/// immediate one, which is what makes a fallback chain usable rather than a way of
/// multiplying latency.
/// </para>
/// <para>
/// <strong>Rolling window, not consecutive failures.</strong> Counting consecutive failures
/// is simpler and wrong for this workload: a provider degraded to a 40% error rate never
/// produces a long enough streak to trip, while a healthy provider under a brief network
/// blip does. This counts successes and failures in <see cref="ResilienceOptions.SamplingWindowSeconds"/>
/// worth of buckets and opens on a ratio, which detects partial degradation — the failure
/// mode providers actually exhibit.
/// </para>
/// <para>
/// <strong>Minimum throughput is load-bearing.</strong> Without it, the first request after
/// a quiet period failing once produces a 1/1 failure ratio and opens the circuit, so a
/// low-traffic deployment would spend its life with the breaker open on a provider that is
/// fine. The ratio is only consulted once the window holds enough calls for it to mean
/// something.
/// </para>
/// <para>
/// Timing uses <see cref="TimeProvider.GetTimestamp"/> arithmetic throughout — monotonic,
/// integer, and unaffected by a clock adjustment. A breaker that can be reset by NTP is a
/// breaker that fails open during exactly the kind of incident that also involves clock
/// skew.
/// </para>
/// <para>Instances are thread-safe.</para>
/// </remarks>
public sealed class CircuitBreaker
{
    /// <summary>
    /// How many buckets the sampling window is divided into.
    /// </summary>
    /// <remarks>
    /// Ten is enough that the window slides smoothly rather than lurching, and few enough
    /// that summing them on every call stays cheaper than the allocation it avoids. A single
    /// bucket would be a tumbling window, which loses every failure recorded just before a
    /// boundary — precisely the failures that were about to trip the breaker.
    /// </remarks>
    public const int BucketCount = 10;

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly int _minimumThroughput;
    private readonly double _failureRatio;
    private readonly long _bucketTicks;
    private readonly long _breakTicks;
    private readonly long[] _successes = new long[BucketCount];
    private readonly long[] _failures = new long[BucketCount];

    private int _head;
    private long _headStamp;
    private long _openedStamp;
    private CircuitState _state = CircuitState.Closed;
    private bool _probeInFlight;

    /// <summary>Creates a breaker for one upstream.</summary>
    /// <param name="key">The upstream this breaker guards, used in diagnostics.</param>
    /// <param name="options">The resilience configuration.</param>
    /// <param name="timeProvider">The clock.</param>
    public CircuitBreaker(string key, ResilienceOptions options, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        Key = key;
        _timeProvider = timeProvider;
        _minimumThroughput = options.MinimumThroughput;
        _failureRatio = options.FailureRatio;

        long frequency = timeProvider.TimestampFrequency;

        // Integer division deliberately: with the default 30-second window and ten buckets
        // this is exact, and for a window that does not divide evenly a fractionally short
        // bucket makes the window fractionally short, which is the harmless direction.
        _bucketTicks = Math.Max(1, frequency * options.SamplingWindowSeconds / BucketCount);
        _breakTicks = Math.Max(1, frequency * options.BreakDurationSeconds);

        _headStamp = timeProvider.GetTimestamp();
    }

    /// <summary>The upstream this breaker guards.</summary>
    public string Key { get; }

    /// <summary>The current state, for diagnostics and tests.</summary>
    public CircuitState State
    {
        get
        {
            lock (_gate)
            {
                // Reported without transitioning: asking a breaker how it is doing must not
                // consume the half-open probe that a real call is entitled to.
                if (_state == CircuitState.Open && IsBreakElapsed())
                {
                    return CircuitState.HalfOpen;
                }

                return _state;
            }
        }
    }

    /// <summary>
    /// Asks permission to call the upstream.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the call may proceed. When it does, the caller must report
    /// the outcome through <see cref="RecordSuccess"/> or <see cref="RecordFailure"/>,
    /// otherwise a half-open probe is never resolved and the circuit stops admitting traffic
    /// permanently.
    /// </returns>
    public bool TryAcquire()
    {
        lock (_gate)
        {
            Roll();

            switch (_state)
            {
                case CircuitState.Open:
                    if (!IsBreakElapsed())
                    {
                        return false;
                    }

                    _state = CircuitState.HalfOpen;
                    _probeInFlight = true;
                    return true;

                case CircuitState.HalfOpen:
                    if (_probeInFlight)
                    {
                        return false;
                    }

                    _probeInFlight = true;
                    return true;

                default:
                    return true;
            }
        }
    }

    /// <summary>Reports that the upstream answered.</summary>
    public void RecordSuccess()
    {
        lock (_gate)
        {
            Roll();

            if (_state == CircuitState.HalfOpen)
            {
                // The probe worked. Clear the window as well as the state: the failures that
                // opened the circuit are, by construction, still sitting in it, and counting
                // them against a recovered upstream would re-open the circuit on its next
                // stumble regardless of how healthy it now is.
                Clear();
                _state = CircuitState.Closed;
                _probeInFlight = false;
                return;
            }

            _successes[_head]++;
        }
    }

    /// <summary>Reports that the upstream failed in a way that counts against its health.</summary>
    /// <remarks>
    /// Only call this for failures the upstream is responsible for. A malformed request or a
    /// rejected credential says nothing about upstream health, and counting it would let one
    /// misconfigured caller open the circuit for everyone else.
    /// </remarks>
    public void RecordFailure()
    {
        lock (_gate)
        {
            Roll();

            if (_state == CircuitState.HalfOpen)
            {
                Open();
                return;
            }

            _failures[_head]++;

            long failures = 0;
            long total = 0;

            for (int i = 0; i < BucketCount; i++)
            {
                failures += _failures[i];
                total += _failures[i] + _successes[i];
            }

            if (total >= _minimumThroughput && (double)failures / total >= _failureRatio)
            {
                Open();
            }
        }
    }

    /// <summary>
    /// Reports that an acquired call ended without telling us anything about the upstream.
    /// </summary>
    /// <remarks>
    /// The case this exists for is the client hanging up mid-call. That is not an upstream
    /// failure and must not count as one, but it still has to release a half-open probe:
    /// a probe acquired and never resolved leaves the circuit admitting nothing for as long
    /// as the process lives, and it would take one cancelled request to get there.
    /// </remarks>
    public void RecordAbandoned()
    {
        lock (_gate)
        {
            _probeInFlight = false;
        }
    }

    private bool IsBreakElapsed() => _timeProvider.GetTimestamp() - _openedStamp >= _breakTicks;

    private void Open()
    {
        _state = CircuitState.Open;
        _openedStamp = _timeProvider.GetTimestamp();
        _probeInFlight = false;

        // The window is cleared on opening so that the break duration, not a stale bucket of
        // failures, decides when traffic returns.
        Clear();
    }

    private void Clear()
    {
        Array.Clear(_successes);
        Array.Clear(_failures);
        _head = 0;
        _headStamp = _timeProvider.GetTimestamp();
    }

    /// <summary>
    /// Advances the window to the present, zeroing the buckets that scrolled out of it.
    /// </summary>
    /// <remarks>Called under <see cref="_gate"/>.</remarks>
    private void Roll()
    {
        long elapsed = _timeProvider.GetTimestamp() - _headStamp;
        if (elapsed < _bucketTicks)
        {
            return;
        }

        long steps = elapsed / _bucketTicks;

        if (steps >= BucketCount)
        {
            // Idle for longer than the whole window: nothing in it is relevant any more.
            Clear();
            return;
        }

        for (long i = 0; i < steps; i++)
        {
            _head = (_head + 1) % BucketCount;
            _successes[_head] = 0;
            _failures[_head] = 0;
        }

        // Advanced by whole buckets rather than set to now, so buckets stay aligned to a
        // fixed grid and a burst of calls cannot repeatedly nudge the boundary forward.
        _headStamp += steps * _bucketTicks;
    }
}
