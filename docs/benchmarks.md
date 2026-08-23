# Benchmarks

One of the project's [three standing rules](../ROADMAP.md#three-standing-rules) is
that **every performance claim ships with the harness that reproduces it**. This
page describes the harness and the rules that govern how its output may be quoted.

There are deliberately no numbers on this page. Numbers belong to a machine, a
runtime version and a moment; a number quoted without them is marketing.

## Running them

```bash
dotnet run -c Release --project bench/Gatehouse.Benchmarks -- --filter '*'
```

Narrow to something specific:

```bash
dotnet run -c Release --project bench/Gatehouse.Benchmarks -- --filter '*Streaming*'
dotnet run -c Release --project bench/Gatehouse.Benchmarks -- --filter '*Routing*'
```

`-c Release` is not optional. BenchmarkDotNet will warn loudly about a Debug build
and the results will be meaningless.

## What is measured

### `StreamingBenchmarks`

The per-chunk cost of the streaming path — the number that matters most, because it
runs once per token rather than once per request. A gateway that adds a microsecond
per chunk adds a millisecond to a thousand-token completion, on every concurrent
stream simultaneously.

Parsing, serialising and the full relay are measured separately so that a
regression can be attributed rather than merely observed. `RelayEndToEnd` divided
by `ChunkCount` is the figure to quote as "Gatehouse overhead per chunk".

### `RoutingBenchmarks`

Alias resolution, which happens exactly once per request. Parameterised over 3, 50
and 500 configured routes to confirm the cost does not grow with configuration
size. Misses are measured separately from hits, because a misconfigured client can
send misses at full request rate.

Memory is diagnosed on both suites. The router freezes its lookup table at
construction specifically so that resolution allocates nothing; if that stops being
true, this is where it becomes visible.

## What is *not* measured, and why

**End-to-end latency against a real provider.** It is dominated by the provider and
the network. Publishing it would attribute someone else's inference time to
Gatehouse, in both directions.

**Requests per second.** Without a specified concurrency level, payload size and
provider behaviour, a throughput number communicates nothing. When Gatehouse
eventually makes a throughput claim it will come with a load harness that pins all
three, not a figure from a single laptop run.

**Comparisons against other gateways.** A benchmark of somebody else's project,
configured by us, is not evidence. If a comparison is ever published it will be
reproducible against both projects with configuration files included.

## Rules for quoting results

These apply to the README, release notes, issues, and anything anyone says on the
project's behalf:

1. **State the environment.** CPU, OS, .NET version, and whether the build was
   JIT or NativeAOT. BenchmarkDotNet prints all of it; include it.
2. **Name the benchmark.** "Gatehouse adds ~N µs per chunk (`RelayEndToEnd`,
   `ChunkCount=1000`)" is checkable. "Gatehouse is fast" is not.
3. **Quote the distribution, not just the mean.** For anything on the request
   path, the p99 is the number an operator actually experiences.
4. **Never quote a number this harness cannot regenerate.** If a claim needs a
   measurement the harness does not make, the harness gets extended first.

## Adding a benchmark

If a change is motivated by performance, it needs a benchmark that demonstrates the
improvement — see [CONTRIBUTING.md](../CONTRIBUTING.md#benchmarks). Keep new
benchmarks in the existing suites where they fit, use `[MemoryDiagnoser]`, and
prefer `[Params]` over separate methods so the scaling behaviour is visible rather
than inferred.
