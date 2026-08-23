# Gatehouse governance

This document exists so that a prospective adopter can answer "what happens to
this project if its founder loses interest?" without having to ask anyone.

## Licensing commitments

These are the terms on which Gatehouse asks for your adoption. They are
deliberately hard to reverse.

### 1. Apache-2.0, permanently

Gatehouse is licensed under Apache-2.0. The project will not relicense to a
source-available, BUSL, SSPL, or "fair source" license. Any relicensing proposal
requires unanimous maintainer consent **and** would be, in the project's own
judgment, a breach of faith with adopters — the maintainers commit to rejecting
one.

### 2. No governance feature will ever move behind a paywall

Specifically and permanently open, with no "enterprise edition" gating:

- Hierarchical budgets and spend enforcement
- Single sign-on (OIDC) and SCIM provisioning
- Role-based access control and AD group mapping
- Audit logging
- Model allowlists and approval workflows
- PII detection and redaction
- Air-gapped deployment
- Chargeback and cost-allocation exports

The industry pattern is to ship a capable core and paywall exactly the features a
compliance officer requires. Gatehouse treats the resentment that pattern
generates as its primary competitive advantage. Squandering it would cost more
than any feature could earn.

### 3. Monetization, if any, is support and hosting

If the project ever needs revenue, it comes from commercial support contracts and
a hosted offering. It does not come from features, from usage limits in the
open-source build, or from telemetry.

### 4. No phone-home

The gateway does not report usage to the project. There is no opt-out because
there is nothing to opt out of. Adopters in regulated environments should not have
to audit us to find this out.

## Decision-making

### Roles

- **Contributor** — anyone who opens an issue or a pull request.
- **Maintainer** — commit rights and a binding vote. Listed in
  [MAINTAINERS.md](./MAINTAINERS.md).
- **Security response team** — a subset of maintainers who handle embargoed
  vulnerability reports.

### How decisions are made

Ordinary changes proceed by **lazy consensus**: a pull request with one
maintainer approval and green CI merges. Silence is assent.

The following require an issue tagged `rfc`, a minimum **7-day** comment period,
and explicit approval from a majority of maintainers:

- Adding a provider (see the provider cap below)
- Any change to the wire-compatible API surface after v1.0
- Adding a required runtime dependency (a new database, a message broker)
- Anything touching this document

Deadlock is resolved by a simple majority of maintainers. If a vote ties, the
change does not happen — the status quo wins ties.

### Becoming a maintainer

Nomination by an existing maintainer, approved by majority, after a sustained
contribution history — roughly: several non-trivial merged pull requests, useful
participation in review of *other* people's work, and a demonstrated grasp of the
project's scope discipline.

**Phase 4 requires at least three maintainers from at least two organizations
before v1.0 ships.** A single-vendor project with a foundation logo is still a
single-vendor project. If that bar is not met, v1.0 waits.

### Emeritus and inactivity

A maintainer inactive for 12 months moves to emeritus status, keeping credit and
losing commit rights. Returning is a formality, not a re-application. This exists
so the maintainer list stays an accurate description of who is actually
responsible.

## Scope discipline

Gatehouse says no to more than it says yes to. Two rules do most of the work:

**The provider cap.** Seven providers, hard. An eighth is added only when a
maintainer commits to owning it — its API churn, its bug reports, its tests.
Provider count is a vanity metric that converts directly into maintenance debt.

**The dependency bar.** A working Gatehouse deployment requires the binary and a
file. Adding a *required* dependency — Postgres, Redis, Kubernetes — needs an RFC.
Optional backends for shops that want them are fine; required ones are not.

## Sustainability

- **Provider churn budget.** Roughly 0.5 engineer-years per year is allocated to
  keeping existing provider integrations working, *before* feature work is
  planned. Integrations decay whether or not anyone budgets for them.
- **Foundation.** The project intends to donate to the .NET Foundation, giving
  adopters a neutral home and an asset-continuity story independent of any
  individual. Progress is tracked in [#1](https://github.com/zcsizmadia/Gatehouse/issues/1).
- **Succession.** If all maintainers become inactive, the .NET Foundation is
  asked to appoint stewards or to formally archive the project with a notice in
  the README. Silent abandonment is the failure mode this clause exists to
  prevent.

## The name and the marks

The Apache-2.0 licence covers the code. It does not cover the name "Gatehouse" or
the marks in [assets/](./assets/), which exist so that people can tell whether
they are running this project or something else.

Always allowed, without asking:

- Saying your software works with, integrates with, or is built on Gatehouse.
- Linking to the project and using the marks to do it.
- Unmodified redistribution of a release, marks intact.
- Talking about Gatehouse — reviews, comparisons, talks, books, courses.

Please ask first:

- Naming a product, service, or company after Gatehouse, or with "Gatehouse" in
  the name.
- Distributing a *modified* build under the Gatehouse name. Fork freely — that
  right is unconditional — but give the fork its own name, because users who hit a
  bug need to know whose bug it is.
- Using the marks in a way that suggests the project endorses or certifies you.

There is no fee and no licence to sign; the answer is usually yes. Open an issue.
On donation to the .NET Foundation, the marks transfer with the project, which is
the point of having them held somewhere other than one person's account.

## Code of conduct

The [Contributor Covenant](./CODE_OF_CONDUCT.md) applies to all project spaces.
Enforcement is by the maintainers; reports go to the address in that document.

## Amending this document

Changes follow the `rfc` process above. The licensing commitments in section 1
and 2 are intended to be permanent: a proposal to weaken them should be read as a
proposal to fork.
