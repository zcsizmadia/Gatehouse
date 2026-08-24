# Changelog

All notable changes to Gatehouse are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Pre-1.0 the public API may change in any minor release. The API-stability
guarantee takes effect at v1.0 — see the [roadmap](./ROADMAP.md).

## [Unreleased]

## [0.1.0-beta.1] — 2026-08-24

First release. Everything in Phase 1 of the [roadmap](./ROADMAP.md), and nothing
from Phase 2 — read the [capability table in SECURITY.md](./SECURITY.md#what-is-actually-implemented)
before deploying this, because the governance features Gatehouse exists for are
not in it yet. There are **no budgets, no spend limits, no per-key model
restrictions, no RBAC, no SSO and no rate limiting**: a valid key can call any
configured model without limit.

Beta rather than alpha because the surface is now the one Phase 1 planned and the
supply-chain promises are all real. Pre-1.0 still means the API may change in any
minor release.

### Added

**Phase 1 — Amazon Bedrock.** The seventh and final provider.

- Built on `AWSSDK.BedrockRuntime`, the only vendor SDK in Gatehouse. ADR 0002
  predicted it would pass the NativeAOT gate; that has now been verified rather
  than assumed — an AOT publish of the whole gateway with Bedrock in it emits zero
  IL warnings with `IL2026` and `IL3050` as errors, and ILC analysed the full graph.
- Uses the **Converse** API, not InvokeModel. InvokeModel would have meant one
  request shape, response shape and usage parser per model family inside a single
  provider — a provider registry hiding inside a provider, defeating the seven
  provider cap from the inside. With Converse, a new model family on Bedrock needs
  no Gatehouse change.
- **Cache-token semantics are derived, not assumed.** Providers disagree about
  whether cache tokens are additive to the input count or a subset of it, and
  guessing wrong silently mis-meters every cached request. Bedrock reports a total
  alongside the parts, so the convention is worked out from the provider's own
  arithmetic — which matters here because Bedrock is the one provider that could
  not be verified against a live endpoint during development.
- Addressed by `Region`, never by `BaseUrl`. Setting a BaseUrl is a startup error
  rather than silently ignored, and the region is never inferred from the ambient
  `AWS_REGION`.
- With no credential variables set, the AWS credential chain resolves an IAM role —
  the same story as Entra managed identity, storing no credential at all. Setting
  only one of the two variables is a startup error.
- The SDK's own retries are switched off (`MaxErrorRetry = 0`). Two retry layers
  multiply one client request into up to nine billed upstream calls and hide the
  failures from the circuit breaker.
- Streaming goes through `IAsyncEnumerable`; the SDK's synchronous and
  callback-based alternatives would pin a thread per in-flight completion.
- Known gaps, including that this is **not verified against live Bedrock**, are in
  [docs/providers/bedrock.md](./docs/providers/bedrock.md).

**Phase 1 — exact-match response caching.**

- Repeated identical requests can be served from memory instead of from a
  provider. **Exact match only**: Gatehouse never serves an answer to a question
  that merely resembles a stored one. Semantic caching is a Phase 4 question, and
  only with honest measurements of how often it is wrong.
- **Off by default.** Caching changes observable behaviour — repeated requests stop
  being sampled and start returning a fixed answer — and a gateway that starts
  doing that because somebody upgraded has changed a caller's output without being
  asked.
- **A cache hit is never counted as provider usage.** Its tokens are real and
  nobody was billed for them, so the request log records `served_from_cache`, the
  usage aggregation excludes those rows from every token total, and reports them as
  `TokensAvoided` instead. Counting them as spend would inflate recorded usage by
  exactly the amount the cache saved.
- `Confidence` is now measured against billable requests, so a more effective cache
  no longer drags the figure down.
- `stream` is deliberately not part of the cache key: the content of an answer does
  not depend on how it is delivered, so streamed and buffered callers share one
  entry. Streamed hits are replayed as server-sent events.
- Never cached: failures, streams that ended without a finish reason, multi-choice
  responses, and anything over `MaxResponseBytes`.
- Bounded by entry count and by response size, evicting least-recently-used.
  Reading does not extend an entry's life.
- `ScopeToOrganisation` is on by default, so one tenant's cached answers are not
  served to another.
- `X-Gatehouse-Cache: hit` on a cached response.
- Schema version 4. New metrics: `gatehouse.cache.hits`, `.misses`,
  `.tokens_avoided`, `.evictions`, `.skipped_too_large`.
- Benchmarks in `bench/Gatehouse.Benchmarks`, per the standing rule. They earned
  their keep immediately: they showed a 16 KB prompt allocating ~50 KB per request
  to be hashed, which hashing incrementally through a pooled block cut to 696 B —
  constant in prompt length — and made 42% faster. See
  [docs/caching.md](./docs/caching.md).

**Phase 1 — metering and invoice reconciliation.**

- `gatehouse usage summary` and `gatehouse usage reconcile`. Reconcile takes a CSV
  export from the provider's own usage dashboard, compares it against the request
  log, and reports each line as balanced, within known gaps, unexplained, missing
  from the statement, or — worst — billed by the provider for a model Gatehouse has
  never seen, which means a credential is in use outside the gateway entirely.
- It does **not** try to make the numbers agree. It quantifies the disagreement,
  bounds how much of it Gatehouse's own gaps could account for, and reports the
  remainder as needing investigation. A reconciliation that always balances is not
  doing anything.
- Gaps are only counted in the direction they can act: unreadable requests can
  only make a provider's figure larger, so they never excuse a statement that
  comes in *lower* — that direction points at a double count, which is the failure
  mode that overcharges an internal team.
- Cache-read and cache-write token counts are now persisted. Providers were already
  reporting them and Gatehouse was discarding them at the storage boundary: cache
  reads bill at roughly a tenth of the input rate and cache writes at a premium, so
  a prompt total that cannot separate them can detect a variance against an invoice
  but not explain it.
- `metered` is now an explicit column. Unmetered passthrough traffic was previously
  identified by a `(passthrough:…)` prefix on the requested model — a naming
  convention standing in for the single largest category of legitimately
  unexplained spend.
- Every summary prints a confidence figure: the share of requests whose tokens the
  provider reported. A total printed without it is the number that ends up in a
  spreadsheet as though it were exact.
- `reconcile` exits 1 on findings and 2 on bad input, so it can be a scheduled
  month-end job. Both commands are read-only and safe against a live gateway.
- Schema version 3. Known gaps — including that this reconciles tokens rather than
  currency, and why — are in [docs/metering.md](./docs/metering.md).

**Phase 1 — resilience.**

- Per-route **fallback chains**. A route may declare ordered fallback aliases;
  a failure the upstream is responsible for (408, 429, 5xx) falls through to the
  next one. A failure the caller has to fix (400, 401, 402, 403) does not —
  retrying it elsewhere bills a second account to produce the same rejection.
  Chains are resolved non-transitively, so what one route declares is every
  upstream a request for it can reach.
- **Circuit breakers**, keyed per provider *and* upstream model rather than per
  provider: Azure quota is per deployment, so a saturated `gpt-4o` deployment
  must not take out the `gpt-4o-mini` deployment beside it. Rolling window with
  a minimum-throughput gate, so partial degradation is detected and a quiet
  gateway does not trip on a single failure. One probe on recovery, not one per
  waiting caller.
- Streamed fallback happens strictly before the first chunk reaches the client.
  After that the 200 is committed, and failing over would mean replaying tokens
  the caller already saw or splicing two completions together — so mid-stream
  failures surface as themselves.
- The request log now records the route that **actually answered**. Attributing a
  successful fallback to the primary provider would bill an account that was
  never called.
- New metrics: `gatehouse.route.fallbacks` and
  `gatehouse.circuit_breaker.rejections`.
- New configuration section `Gatehouse:Resilience`. Both features are on by
  default and inert on a healthy deployment.
- Known gaps are documented rather than left to be discovered — see
  [docs/resilience.md](./docs/resilience.md).

**Phase 1 — virtual keys.**

- Virtual keys: `Authorization: Bearer gh-sk-...` credentials that let applications
  hold a Gatehouse key instead of a provider key. Revoking one stops a single
  application without rotating anything upstream.
- `gatehouse keys create|list|revoke`. The secret is shown once; only a SHA-256 hash
  is stored, so it cannot be recovered and a stolen database yields no credentials.
- Chargeback attribution: organisation, team and application labels are recorded on
  every request, denormalised so a report for a past period attributes spend to
  whoever owned it then rather than to whoever owns the key now.
- Authentication is **required by default**. A gateway configured to require it with
  no usable key refuses to start, rather than starting and rejecting every request —
  the failure mode that looks healthy to an orchestrator.
- Schema migration to version 2, adding the key table and the attribution columns
  without rewriting existing rows.

**Phase 1 — providers.**

- Azure OpenAI, via the existing OpenAI-compatible provider plus deployment-in-path
  addressing and Microsoft Entra managed-identity authentication. `Azure.AI.OpenAI` is
  deliberately not a dependency; see [ADR 0002](./docs/adr/0002-provider-integration.md).
- Anthropic Messages API provider, hand-written to preserve the cache-token fields that
  invoice reconciliation depends on.
- Google Gemini provider, hand-written because no official .NET SDK covers the Gemini
  Developer API.
- `MeteringConsistency`: an arithmetic backstop that downgrades token counts to *estimated*
  and logs a discrepancy when a provider's reported usage stops adding up, rather than
  letting it reach a chargeback export unnoticed.
- `TokenUsage.CacheCreationTokens`, separating cache writes (billed at a premium) from cache
  reads (billed at a discount).
- [docs/providers/wire-formats.md](./docs/providers/wire-formats.md), recording each
  provider's verified wire format and the metering traps in it.

### Fixed

- Three metering defects caught while reading provider documentation, before any of the code
  shipped: Anthropic's streamed usage is cumulative rather than incremental (summing it
  over-bills every streamed request); Anthropic's `input_tokens` excludes cached tokens,
  which are additive (mapping it straight through under-reports a cache-heavy prompt by most
  of its size); and Gemini bills thinking tokens as output while reporting them outside
  `candidatesTokenCount`.

