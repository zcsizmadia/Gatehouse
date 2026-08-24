# Metering and invoice reconciliation

The most-cited complaint about the incumbent AI gateways is that their usage
numbers do not match the provider bill, and nobody can say why. Producing a
total is easy. Producing a total alongside a defensible account of where it can
and cannot be trusted is the actual requirement, because the number gets handed
to a finance team who will ask.

So Gatehouse does not try to make the numbers agree. It quantifies the
disagreement, bounds how much of it Gatehouse's own known gaps could explain,
and reports the remainder as needing investigation.

**A reconciliation that always balances is a reconciliation that is not doing
anything.**

## What gets recorded

Per request, server-side, in the request log:

| Field | Why it is separate |
| ----- | ------------------ |
| `prompt_tokens` | Input total, including both cache subsets |
| `completion_tokens` | Output total |
| `prompt_tokens_cached` | Billed at roughly a *tenth* of the input rate |
| `prompt_tokens_cache_write` | Billed at a *premium* over the input rate |
| `usage_is_provider_reported` | Whether the counts came from the provider or are absent |
| `metered` | Whether Gatehouse could read the counts at all |

The cache split is why this is three columns and not one. Two months with
identical prompt-token totals and different cache hit rates produce materially
different bills; a reconciliation holding only the total can detect that the
numbers disagree and cannot say why.

Gatehouse does **not** estimate token counts locally. A request either has
provider-reported counts or it has none, and the second case is reported as a
genuine unknown rather than as zero. Reporting it as zero is a lie that adds up:
a month of them looks like free traffic.

## Reading what was recorded

```bash
gatehouse usage summary --month 2026-08
```

```
PROVIDER         MODEL                          REQUESTS         PROMPT       CACHED     COMPLETION  CONFIDENCE
openai           gpt-4o                               50        250,000            0         45,000      100 %
anthropic        claude-sonnet-5                     200        400,000            0        100,000      100 %
openai           gpt-4o-mini                         960      1,080,000      360,000        270,000       94 %

Total: 1,210 request(s), 1,730,000 prompt + 415,000 completion = 2,145,000 tokens.
60 request(s) had no readable token counts, so the totals above are a floor rather
than a measurement.
```

**Confidence** is the share of requests whose tokens the provider reported. It
is printed on every summary, not only when reconciling, because a total printed
without it is the number that ends up in a spreadsheet as though it were exact.

## Reconciling against a bill

Export usage from the provider's own dashboard as CSV, then:

```bash
gatehouse usage reconcile --month 2026-08 --statement ./openai-august.csv
```

The statement needs a header row of
`provider,model,prompt_tokens,completion_tokens`. Column order does not matter,
`upstream_model` is accepted for `model`, thousands separators and quoted fields
are handled, and `#` comments are allowed so you can record which invoice a file
came from next to the numbers. Repeated rows for one model are **summed**, so a
per-day export can be fed in unchanged.

A real run:

```
PROVIDER         MODEL                         STATEMENT       RECORDED       VARIANCE  VERDICT
openai           text-embedding-3-large          900,000              0       +900,000  NOT RECORDED BY GATEHOUSE
openai           gpt-4o                                0        295,000       -295,000  NOT ON STATEMENT
openai           gpt-4o-mini                   1,422,000      1,350,000        +72,000  within known gaps
anthropic        claude-sonnet-5                 500,000        500,000              0  balanced

openai/text-embedding-3-large — NOT RECORDED BY GATEHOUSE
  - The provider billed 900,000 tokens for 'text-embedding-3-large' and Gatehouse
    has no record of it at all. A credential for this provider is very likely in
    use outside the gateway.

openai/gpt-4o-mini — within known gaps
  - 40 passthrough request(s) were forwarded verbatim and could not be metered; at
    the mean size of metered traffic that is about 60,000 tokens. Passthrough
    requests are often larger than average, so treat this as a floor.
  - 20 request(s) returned no token counts at all; at the mean size of the rest
    that is about 30,000 tokens.
  - 20 request(s) failed. Any that failed after the provider had begun generating
    were still billed, and providers do not report usage on an error response, so
    their cost is invisible to Gatehouse.
```

