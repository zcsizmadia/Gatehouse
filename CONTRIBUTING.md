# Contributing to Gatehouse

Thank you for considering it. This guide should get you from clone to merged
pull request without having to guess at anything.

## Before you start

- **Bugs and small fixes** — open a pull request directly.
- **New features** — open an issue first. Gatehouse says no to more than it says
  yes to (see [scope discipline](./GOVERNANCE.md#scope-discipline)); an issue
  costs you ten minutes, a rejected pull request costs you a weekend.
- **New providers** — the provider count is capped at seven and adding an eighth
  requires an RFC plus a maintainer who commits to owning it. Please read
  [GOVERNANCE.md](./GOVERNANCE.md) before writing code.

## Prerequisites

- **.NET SDK 10.0.100 or later.** Libraries multi-target `net8.0;net9.0;net10.0`,
  so the .NET 8 and 9 targeting packs must be installed too — the SDK installer
  handles this, or `dotnet workload` / Visual Studio will prompt.
- Git, and a POSIX shell or PowerShell.
- Docker, only if you are touching container packaging.

## Build and test

```bash
dotnet restore
dotnet build   -c Release
dotnet test    -c Release
```

`dotnet build` treats **all warnings as errors**. This is deliberate: a gateway
that warns during build is a gateway that surprises in production. If an analyzer
is wrong, suppress it in `.editorconfig` with a comment explaining why, rather
than in the source.

Run the gateway locally:

```bash
dotnet run --project src/Gatehouse.Server -- --config ./samples/gatehouse.yaml
```

## Testing

Gatehouse uses **[TUnit](https://tunit.dev/)**, not xUnit or NUnit. TUnit is
source-generated and AOT-compatible, which matters because the NativeAOT build is
a shipping target rather than an experiment.

A quick orientation if you have not used it:

```csharp
public class BudgetTests
{
    [Test]
    public async Task Rejects_request_over_the_remaining_allowance()
    {
        var budget = new Budget(limitUsd: 10.00m, spentUsd: 9.95m);

        var decision = budget.Evaluate(estimatedCostUsd: 0.10m);

        await Assert.That(decision.Allowed).IsFalse();
    }

    [Test]
    [Arguments(0.01, true)]
    [Arguments(5.00, false)]
    public async Task Honours_the_remaining_allowance(decimal cost, bool allowed)
    {
        // ...
    }
}
```

Assertions are `await Assert.That(x).IsEqualTo(y)` — they are awaited. Test
methods are `[Test]`, parameterised cases are `[Arguments(...)]`.

What we ask for:

- A regression test with every bug fix, failing before your change and passing
  after.
- Tests for the failure path, not only the happy path. A gateway is mostly
  failure paths.
- No `Thread.Sleep` and no wall-clock dependencies. Inject `TimeProvider`;
  `Microsoft.Extensions.TimeProvider.Testing` is already referenced.

## Style

`.editorconfig` is authoritative and enforced at build time. The short version:
file-scoped namespaces, `_camelCase` private fields, braces always, `var` only
when the type is obvious from the right-hand side.

Comments should explain **why**, not what. If a comment restates the code, delete
one of them.

## AOT and trimming

Shipping projects are compiled with the trim and AOT analyzers on, and `IL2026`,
`IL2091` and `IL3050` are **errors**. Practically:

- No `JsonSerializer` calls without a `JsonSerializerContext`. Source-generated
  serialization only.
- No reflection over types the linker cannot see.
- If you genuinely need a suppression, `[UnconditionalSuppressMessage]` with a
  justification that explains why it is safe — never `[RequiresUnreferencedCode]`
  pushed up the call stack until it escapes.

## Benchmarks

If your change is motivated by performance, it needs a benchmark in
`bench/Gatehouse.Benchmarks` that demonstrates the improvement. This is one of the
project's [three standing rules](./ROADMAP.md#three-standing-rules): every
performance claim ships with the harness that reproduces it.

```bash
dotnet run -c Release --project bench/Gatehouse.Benchmarks -- --filter '*Streaming*'
```

## Commits and pull requests

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/):

```
feat(providers): add Bedrock streaming support
fix(metering): reconcile cached-token counts against the invoice line
docs(governance): clarify the tie-breaking rule
```

Types in use: `feat`, `fix`, `docs`, `test`, `refactor`, `perf`, `build`, `ci`,
`chore`. The scope is the area touched — `providers`, `metering`, `budgets`,
`server`, `cli`.

For the pull request itself: describe what changes and why, link the issue, and
say what you did to verify it. Green CI plus one maintainer approval merges.

## Developer Certificate of Origin

Contributions are accepted under the [DCO](https://developercertificate.org/).
Sign off your commits:

```bash
git commit -s -m "fix(metering): reconcile cached-token counts"
```

This adds a `Signed-off-by:` line certifying that you wrote the patch or
otherwise have the right to submit it under Apache-2.0. There is no CLA — the DCO
is sufficient and does not ask you to assign anything.

## Security issues

Do not open a public issue. See [SECURITY.md](./SECURITY.md).

## Code of conduct

The [Contributor Covenant](./CODE_OF_CONDUCT.md) applies in all project spaces.