### Phase 0 — Foundations

- Project governance: [GOVERNANCE.md](./GOVERNANCE.md) with the permanent
  no-paywall commitment, a public [ROADMAP.md](./ROADMAP.md),
  [CONTRIBUTING.md](./CONTRIBUTING.md), and a [SECURITY.md](./SECURITY.md) that
  states the threat model rather than only an inbox.
- Architecture spike: an OpenAI-compatible `/v1/chat/completions` endpoint with
  server-sent-event streaming, proxying to a configurable upstream.
- Self-contained NativeAOT publish for `linux-x64`, `linux-arm64`, `win-x64` and
  `osx-arm64`.
- One build, three hosts: Windows Service, systemd notify, and container.
- SQLite as the default zero-dependency store.
- OpenTelemetry tracing, metrics and logs following the GenAI semantic
  conventions.
- Public benchmark harness in `bench/Gatehouse.Benchmarks`.
- NuGet packages under the `Gatehouse.*` prefix — `Gatehouse.Core`,
  `Gatehouse.Providers.OpenAI` and `Gatehouse.Storage.Sqlite` — each targeting
  `net8.0`, `net9.0` and `net10.0`, with XML documentation and Source Link.
- Supply chain from the first commit: deterministic builds, CycloneDX SBOM,
  Sigstore signing and SLSA provenance on release, CodeQL and OpenSSF Scorecard
  in CI.

[Unreleased]: https://github.com/zcsizmadia/Gatehouse/compare/v0.1.0-beta.1...HEAD
[0.1.0-beta.1]: https://github.com/zcsizmadia/Gatehouse/releases/tag/v0.1.0-beta.1
