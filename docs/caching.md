# Exact-match response caching

Repeated identical requests can be answered from memory instead of from a
provider. That is the whole feature, and the restraint is the point:
**Gatehouse never serves an answer to a question that merely resembles a stored
one.**

## Off by default, and why

Caching changes observable behaviour. Repeated identical requests stop reaching
the provider, so they stop being sampled and start returning a fixed answer, and
their latency falls to almost nothing. Every one of those is usually wanted — but
a gateway that starts doing it because somebody upgraded is a gateway that
changed the output of a caller's application without being asked.

```json
"Cache": {
  "Enabled": true,
  "TtlSeconds": 3600,
  "MaxEntries": 10000,
  "MaxResponseBytes": 262144,
  "ScopeToOrganisation": true
}
```

## What the key covers

Anything that can change the upstream response and is *not* in the key is a
correctness bug: two different requests hashing the same serves one caller
another caller's answer, silently.

**In the key:** the resolved provider, the upstream model, every message (role,
content, name, in order), `temperature`, `top_p`, `max_tokens`, `stop`, and — when
scoping is on — the organisation.

**Deliberately excluded:**

- `stream`. The content of an answer does not depend on how it is delivered. A
  streamed and a buffered request for the same conversation share one entry,
  which is correct and roughly doubles the hit rate on a mixed workload.
- `user`. An opaque end-user identifier providers use for abuse monitoring rather
  than generation. Including it would give every end user a private cache and
  drive the hit rate to nothing for exactly the deployments that set it.
- The caller's model *alias*. The upstream model determines the answer; the alias
  is a routing label, so two aliases pointing at one deployment share an entry.

The safety argument rests on one fact about the rest of the codebase: the
provider layer builds its upstream body field by field from the typed request
surface, so fields a client sends that Gatehouse does not model are **dropped,
not forwarded**, and cannot affect the response. If that ever changes — a
`JsonExtensionData` on the request type, or a provider forwarding a raw body —
this key becomes unsafe and has to change in the same commit.

The key is canonical JSON, hashed with SHA-256. JSON rather than concatenated
strings because concatenation invites the classic collision, where `("ab","c")`
and `("a","bc")` produce one key; JSON's own escaping removes the whole class of
problem without a hand-rolled delimiter scheme.

## Cross-organisation sharing is off by default

`ScopeToOrganisation` costs hit rate and is still the right default here. A
shared cache means one tenant's spend subsidises another's, that the second
tenant's audit trail shows a completion it never paid for, and that response time
reveals whether somebody else has asked a given question before. None is
catastrophic; all are surprises, and a governance tool should not hand an
operator a surprise it could have avoided.

Turn it off for a single-tenant deployment, where there is no boundary to cross.

Requests with no attribution — authentication disabled, or a key with no
organisation — are scoped to `(unattributed)` rather than falling into the shared
pool.

## Cache hits and the bill

**A cache hit is never counted as provider usage.** Its token counts are real —
they describe the response — but no provider billed for them. Counting them as
consumption would inflate recorded usage by exactly the amount the cache saved,
so a reconciliation would report Gatehouse recording *more* than the invoice
charged: the over-count direction, which is the one that overcharges an internal
team.

So the request log records `served_from_cache`, and the usage aggregation:

- excludes those rows from every token total,
- counts them as `CacheHits`,
- prices them as `TokensAvoided` — the saving,
- and measures `Confidence` against **billable** requests, so a more effective
  cache does not drag the confidence figure down.

See [metering](./metering.md). A cache hit is announced to the caller with
`X-Gatehouse-Cache: hit`, because a cache nobody can see is a cache nobody can
debug.

## What is never cached

- **Failures.** A 503 replayed for an hour turns a momentary provider blip into
  an outage that outlives it.
- **Truncated streams.** A stream that ends without a finish reason is not
  stored. Caching half a completion and replaying it for the whole TTL is the
  worst failure this cache can have, because it looks like a model that stops
  mid-sentence.
- **Multi-choice responses (`n > 1`).** They cannot be reassembled from a stream
  by appending deltas into one string. Rather than store something subtly wrong,
  nothing is stored.
- **Responses over `MaxResponseBytes`.** Skipped outright rather than stored and
  immediately evicted, so one very long completion cannot flush a cache full of
  useful short ones.

## Bounds and eviction

Bounded twice: by entry count, and by the size of any single response. Worst-case
memory is about `MaxEntries × MaxResponseBytes`, which an operator can reason
about before it becomes an incident. An unbounded cache in front of a gateway
does not save money — it converts a cost problem into an out-of-memory crash.

Eviction is least-recently-used, and reads promote. Expiry is lazy: entries are
checked on read and dropped when stale, and reading does **not** extend an
entry's life — a cache that refreshed the TTL on read would serve a popular
answer indefinitely, which is precisely the entry most likely to have gone stale.

## Performance

Every performance claim ships with the harness that reproduces it:

```bash
dotnet run -c Release --project bench/Gatehouse.Benchmarks -- --filter '*CachingBenchmarks*'
```

On the author's machine (.NET 10, x64, `--job short`):

| Operation | Mean | Allocated |
| --------- | ---- | --------- |
| Cache key, short chat prompt | 1.0 µs | 696 B |
| Cache key, 16 KB RAG prompt | 9.2 µs | 696 B |
| Hit lookup (key already computed) | 50 ns | 32 B |
| Miss lookup | 20 ns | 0 B |
| Store, including the eviction check | 120 ns | 288 B |

Read those as ratios on your own hardware, not as absolutes.

The number worth understanding is that **allocation is constant in prompt
length** — 696 B whether the prompt is 30 bytes or 16 KB. That is not free by
accident. The obvious implementation writes canonical JSON into a buffer and
hashes the result, and this harness is what argued against it: a 16 KB prompt
allocated ~50 KB per request, on the path of every request including the misses.
Sizing the buffer better barely helped, because the buffer *was* the allocation.
Hashing incrementally through one pooled block cut it to 696 B and made it 42%
faster.

Enabling the cache puts a hash over the whole conversation on every request,
including misses. That cost is paid by everybody; the saving is collected only by
the hits. For a workload with a low hit rate and long prompts, caching can be a
net loss — the harness above is how you find out which you have.

## Known gaps

- **In-process, so per instance.** Two gateways behind a load balancer keep
  independent caches, and the hit rate falls roughly with instance count. A
  shared cache means a required Redis, and project governance puts a required
  external dependency behind an RFC — a working Gatehouse needs the binary and a
  file. The honest trade is a smaller hit rate rather than a bigger deployment.
- **No cache stampede protection.** Several concurrent misses on one key all go
  upstream, and all of them store. Both answers are valid and the newer one wins;
  what is lost is the duplicated spend on the first request for a hot key.
- **No invalidation.** There is no way to evict an entry short of restarting or
  waiting out the TTL. Set `TtlSeconds` to what you can tolerate being stale.
- **No semantic caching.** Deliberately, and not in the near-term plan. It is the
  feature most likely to return a confidently wrong answer to a caller who has no
  way to tell, and shipping it without honest measurements of how often that
  happens would be selling a hazard as a saving. Phase 4, with those
  measurements, or not at all.
