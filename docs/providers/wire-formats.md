# Provider wire formats

Notes taken from each provider's official documentation while implementing its
provider, kept because the details that matter for **metering** are the ones most
easily got wrong from memory — and getting them wrong produces a chargeback report
that is confidently incorrect rather than obviously broken.

Every claim here was read from the vendor's own documentation. Where the
documentation is silent or ambiguous, this page says so rather than guessing.

---

## The worst trap: cached tokens are not counted the same way twice

Providers disagree about whether cached prompt tokens are **part of** the prompt count or
**separate from** it, and the disagreement is invisible unless you look for it.

| Provider | Relationship | Consequence of getting it wrong |
| -------- | ------------ | ------------------------------- |
| OpenAI | `cached_tokens` is a **subset** of `prompt_tokens` | none, if mapped directly |
| Anthropic | `input_tokens` **excludes** both cache fields | prompt under-reported by the whole cached portion |

Anthropic documents the arithmetic explicitly:

```text
total_input_tokens = cache_read_input_tokens + cache_creation_input_tokens + input_tokens
```

`input_tokens` counts only the tokens *after the last cache breakpoint*. So the obvious
mapping — `input_tokens` onto `PromptTokens` — under-reports the billable prompt by
everything that was cached. On the workloads where caching is worth using, that is most of
the prompt, and the error is **silent**: the numbers look plausible, they are just far too
small.

Gatehouse normalises to **subset semantics** everywhere: `PromptTokens` is the total billable
input, with `CachedPromptTokens` and `CacheCreationTokens` as breakdowns within it.
`TokenUsage.FromProviderWithAdditiveCache` exists so this cannot be done by accident, and
`MeteringConsistency` asserts that the two cache figures never exceed the prompt they belong
to.

Reads and writes are also tracked separately, because they are priced in opposite directions:

| Category | Multiplier vs base input |
| -------- | ------------------------ |
| Cache read | **0.1x** |
| Cache write, 5-minute TTL | **1.25x** |
| Cache write, 1-hour TTL | **2x** |

Collapsing them into one "cached tokens" number prices a cache-warming request as though it
were a cache hit — understating it by more than a factor of ten.

---

## The recurring trap: streamed usage semantics

Three of the four hand-written providers report token usage during a stream, and
they do not agree on what the numbers mean. Getting this wrong is silent:

| Provider | Streamed usage semantics | Correct handling |
| -------- | ------------------------ | ---------------- |
| OpenAI-compatible | Final chunk only, absolute | Take the last non-null `usage` |
| Anthropic | **Cumulative** on every `message_delta` | Take the **last** value — never sum |
| Gemini | Not documented (see below) | Take the last, and verify the arithmetic |

> Summing Anthropic's `output_tokens` across `message_delta` events — the obvious
> implementation — over-counts every streamed completion. The documentation warns
> about this explicitly, and it is the single most consequential line of provider
> documentation this project depends on.

`MeteringConsistency` guards all of them: it checks that reported prompt and
completion counts agree with the reported total, and logs a discrepancy rather than
letting an unnoticed semantics change reach a chargeback export.

---

## Anthropic — Messages API

**Documentation:** <https://platform.claude.com/docs/en/api/messages>

**Endpoint:** `POST {baseUrl}/v1/messages`

**Headers**

| Header | Value |
| ------ | ----- |
| `x-api-key` | the API key — *not* `Authorization: Bearer` |
| `anthropic-version` | `2023-06-01` |
| `content-type` | `application/json` |

**Request**

```json
{
  "model": "claude-sonnet-5",
  "max_tokens": 1024,
  "system": "You are terse.",
  "messages": [{ "role": "user", "content": "Hello" }],
  "stream": true,
  "temperature": 0.7,
  "top_p": 0.95,
  "stop_sequences": ["END"]
}
```

Three translation rules follow from this shape:

1. **`system` is a top-level field, not a role.** OpenAI puts the system prompt in
   the `messages` array; Anthropic does not accept `"role": "system"` there. System
   messages must be lifted out and concatenated into the top-level field.
2. **`max_tokens` is required.** OpenAI treats it as optional. A request that omits
   it is rejected, so the provider must supply a default rather than pass null
   through.
3. Only `user` and `assistant` are valid roles in `messages`.

