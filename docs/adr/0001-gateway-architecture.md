# ADR 0001 — Gateway architecture

- **Status:** Accepted
- **Date:** 2026-08-23
- **Phase:** 0 (Foundations)

## Context

Gatehouse must sit in the request path between applications and LLM providers,
enforce governance, and meter usage accurately — while being deployable as a
single artifact into environments that will not accept a Python runtime, a Redis,
or a Postgres as a precondition.

Two things follow from the positioning. Governance requires understanding the
request, which means parsing bodies. Enterprise deployment requires a low
operational footprint, which argues for touching as little as possible. Those pull
in opposite directions, and this ADR records how the tension is resolved.

## Decisions

### 1. Two proxy paths, not one

**Decided:** an inspected path for OpenAI-compatible traffic, and a YARP
passthrough for provider-native traffic.

The `/v1/chat/completions` endpoint is a first-class ASP.NET Core endpoint. It
deserializes the request, resolves the model alias, meters the response and writes
an audit record. That costs a body parse per request, and it is the only way to
enforce a budget or produce a chargeback line.

YARP handles `/passthrough/{provider}/**`, forwarding bytes without understanding
them. It exists because some provider features have no OpenAI-compatible
expression, and the realistic alternative to an audited escape hatch is
applications quietly bypassing the gateway. It is **off by default**, per provider,
and requests through it are recorded as *unmetered* — a visible gap in a chargeback
report rather than an invisible one.

The second path also derisks Phase 3: the MCP gateway proxies traffic that is not
chat completions at all, and proving both models coexist in one host now is
cheaper than discovering an incompatibility in month nine.

**Rejected:** YARP for everything, with transforms doing the metering. Transforms
operate on a stream the proxy is trying not to buffer; reconstructing token counts
from it is exactly the fragile arrangement that produces the metering defects this
project is positioned against.

### 2. Providers are hand-written; `IChatClient` is the escape hatch

**Decided:** the seven first-class providers implement `IChatProvider` directly.
`ChatClientProvider` adapts any `Microsoft.Extensions.AI` `IChatClient` for
everything else.

`Microsoft.Extensions.AI` is the abstraction .NET is standardising on, and
consuming it is cheaper than competing with it. But it is a lowest common
denominator by design, and the fields it does not model — cached prompt tokens,
provider-specific finish reasons, the raw usage block finance needs for invoice
reconciliation — are precisely the ones a governance gateway cannot lose.

So: hand-written where accuracy is a product requirement, adapter where breadth is
worth more than fidelity.

### 3. NativeAOT is a constraint from the first commit, not a later goal

**Decided:** `IL2026`, `IL2091` and `IL3050` are build errors. Serialization is
source-generated. Configuration binding is source-generated.

Retrofitting AOT compatibility onto a codebase means auditing every reflection
call in it. Enforcing it from commit one means each individual violation is caught
by the compiler while the fix is still small. The Phase 0 gate — a published binary
that serves a streamed completion — is verified in CI on every pull request, not
at release time.

Two consequences are load-bearing and easy to undo by accident:

- Configuration options use `set`, not `init`. The binding generator emits plain
  assignments and silently cannot populate an init-only property, producing a
  gateway that starts, reports healthy, and rejects every request.
- `ValidateDataAnnotations` is not used. It reflects over the options type, so in
  the AOT build the attributes would be unenforced. `GatehouseOptionsValidator`
  performs the same checks in code the linker can follow.

### 4. SQLite is the default store, and no external database is ever required

**Decided:** SQLite with WAL, writes batched off the request path through a bounded
channel. Alternative stores are pluggable via `IRequestLogStore`; adding a
*required* dependency needs an RFC.

An evaluator who must stand up Postgres and Redis before seeing a completion
proxied usually stops before finishing. Recording is for billing and audit, both of
which tolerate a short delay; inference does not tolerate waiting on a disk write.

The queue is bounded rather than unbounded: an unbounded queue in front of a slow
disk converts a latency problem into an out-of-memory crash. When it is full,
writers wait — dropping records would put silent holes in a chargeback report.

### 5. Streaming defers response headers until the first chunk exists

**Decided:** the endpoint pulls the first chunk from the provider *before* writing
any header.

Until a chunk exists the status line is still ours to choose. An upstream that
rejects the request outright therefore produces a real 4xx or 5xx, rather than a
200 whose body immediately announces a failure. That distinction matters to every
load balancer, retry policy and error-rate dashboard between the caller and here.
Once headers are on the wire the option is gone, so a mid-stream failure is
reported in-band instead.

### 6. Libraries multi-target; applications target current LTS

**Decided:** libraries build for `net8.0;net9.0;net10.0`. `Gatehouse.Server`
targets `net10.0`.

.NET 8 and 9 both leave support on 2026-11-10 — before this roadmap reaches the end
of Phase 1. Shipping a gateway that a regulated-industry evaluator can see is on an
unsupported runtime fails the Phase 2 gate on the first checklist item. The
libraries still target the older frameworks so a shop on .NET 8 can reference
`Gatehouse.Core` and, from Phase 3, the guardrails plugin contract.

### 7. Timeouts are applied per call, never via `HttpClient.Timeout`

**Decided:** `HttpClient.Timeout` is `InfiniteTimeSpan`. Providers apply the
configured timeout through a linked `CancellationTokenSource`, and disarm it once
response headers arrive on a streamed call.

`HttpClient.Timeout` covers the whole exchange including the response body. On a
streamed completion that aborts generations which legitimately run long — the
expensive ones, after the caller has already been billed for the tokens produced so
far. This is the single most consequential detail in the provider implementation and
the easiest to reintroduce.

## Consequences

**Good.** One build, three hosts, no runtime to install. Governance has a place to
live from the start. AOT and metering accuracy are enforced by CI rather than by
discipline.

**Costs.** Two proxy paths are more code than one. Multi-targeting roughly doubles
library build time. Source-generated serialization means adding a wire type is two
edits instead of one. `set`-based options give up compile-time immutability on the
configuration surface.

**Revisit if:** `Microsoft.Extensions.AI` grows a usage model rich enough for
invoice reconciliation, which would let the hand-written providers shrink to
translation-only concerns.
