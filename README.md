# Gatehouse

**The open AI control plane for the enterprise.**

[![CI](https://github.com/zcsizmadia/Gatehouse/actions/workflows/ci.yml/badge.svg)](https://github.com/zcsizmadia/Gatehouse/actions/workflows/ci.yml)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](./LICENSE)
[![OpenSSF Scorecard](https://api.securityscorecards.dev/projects/github.com/zcsizmadia/Gatehouse/badge)](https://securityscorecards.dev/viewer/?uri=github.com/zcsizmadia/Gatehouse)

Gatehouse sits between your applications and your LLM providers. It routes the
traffic, enforces who is allowed to spend what, and produces the audit trail and
chargeback data your finance and compliance teams ask for.

A gatehouse is the fortified building that controls a castle's entrance. It
houses the guards, decides who passes, and historically collected the tolls.
That is the product in one word:

| Castle          | Gatehouse                                                |
| --------------- | -------------------------------------------------------- |
| The **gate**    | Routing — one OpenAI-compatible endpoint, many providers |
| The **guards**  | Governance — budgets, RBAC, allowlists, audit, redaction |
| The **toll**    | Chargeback — metering that reconciles to provider invoices |

## Why another AI gateway

Because the governance features are the ones that get paywalled, and governance
is the reason enterprises deploy a gateway in the first place.

Gatehouse makes one commitment above all others, written into
[GOVERNANCE.md](./GOVERNANCE.md) and not subject to a later change of heart:

> **No governance feature will ever move behind a paywall.**

Budgets, SSO, RBAC, SCIM, audit logs, model allowlists, PII redaction and
air-gapped deployment are open-source features, permanently. Revenue, if it ever
comes, comes from support and hosting — never from features.

Three more things follow from being a .NET project rather than a Python one:

- **One executable.** NativeAOT, self-contained. No .NET runtime to install, no
  interpreter, no virtualenv, no sidecar — just the executable and the SQLite
  native library beside it. Runs as a Windows Service, a systemd unit, or a
  distroless container from the same build.
- **SQLite by default.** A working deployment needs no Postgres and no Redis. Scale
  out when you need to, not to get started.
- **Entra-native.** Managed identity to Azure OpenAI, OIDC SSO, AD-group RBAC —
  first-class, not adapters.

## Status

**Phase 1 — Core gateway MVP, in progress. Pre-alpha; not suitable for
production.**

Working today: an OpenAI-compatible streaming endpoint; four provider families
(OpenAI-compatible, Azure OpenAI with Entra managed identity, Anthropic, Gemini);
virtual keys with revocation, expiry and chargeback attribution; usage metering
that normalises the providers' disagreeing token semantics; OpenTelemetry.

Not working yet, and load-bearing if you are evaluating: **no budgets, no spend
limits, no per-key model restrictions, no RBAC, no SSO, no rate limiting.** A
valid key can call any configured model without limit.

The description above is what Gatehouse is *for*. For what it currently
*enforces*, read the [capability table in SECURITY.md](./SECURITY.md#what-is-actually-implemented) —
it is the authoritative answer and it distinguishes shipped controls from planned
ones. The [roadmap](./ROADMAP.md) says when the rest lands.

## Quick start

Issue a key first. Gatehouse requires authentication by default and will refuse to
start without one, rather than start and reject every request:

```bash
# 1. Issue a virtual key. The secret is shown once and only its hash is stored.
dotnet run --project src/Gatehouse.Server -- keys create \
  --name my-app --org acme --team platform \
  --config ./samples/gatehouse.json

# 2. Run the gateway
dotnet run --project src/Gatehouse.Server -- --config ./samples/gatehouse.json
```

Then point any OpenAI client at it:

```bash
curl http://localhost:8080/v1/chat/completions \
  -H "Authorization: Bearer gh-sk-..." \
  -H "Content-Type: application/json" \
  -d '{
        "model": "gpt-4o-mini",
        "stream": true,
        "messages": [{"role": "user", "content": "Say hello in five words."}]
      }'
```

The same request works against every configured provider — swap the `model` for
`claude-sonnet`, `gemini-flash` or `local` and Gatehouse translates it, meters it,
and records who to bill.

For local experiments without a key, set `Gatehouse:Authentication:Mode` to
`Disabled`. Gatehouse warns about it on every startup, because the gateway holds
your provider credentials.

### Managing keys

```bash
gatehouse keys create --name checkout-service --org acme --team payments --app checkout
gatehouse keys list
gatehouse keys revoke vk_1234567890abcdef
```

Revocation takes effect immediately and stops one application without rotating
anything at the provider. The record is kept, because the request log references
it and an audit trail that points at deleted rows is not an audit trail.

## Deployment

| Target          | Guide                                                        |
| --------------- | ------------------------------------------------------------ |
| Docker          | [docs/deployment/docker.md](./docs/deployment/docker.md)     |
| systemd (Linux) | [docs/deployment/systemd.md](./docs/deployment/systemd.md)   |
| Windows Service | [docs/deployment/windows-service.md](./docs/deployment/windows-service.md) |

## Performance

Every performance claim Gatehouse makes ships with the harness that reproduces
it. There are no benchmark numbers in this README that you cannot regenerate on
your own hardware:

```bash
dotnet run -c Release --project bench/Gatehouse.Benchmarks -- --filter '*'
```

See [docs/benchmarks.md](./docs/benchmarks.md) for methodology and the standing
rule that governs how results may be quoted.

## What Gatehouse is *not* (yet)

Being honest about this is cheaper than being discovered:

- Not production-ready. Phase 0 is an architecture spike.
- Virtual keys authenticate and attribute requests, but they do not yet *limit*
  anything. Budgets, spend enforcement, SSO and RBAC arrive in Phase 2.
- Semantic caching is deliberately **not** in the near-term plan. Exact-match
  caching only, until we can ship semantic caching with honest safety metrics.
- Provider coverage is deliberately capped at seven. Breadth is how gateways rot.

## Supply chain

Signed releases, an SBOM per artifact, and SLSA build provenance have been in
place since the first commit — not added before v1.0. See
[SECURITY.md](./SECURITY.md) for verification instructions.

## Contributing

Read [CONTRIBUTING.md](./CONTRIBUTING.md). Project decision-making, maintainer
criteria and the licensing commitments are in [GOVERNANCE.md](./GOVERNANCE.md).

## License

[Apache License 2.0](./LICENSE). Apache-2.0 rather than MIT for the explicit
patent grant, which enterprise legal review asks about.