**Stop reasons:** `end_turn`, `stop_sequence`, `max_tokens`, `tool_use`. Mapped to
the OpenAI vocabulary as `stop`, `stop`, `length`, `tool_calls`.

**Usage object**

```json
{
  "input_tokens": 2679,
  "output_tokens": 510,
  "cache_creation_input_tokens": 0,
  "cache_read_input_tokens": 0
}
```

The two cache fields are billed at different rates from ordinary input tokens and
are the reason this provider is hand-written rather than adapted from `IChatClient`,
whose `UsageDetails` has nowhere to put them.

### Streaming

Event sequence: `message_start`, then per content block
`content_block_start` → `content_block_delta`* → `content_block_stop`, then one or
more `message_delta`, then `message_stop`. `ping` events may appear anywhere.

```
event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ello frien"}}
```

- `message_start` carries `input_tokens` and the cache fields, plus a partial
  `output_tokens`.
- `message_delta` carries **cumulative** `output_tokens`, and may repeat the input
  and cache fields.
- Content blocks carry an `index`; a response may contain several.
- The documentation states that new event types may be added and that clients
  **must** tolerate unknown ones. The reader therefore ignores what it does not
  recognise instead of failing the stream.

---

## Google Gemini — generateContent

**Documentation:** <https://ai.google.dev/api/generate-content>

**Endpoints**

```
POST {baseUrl}/v1beta/models/{model}:generateContent
POST {baseUrl}/v1beta/models/{model}:streamGenerateContent?alt=sse
```

`alt=sse` is required for streaming. Without it the response is a JSON array
delivered in fragments, which is not server-sent events and cannot be parsed
incrementally by the same reader.

**Authentication:** `x-goog-api-key` header. The API also accepts `?key=`, which
Gatehouse does not use — a credential in a query string ends up in access logs,
proxy logs and error reports.

**Request**

```json
{
  "contents": [{ "role": "user", "parts": [{ "text": "Hello" }] }],
  "systemInstruction": { "parts": [{ "text": "You are terse." }] },
  "generationConfig": {
    "temperature": 0.7,
    "topP": 0.95,
    "maxOutputTokens": 1024,
    "stopSequences": ["END"]
  }
}
```

Translation rules:

1. Roles are `user` and **`model`** — not `assistant`.
2. The system prompt is `systemInstruction`, a separate object, as with Anthropic.
3. Sampling parameters live under `generationConfig` in camelCase.

**Response**

```json
{
  "candidates": [{ "content": { "parts": [{ "text": "..." }], "role": "model" }, "finishReason": "STOP" }],
  "usageMetadata": {
    "promptTokenCount": 12,
    "cachedContentTokenCount": 0,
    "candidatesTokenCount": 31,
    "totalTokenCount": 43
  }
}
```

`finishReason` is upper-case (`STOP`, `MAX_TOKENS`, `SAFETY`, `RECITATION`) and
maps to `stop`, `length`, `content_filter`, `content_filter`.

> **Known gap.** The documentation does not state whether `usageMetadata` in
> streamed chunks is cumulative or per-chunk. `totalTokenCount` being a total
> rather than a delta implies cumulative, and that is what Gatehouse assumes: it
> takes the last reported value and never sums. The assumption is asserted by
> `MeteringConsistency`, so if it is wrong the result is a logged discrepancy
> rather than a wrong invoice. Revisit if Google documents it.

---

## Amazon Bedrock

Served through the official, trim-annotated `AWSSDK.BedrockRuntime` rather than
hand-rolled HTTP, so there is no wire format to record here. That choice is
deliberate and explained in [ADR 0002](../adr/0002-provider-integration.md): it
avoids hand-rolling SigV4 request signing, which is the highest-risk component the
alternative would have required.

---

## Azure OpenAI

No separate wire format: the REST API is OpenAI-compatible, so the existing
`openai-compatible` provider serves it. Only two things differ, and both are
addressing rather than payload:

- The deployment name goes in the **path**, not the body:
  `{endpoint}/openai/deployments/{deployment}/chat/completions?api-version=...`
- Authentication is either an `api-key` header or an Entra bearer token for the
  `https://cognitiveservices.azure.com/.default` scope.

This is why `Azure.AI.OpenAI` is not a dependency. It is the one Azure package that
carries no trimming annotations, and Gatehouse needs none of it — only
`Azure.Identity`, which is annotated, for managed-identity token acquisition.
