# Changelog

All notable changes to Gatehouse are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Pre-1.0 the public API may change in any minor release. The API-stability
guarantee takes effect at v1.0 — see the [roadmap](./ROADMAP.md).

## [Unreleased]

### Added

Phase 0 — Foundations.

- Project governance: [GOVERNANCE.md](./GOVERNANCE.md) with the permanent
  no-paywall commitment, a public [ROADMAP.md](./ROADMAP.md),
  [CONTRIBUTING.md](./CONTRIBUTING.md), and a [SECURITY.md](./SECURITY.md) that
  states the threat model rather than only an inbox.
- Architecture spike: an OpenAI-compatible `/v1/chat/completions` endpoint with
  server-sent-event streaming, proxying to a configurable upstream.
- NativeAOT single-file publish for `linux-x64`, `linux-arm64`, `win-x64` and
  `osx-arm64`.
- One binary, three hosts: Windows Service, systemd notify, and container.
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