`reconcile` exits **1** when a line needs investigating and **2** on bad input,
so it can run as a scheduled month-end job whose failure is visible without
anyone reading the output. Both commands are read-only and safe against a live
gateway.

## The verdicts, in order of severity

| Verdict | Meaning | What to do |
| ------- | ------- | ---------- |
| `balanced` | Within tolerance | Nothing |
| `within known gaps` | Outside tolerance, but no bigger than the requests Gatehouse could not read | Note it; reduce passthrough if the gap is growing |
| `NOT ON STATEMENT` | Gatehouse recorded usage the statement omits | Usually the export was scoped differently — check period and account |
| `UNEXPLAINED` | Bigger than anything Gatehouse can account for | Look for applications calling the provider directly, or a second gateway on the same account |
| `NOT RECORDED BY GATEHOUSE` | The provider billed for a model Gatehouse has never seen | **A credential is in use outside the gateway. Every governance control is inert for that traffic.** |

`NOT RECORDED BY GATEHOUSE` ranks above `UNEXPLAINED` deliberately: the second
means some traffic bypassed the gateway, the first means all of it did.

Tolerance defaults to **1,000 tokens or 0.5%, whichever is larger** — both,
because a purely relative tolerance calls a 40-token difference on a small month
a catastrophe, and a purely absolute one calls 5,000 tokens on a billion-token
month a problem worth waking someone for. Override with `--tolerance-tokens`.

## How the explainable bound is computed

Unreadable requests are priced at **the mean token count of the requests that
were readable**. That is the best available inference, and it is wrong whenever
unreadable requests differ systematically in size from readable ones —
passthrough traffic in particular is often the long-context requests that had no
OpenAI-compatible expression.

It is published as **a bound to compare against, never a correction to apply.**
Gatehouse will not adjust a total using this figure.

Gaps are only counted in the direction they can act. Unreadable requests can
only make the provider's figure *larger* than Gatehouse's, so they never excuse a
statement that comes in lower — that direction points at a double count, which is
the failure mode that overcharges an internal team, and the report says so.

## Known gaps

- **Tokens, not money.** Reconciling currency needs a price book per provider,
  per model, per date — a standing maintenance liability and a source of wrong
  answers every time a vendor changes a rate. That arrives with the FOCUS
  chargeback work in Phase 3. Tokens are what both sides can state without
  interpretation.
- **A failed attempt's token cost is invisible.** If a provider generates tokens
  and then fails, those tokens were billed, and providers do not report usage on
  an error response. The count of failed requests is reported so the gap is
  visible; its size cannot be measured.
- **Window boundaries never align.** Gatehouse timestamps a request when it
  starts; a provider bills it when it completes. A request spanning midnight on
  the last of the month lands in different periods on each side. This is a
  structural source of small variance and is what the tolerance absorbs.
- **Passthrough is unmetered by construction.** It forwards the body untouched,
  so there is nothing to read. It is recorded, counted, and named in every
  report — an unmetered request Gatehouse knows about beats a metered one it
  never saw — but it cannot be priced.
- **No per-key or per-team breakdown yet.** The attribution columns are recorded
  on every row from Phase 1, so the history is accumulating, but the aggregation
  here groups only by provider and model. Chargeback by org, team and application
  is Phase 3.

## Schema note

These columns arrived in schema version 3. Version 3 also backfills `metered = 0`
onto pre-existing passthrough rows — the only migration in the schema's history
that touches an existing row, permitted because it sets a new classification
column to the value that was always semantically true of those rows. No migration
may ever rewrite a recorded fact: a token count, a timestamp, a status code or an
attribution label. Those are the observations, and rewriting an observation is
falsifying the record.
