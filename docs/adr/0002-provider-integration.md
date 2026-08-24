# ADR 0002 — Provider integration: official SDKs versus hand-written HTTP

- **Status:** Accepted
- **Date:** 2026-08-23
- **Phase:** 1 (Core gateway MVP)
- **Supersedes:** the provider paragraph in [ADR 0001](./0001-gateway-architecture.md), which
  said only that the seven providers would be hand-written

## Context

Phase 1 adds Azure OpenAI, Anthropic, Amazon Bedrock and Google Gemini to the
OpenAI-compatible provider already shipped. Each could be integrated through a vendor SDK or
through hand-written HTTP, and the choice interacts with two constraints Phase 0 established:

- **The NativeAOT gate.** CI publishes a NativeAOT binary and runs it. A dependency that is
  not trim-annotated fails that gate, and `IL2026` is a build error here.
- **Metering that reconciles against provider invoices.** This is Phase 1's headline
  deliverable. Any abstraction that discards provider-specific token fields discards the
  evidence.

The initial assumption — that the AWS SDK would break AOT and that SDKs generally would — was
**checked rather than trusted**, and it was partly wrong.

## What the evidence actually said

Verified NuGet ownership, and the presence of the `IsTrimmable` assembly metadata that
trim-annotated packages carry:

| Provider | Official SDK | Owner verified | Trim-annotated |
| -------- | ------------ | -------------- | -------------- |
| OpenAI | `OpenAI` 2.13 | yes | **yes** |
| Azure OpenAI | `Azure.AI.OpenAI` 2.1 | yes | **no** (netstandard2.0 only) |
| Anthropic | `Anthropic` 12.42 | yes | **no** |
| Amazon Bedrock | `AWSSDK.BedrockRuntime` 4.0 | yes | **yes** |
| Google Gemini | `Google.Cloud.AIPlatform.V1` | yes | **no** — and it is Vertex AI, not this API |
| Ollama / vLLM | none | — | — |
| Foundry Local | `Microsoft.AI.Foundry.Local` | yes | no (management only) |
| — | `Azure.Identity` 1.21 | yes | **yes** |

Two results were the opposite of the assumption. **The AWS SDK is trim-annotated** and carries
explicit `RequiresUnreferencedCode` markers, so the analyzer can say precisely what is unsafe.
**Azure's own OpenAI SDK is not**, targeting only netstandard2.0.

## Decision

Use an official SDK exactly where it is both applicable and trim-annotated. Hand-write the
rest.

| Provider | Approach | Reason |
| -------- | -------- | ------ |
| OpenAI, Ollama, vLLM, Foundry Local | existing `openai-compatible` provider | already shipped and tested; a new dependency would add nothing |
| **Azure OpenAI** | same provider + `Azure.Identity` | its REST API *is* OpenAI-compatible, so only addressing and auth differ |
| **Anthropic** | hand-written HTTP | SDK not trim-annotated, and the cache-token fields must survive |
| **Google Gemini** | hand-written HTTP | no official SDK exists for this API at all |
| **Amazon Bedrock** | `AWSSDK.BedrockRuntime`, Converse API | trim-annotated — since verified by publishing — and it removes hand-rolled SigV4 signing |

### Azure OpenAI needs no Azure SDK for the data plane

The one genuinely surprising conclusion. Azure OpenAI speaks the OpenAI-compatible payload;
only two things differ, and both are addressing rather than content:

- the deployment name goes in the path, with an `api-version` query parameter;
- authentication is an `api-key` header or an Entra bearer token.

So `IUpstreamAddressing` isolates the first and a `DelegatingHandler` the second, and the
carefully-tested request builder, SSE reader and usage parser are shared. `Azure.AI.OpenAI` —
the one Azure package that would have failed the AOT gate — is not a dependency at all.
`Azure.Identity` is, because managed identity is worth having and is annotated.

The alternative, a second near-identical provider, is how two implementations drift until only
one of them has the streaming fix.

### Bedrock is deferred to its own pull request

Not because of the SDK choice but because of its blast radius: `AWSSDK.BedrockRuntime`'s
`RequiresUnreferencedCode` markers may surface as `IL2026` build errors on the paths Gatehouse
actually calls, and this repository promotes those to errors. That needs its own evaluation,
and it should not hold up three providers that are ready.

## Consequences

**Good.** Two hand-written wire formats instead of three. No hand-rolled SigV4 — the riskiest
component of the original plan is gone. Official SDKs used exactly where they are safe. Cache
and thinking token fields preserved, which is what makes the metering claim checkable.

**Costs.** Two wire formats to own, including their streaming state machines, plus the standing
provider-churn budget those imply. `Azure.Identity` brings `Azure.Core`, which is not
trim-annotated — it passes the analyzers on the paths used, but it is a dependency to watch.

**What this bought, concretely.** Reading the documentation rather than writing from memory
caught three metering defects before any code existed:

1. Anthropic's streamed usage is **cumulative**; summing it over-bills every streamed request.
2. Anthropic's `input_tokens` **excludes** cached tokens, which are additive — mapping it
   straight through under-reports a cache-heavy prompt by most of its size.
3. Gemini's thinking tokens are billed as output but reported outside `candidatesTokenCount`,
   and its `totalTokenCount` includes them — so forwarding that total makes every
   thinking-model request look internally inconsistent.

Each is silent: plausible numbers, materially wrong. `MeteringConsistency` exists because of
them, and normalises everything to subset semantics so a future provider cannot reintroduce
the same class of error unnoticed. The details are in
[docs/providers/wire-formats.md](../providers/wire-formats.md).

**Revisit if:** Anthropic or Google ship a trim-annotated SDK, or `Microsoft.Extensions.AI`
grows a usage model rich enough to carry cache and thinking token breakdowns.

## Follow-up: the Bedrock prediction was tested

Recorded here because an ADR that predicts and never checks is a guess with a
document around it.

The decision above rested on `AWSSDK.BedrockRuntime` being trim-annotated. When
Bedrock was actually implemented, that was verified rather than assumed:

- Both `AWSSDK.BedrockRuntime` and `AWSSDK.Core` do carry `IsTrimmable=True`.
- An AOT publish of the whole gateway with Bedrock included emits **zero** IL
  warnings, with `IL2026`, `IL2091` and `IL3050` promoted to errors.
- ILC analysed the full graph — a 168 MB object file — so the zero is coverage,
  not a skipped step. A probe whose own reflection code tripped `IL2070`
  confirmed the analyzer was live and simply had nothing to say about the SDK.

Two things the decision did not anticipate, both settled during implementation:

- **Converse rather than InvokeModel.** InvokeModel would have meant one request
  shape, response shape and usage parser *per model family*, inside a single
  provider. Converse is Bedrock's own normalisation, so a new model family needs
  no Gatehouse change. Without this the provider cap would have been defeated
  from the inside.
- **The SDK's retries had to be switched off.** `MaxErrorRetry = 0`, because two
  retry layers multiply one client request into up to nine billed upstream calls
  and hide failures from the circuit breaker.

The conclusion holds: an SDK is worth it exactly where it is applicable and
trim-annotated, and Bedrock remains the only provider where both are true.
