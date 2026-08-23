# Gatehouse

**The open AI control plane for the enterprise.**

Gatehouse sits between your applications and your LLM providers. It routes the
traffic, records who spent what, and produces the audit trail and chargeback data
your finance and compliance teams ask for.

One OpenAI-compatible endpoint in front of many providers — OpenAI-compatible,
Azure OpenAI with Entra managed identity, Anthropic, Gemini — with virtual keys,
usage metering that normalises the providers' disagreeing token semantics, and
OpenTelemetry GenAI telemetry. NativeAOT, SQLite by default, no Postgres and no
Redis to stand up.

**Status: pre-alpha, mid-Phase-1. Not suitable for production.** There are no
budgets, no spend limits, no per-key model restrictions, no RBAC, no SSO and no
rate limiting yet: a valid key can call any configured model without limit.

- Repository, roadmap and quick start: https://github.com/zcsizmadia/Gatehouse
- What is actually enforced today, as a capability table:
  https://github.com/zcsizmadia/Gatehouse/blob/main/SECURITY.md#what-is-actually-implemented

Licensed under Apache-2.0. No governance feature will ever move behind a
paywall — that commitment is written into the project's governance document.
