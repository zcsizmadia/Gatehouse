# Amazon Bedrock

The seventh and last provider under the [provider cap](../../GOVERNANCE.md), and the
only one built on a vendor SDK.

## Configuration

```json
"Providers": {
  "bedrock": {
    "Kind": "amazon-bedrock",
    "Region": "us-east-1",
    "TimeoutSeconds": 600
  }
},
"Models": {
  "claude-bedrock": {
    "Provider": "bedrock",
    "UpstreamModel": "anthropic.claude-sonnet-4-20250514-v1:0"
  }
}
```

**Addressed by region, not URL.** The AWS SDK derives the endpoint, so `BaseUrl`
is not merely unnecessary — setting it is *rejected at startup*, because a
setting that looks effective and is ignored is worse than one that fails loudly.
`Region` is required and never inferred from the ambient `AWS_REGION`: model
availability and price both vary by region, and an environment variable that
silently changes which region gets billed is not something a gateway should read.

`UpstreamModel` is the full Bedrock model id, including the vendor prefix and
version suffix.

## Credentials

With **no** credential variables set, the AWS credential chain resolves an IAM
role from the instance, task or pod. That is the recommended path and the same
story as Entra managed identity for Azure: there is no key to rotate, leak, or
find in a backup, because Gatehouse stores nothing.

For static keys, set **both**:

```json
"AccessKeyIdEnvironmentVariable": "AWS_ACCESS_KEY_ID",
"SecretAccessKeyEnvironmentVariable": "AWS_SECRET_ACCESS_KEY"
```

Setting one without the other is a startup error. Left to itself it would
resolve to no credential at all and fall through to the IAM role — a confusing
way to discover that half the configuration was missed.

Credentials are resolved once at startup, not per request, so a rotation takes
effect at a restart rather than halfway through a request. Client construction
does no network I/O; a gateway with Bedrock configured and no AWS metadata
service reachable still starts in about two seconds.

## Why an SDK here and nowhere else

[ADR 0002](../adr/0002-provider-integration.md) uses an official SDK only where
it is both applicable and trim-annotated. Bedrock is the one place both hold:

- **The SDK passes the NativeAOT gate.** `AWSSDK.BedrockRuntime` and
  `AWSSDK.Core` both carry `IsTrimmable=True`, and — more to the point — an AOT
  publish of the whole gateway with Bedrock in it produces **zero** IL warnings,
  with `IL2026` and `IL3050` promoted to errors. That was verified by publishing,
  not assumed from the metadata.
- **It removes hand-rolled SigV4 signing.** Request signing is the one piece of
  provider plumbing where a subtle mistake produces intermittent authentication
  failures rather than an obvious bug.

Managed footprint is about 1.3 MB across the two assemblies.

## Converse, not InvokeModel

`InvokeModel` takes each model family's native payload. Supporting Anthropic,
Nova, Llama and Mistral through it would mean four request shapes, four response
shapes and four sets of usage fields inside what is nominally *one* provider —
a provider registry hiding inside a provider, and precisely the breadth that
governance caps at seven exists to prevent.

Converse is Bedrock's own normalisation of all of that. A new model family on
Bedrock needs no Gatehouse change at all.

The cost: Converse does not expose model-specific parameters. Callers who need
those have the passthrough route, which is recorded as unmetered and says so.

## Metering: the semantics are derived, not assumed

Providers disagree about whether cache tokens are **additive** to the input count
or a **subset** of it. Anthropic's native API reports them additively; OpenAI
reports them as a subset. Getting it wrong silently double-counts or
under-counts every cached request, and the error surfaces months later as a
reconciliation variance nobody can explain.

Bedrock reports `TotalTokens` alongside the parts, which makes the question
answerable rather than a matter of trusting documentation:

- total equals input + output + cache counts → **additive**
- total equals input + output → cache counts are already **inside** input
- no total reported → additive is assumed, which is what AWS documents

This matters more here than for other providers, because Bedrock is the one
provider whose behaviour **could not be verified against a live endpoint during
development**. Deriving the convention from the provider's own arithmetic means a
change in Bedrock's semantics is absorbed rather than silently mis-metered, and
`MeteringConsistency` is the backstop that logs a warning if the numbers still
fail to add up.

## Streaming

Bedrock's event stream is consumed through `IAsyncEnumerable`. The SDK also
offers a synchronous enumerator and an event-callback API, and either would pin a
thread per in-flight completion — on a gateway holding hundreds of concurrent
streams that is thread-pool starvation, presenting as the whole process going
slow rather than as anything to do with Bedrock.

Usage arrives on a metadata event Bedrock sends *after* the content, so it is
attached to a final chunk with an empty delta — the same shape OpenAI uses, so
clients need no special handling.

## Retries are Gatehouse's job

`MaxErrorRetry = 0` on the client, deliberately. Leaving the SDK's retries on
alongside Gatehouse's [fallback chains](../resilience.md) multiplies one client
request into up to nine upstream calls, all billed — and makes the circuit
breaker's failure counts meaningless, because it never sees the failures the SDK
swallowed.

## Known gaps

- **Not verified against live Bedrock.** Development had no AWS credentials, so
  every test runs against constructed SDK objects and hand-built event sequences.
  The translation logic, the derived cache semantics and the stream handling are
  covered by tests; what is *not* covered is Bedrock accepting the request
  Gatehouse builds. Treat the first real call as the integration test.
- **Tool messages are rejected**, not silently coerced into a user turn. Tool
  calling arrives with the MCP work in Phase 3.
- **No cross-region inference profiles.** A model id beginning `us.` or `eu.`
  will be passed through unchanged and should work, but it is untested.
- **No Bedrock guardrails integration.** The guardrails plugin contract is
  Phase 3; a `StopReason` of `guardrail_intervened` is mapped to
  `content_filter` so a caller can at least tell.
