<!--
Thanks for contributing to Gatehouse.

Green CI plus one maintainer approval merges. If this is a new feature, please
check that an issue exists first — see CONTRIBUTING.md.
-->

## What this changes

<!-- One or two sentences. What is different after this merges? -->

## Why

<!-- Link the issue, or explain the problem. "Why" is the part review cares about. -->

Closes #

## How it was verified

<!--
Not "tests pass" — what did you actually check? For a bug fix, say what failed
before. For a behaviour change, say how you observed the new behaviour.
-->

## Checklist

- [ ] Commits are signed off (`git commit -s`) — the project uses the DCO, not a CLA
- [ ] `dotnet build -c Release` is clean (warnings are errors here)
- [ ] Tests pass, including on `net8.0` and `net9.0` if this touches a library
- [ ] A regression test accompanies any bug fix, failing before the change
- [ ] No reflection-based `JsonSerializer` calls added — source-generated only
- [ ] `CHANGELOG.md` updated for anything user-visible

### If this touches performance

- [ ] A benchmark in `bench/Gatehouse.Benchmarks` demonstrates the claim
      (every performance claim ships with the harness that reproduces it)

### If this touches governance features

- [ ] The feature remains free and open — no governance capability is ever paywalled

### If this adds a provider

- [ ] An RFC issue was approved, and a maintainer has committed to owning it
      (the provider count is capped at seven — see GOVERNANCE.md)
