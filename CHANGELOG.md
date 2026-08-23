# Changelog

All notable changes to Gatehouse are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Pre-1.0 the public API may change in any minor release. The API-stability
guarantee takes effect at v1.0 — see the [roadmap](./ROADMAP.md).

## [Unreleased]

### Added

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

[Unreleased]: https://github.com/zcsizmadia/Gatehouse/commits/main
