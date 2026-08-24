# Resilience: fallback chains and circuit breakers

Providers fail. This document says exactly what Gatehouse does about it, what it
deliberately does not do, and where the current implementation falls short — the
last part included because a resilience feature you have the wrong model of is
worse than one you know the edges of.

## The two features are one feature

A fallback chain without circuit breakers is often slower than no fallback at
all. When a provider is hard-down, every request pays its full timeout before
moving on, so a three-link chain against a 100-second timeout turns a
100-second failure into a 300-second one. Breakers without a fallback chain fail
fast to nowhere.

Together they do the thing an operator actually wants: notice a provider is
down, stop asking it, send the traffic somewhere that works.

## Fallback chains

Declared per route, in order of preference:

```json
"fast": {
  "Provider": "openai",
  "UpstreamModel": "gpt-4o-mini",
  "Fallbacks": ["claude-sonnet", "local"]
}
```

**Only failures the upstream is responsible for fall through.** 408, 429 and 5xx
are retryable. 400, 401, 403 and 402 are not:

- A malformed request will be malformed at the next provider too. Retrying it
  bills a second account to produce the same rejection.
- A rejected credential is a configuration error, and falling back hides it. You
  want the 401, not a silent success from somewhere else.
- 402 — out of credit — will still be out of credit on the retry.

**Chains are not transitive.** If `a` falls back to `b` and `b` falls back to
`c`, a request for `a` tries `a`, then `b`, and stops. The list on one entry is
the whole chain, so a reviewer reading a single route sees every upstream a
request for it can reach. Transitive chains read as though they compose and then
produce paths nobody declared.

**`MaxAttempts` is a rail, not a tuning knob.** It caps how many upstream calls
one client request can cause. Without it, a long chain against a broad outage
multiplies one request into that many billed calls.

### Cross-vendor fallback changes which model answers

`fast` falling back to `claude-sonnet` means a caller who asked for a GPT model
may get a Claude one, with different tokenisation, different refusal behaviour
and different output shape. That is the right trade when the caller wants *an
answer*. It is the wrong trade when output has to be reproducible or when a
downstream parser is tuned to one model's habits. Gatehouse cannot tell which
you have, so it does what you configure.

## Circuit breakers

**Keyed per provider *and* upstream model**, not per provider. The failure
domain is the upstream resource: on Azure OpenAI quota is assigned per
deployment, so a saturated `gpt-4o` deployment must not take out the
`gpt-4o-mini` deployment beside it — least of all when that is the obvious
fallback target.

**Rolling window, not consecutive failures.** Counting streaks is simpler and
wrong here. A provider degraded to a 40% error rate never produces a long enough
streak to trip, while a healthy provider hitting a brief blip does. Gatehouse
counts successes and failures across a sliding window and opens on a ratio,
which detects partial degradation — the failure mode providers actually exhibit.

**Minimum throughput is load-bearing.** Without it, the first request after a
quiet period failing once is a 100% failure rate, and a low-traffic deployment
would live with its breaker open on a provider that is fine.

| Setting | Default | Meaning |
| ------- | ------- | ------- |
| `FailureRatio` | `0.5` | Open when this fraction of the window failed |
| `MinimumThroughput` | `10` | Ignore the ratio until the window holds this many calls |
| `SamplingWindowSeconds` | `30` | How much history the ratio is computed over |
| `BreakDurationSeconds` | `15` | How long an open circuit refuses traffic |
| `MaxAttempts` | `4` | Most upstream calls one client request may cause |

After the break, **one** probe is admitted — not one per waiting caller. Letting
every queued request through at that moment reproduces the thundering herd at
the precise moment the upstream is least able to absorb it. A successful probe
closes the circuit and clears the window; a failed one re-opens it for another
full break.

A rejection does not count against upstream health. The upstream was reachable
and answered; it simply said no. Counting it would let one caller sending
malformed requests open the circuit for everybody else.

## Streaming: the boundary that matters

Gatehouse pulls the first chunk of a streamed completion *before* writing any
response header. That window — after the upstream has been asked, before the
client has been told anything — is the only place where failing over is honest,
and it is where all streamed fallback happens.

Once the first chunk is handed over, the 200 is committed and bytes are on their
way. Failing over after that would mean either replaying tokens the caller
already received or splicing two different completions into one that neither
model produced. **So mid-stream failures do not fall back.** They surface as an
in-band SSE error event on a response that has already begun.

This boundary is encoded in the type system rather than in a comment: the
dispatcher returns an enumerator already positioned on the first chunk, so
"fallback is still possible" and "fallback is no longer possible" are different
states of the program, not different lines of a method.

## Known gaps

Stated because you should not have to discover them:

- **A failed attempt's token cost is not recorded.** If a provider generates
  tokens and *then* fails, those tokens were spent, but providers do not report
  usage on error responses, so Gatehouse cannot know. The request log records
  the successful attempt only. Exposure is small — streamed fallback happens
  before the first chunk — but it is not zero, and it is a real reason a
  chargeback report can read slightly low against an invoice.
- **Mid-stream failures do not count against a breaker.** The breaker judges
  "did this upstream start answering". A provider that reliably dies at chunk
  five would never trip it.
- **Breaker state is per process.** Two Gatehouse instances behind a load
  balancer learn a provider is down independently. Shared state is a Phase 3
  question, arriving with HA.
- **No retry against the same route.** A failure moves to the next route or
  fails; it never retries the upstream it just tried. Retrying the same provider
  during an incident is how a gateway becomes the incident.

## Telemetry

| Instrument | Use |
| ---------- | --- |
| `gatehouse.route.fallbacks` | Requests served by a fallback, tagged with alias, serving provider and depth |
| `gatehouse.circuit_breaker.rejections` | Calls skipped because a circuit was open |

Do not alert on `gatehouse.route.fallbacks > 0` — a fallback firing is the
feature working. Alert on a **sustained rate**, which means the primary provider
is unhealthy and nobody has been told, because from the callers' point of view
nothing broke.

## Turning it off

`FallbacksEnabled: false` is the way to make an incident reproducible. With
fallbacks live, the same request can succeed against a different provider and
the primary's failure exists only in telemetry.

Both features are on by default, and both are inert on a healthy deployment: a
route with no `Fallbacks` never falls back, and a breaker only opens once the
sampling window holds enough calls for the ratio to mean something.
