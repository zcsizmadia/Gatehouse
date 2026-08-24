# Gatehouse roadmap

This roadmap is public because continuity is the first thing an enterprise buyer
evaluates, and because the alternative — a roadmap that lives in a maintainer's
head — is what makes projects look abandonable.

Dates are relative to the start of Phase 1. Phases overlap at the edges; the
**gate** is what must be demonstrably true before the next phase is considered
started.

---

## Phase 0 — Foundations (~6 weeks) — *in progress*

Sustainability story before code. Buyers evaluate continuity first, and the
post-TensorZero market has learned to ask.

- [x] Public roadmap, governance document, contribution guide
- [x] Apache-2.0 licensing with the no-paywall commitment written down
- [x] Supply-chain scaffolding from commit one: signed releases, SBOM, SLSA provenance
- [x] Architecture spike: YARP + `Microsoft.Extensions.AI`, NativeAOT single binary, SQLite default
- [x] Public benchmark harness
- [ ] .NET Foundation letter of intent submitted

**Gate:** a streamed completion proxied through one binary, running as a Windows
Service, a systemd unit, and a Docker container — same build, three hosts.

---

## Phase 1 — Core gateway MVP (months 1–4)

The smallest thing a .NET shop can actually deploy.

- [x] OpenAI-compatible endpoint with SSE streaming
- **Exactly seven providers.** Azure OpenAI (Entra managed identity), OpenAI,
  Anthropic, Amazon Bedrock, Google Gemini, Ollama/vLLM, Foundry Local
  - [x] OpenAI, Ollama/vLLM, Foundry Local — one `openai-compatible` provider
  - [x] Azure OpenAI, with Entra managed identity
  - [x] Anthropic
  - [x] Google Gemini
  - [ ] Amazon Bedrock — deferred to its own pull request; see ADR 0002
- Virtual keys, fallback chains, circuit breakers
  - [x] Virtual keys, with revocation, expiry and chargeback attribution
  - [x] Fallback chains — per-route, non-transitive; see [resilience](./docs/resilience.md)
  - [x] Circuit breakers — rolling-window, keyed per provider *and* upstream model
- [x] Token metering that **reconciles against provider invoices** — LiteLLM's
  most-cited bug, and the one finance teams notice. See [metering](./docs/metering.md)
- [ ] Exact-match caching only
- [x] OpenTelemetry GenAI semantic-convention telemetry

**Gate:** a .NET shop can replace a basic LiteLLM deployment with one binary.

### Why seven providers and not seventy

Provider breadth is the metric gateways compete on and the reason they rot. Each
provider is a standing maintenance liability against an API that changes without
notice. Seven covers the overwhelming majority of enterprise .NET deployments.
Adding the eighth requires a maintainer who commits to owning it.

---

## Phase 2 — The governance core (months 4–8) — *the differentiator*

Everything the incumbents paywall, shipped open.

- Hierarchical budgets (org → team → app → key) with **provable** enforcement
- OIDC SSO — Microsoft Entra ID and Okta
- RBAC mapped to AD groups; SCIM user provisioning
- Immutable, tamper-evident audit logs
- Model allowlists and an approval workflow
- PII redaction via Presidio
- Blazor admin UI with **config-as-code parity** — anything the UI can do, a
  file in source control can do
- Tested air-gapped deployment mode

**Gate:** a regulated-industry evaluator can check every enterprise box without
talking to a salesperson. This is the launch moment.

---

## Phase 3 — Agent era & ecosystem (months 8–12)

- MCP gateway with tool-level permissions and OAuth 2.1 — Gartner now defines
  the AI-gateway category to include MCP traffic
- Chargeback exports in FinOps **FOCUS** format
- Kubernetes-*optional* HA, plus a Helm chart for shops that want it
- Guardrails plugin contract (this is where library multi-targeting earns its keep)
- Grafana dashboard pack
- .NET Aspire integration

**Gate:** one production reference deployment in a regulated vertical.

---

## Phase 4 — v1.0 & durability (months 12–18)

- **API-stability guarantee** — the move that legitimized Envoy AI Gateway
- Third-party security audit, published in full
- Foundation governance completed: 3+ maintainers from 2+ organizations
- Semantic caching, optional, shipped with honest safety metrics
- Monetization limited to support and hosting — never features

---

## Three standing rules

These outrank any individual roadmap item. They are repeated in
[GOVERNANCE.md](./GOVERNANCE.md) because they are governance, not planning.

1. **No governance feature ever moves behind a paywall.** The resentment created
   by open-core bait-and-switch is the moat; squandering it costs more than any
   feature earns.
2. **Provider API churn gets a dedicated budget** — approximately 0.5
   engineer-years per year, allocated before feature work, not after.
3. **Every performance claim ships with the harness that reproduces it.** A
   number without a runnable benchmark does not go in the README.

---

## What is deliberately not planned

- A model-training or fine-tuning story. Not a gateway concern.
- A proprietary wire protocol. OpenAI-compatible in, provider-native out.
- Semantic caching before Phase 4. Cache hits that return a *similar* answer to a
  *different* question are a correctness bug wearing a performance costume, and
  shipping one without measured false-hit rates would be dishonest.
- Becoming a vector database, a prompt-management SaaS, or an eval platform.
