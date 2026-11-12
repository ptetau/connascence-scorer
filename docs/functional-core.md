# Connascence.Core is a functional core

`src/Connascence.Core` (the detectors, scoring and settings) is checked by
[FunctionalLint](https://github.com/ptetau/functional-csharp), a Roslyn analyzer that keeps C# to a functional
subset: no loops, no mutation, immutable collections, records for data, static classes for functions.

The CLI, the Roslyn analyzer project and the tests are shells. They aren't checked.

## Why

A functional core is easier to test and reason about: the same input always gives the same findings, and there is
no hidden state between detectors. Core was already pure in the way that matters most (no I/O, no clock, no
console), so it is a good fit.

## How it's wired

| Piece | What it does |
|---|---|
| `external/functional-csharp` | FunctionalLint, as a git submodule pinned to one commit. Clone with `--recurse-submodules`, or run `git submodule update --init`. |
| `Connascence.Core.csproj` | Imports `FunctionalCore.props`: the analyzer, nullable checks and the culture rules, all as errors. Core targets .NET 11. |
| `functionallint.policy` | The owner's rules. Lists Core as the only core, allows Roslyn as its only reference, and lowers FP0014, the one rule Core still breaks, to a warning. |
| `.functionallint-baseline.json` | How many of each rule every Core file still breaks. |
| `scripts/functional-gate.py` | The gate. Fails on any error, or when a file breaks a rule more often than its baseline. |
| `.githooks/pre-commit` | Runs the gate when a commit touches Core, the policy or the baseline. |
| CI | Runs the gate on every pull request, against the base branch's policy. |

## The migration

Core was written imperatively. Turning FunctionalLint on found about 420 problems. All are fixed except one rule:
- Every rule is an error again, except FP0014 (records holding lists). Its 9 cases are in
  `.functionallint-baseline.json`, and the gate fails if a file gains one.
- `python3 scripts/functional-gate.py --update` lowers the baseline after a fix. It refuses to raise it. When
  FP0014 reaches zero, remove it from `functionallint.policy` too.

The refactor doesn't change what the tool reports. The old and new code give byte-identical JSON reports on this
repo, `tests/fixtures/tally` and functional-csharp, in C# and generic mode, and `connascence init` writes the same
config. Run time is about the same.

## Things to know when changing Core

- **Keep to netstandard2.0 APIs.** The Roslyn analyzer compiles Core's source for netstandard2.0. For example, use
  `ImmutableHashSet<T>`, not `IReadOnlySet<T>` (which netstandard2.0 lacks). The analyzer project has polyfills
  for records, `init` and `required` (`src/Connascence.Analyzers/Polyfills`).
- **Findings are immutable.** Scoring, merging and the CLI make new findings with `with`. `Finding` and
  `CSharpContext` compare by identity (they define their own `Equals`): two findings with the same contents are still
  two findings, and they are kept in sets.
- **Keep output order stable.** An `ImmutableDictionary` iterates in hash order, which can change between runs. When
  findings come from iterating a grouping, keep it in first-seen order (`GroupBy` does), as `CSharpContext.Calls`
  and the generic engine's definitions do, and use the dictionary only for lookups.
- **Records holding lists (FP0014): your call.** `Settings` (6 lists and maps), `ScoreSummary` (2) and `TextFile`
  (its lines) are records whose collections compare by reference. Nothing compares them today, so it's harmless now.
  Options: reference a value-equal list such as functional-csharp's `ValueList<T>` (it targets .NET 11, so the
  netstandard2.0 analyzer would need its source compiled in, and JSON needs its converter); add a small
  `[ValueEquality]` list to Core; or give these records their own `Equals`.
- **Don't switch rules off.** No `#pragma`, `[SuppressMessage]`, `#nullable disable` or lowered severities: the gate
  reports them (FP0016). Agents shouldn't edit `functionallint.policy`; ask the owner.

Rule docs, with before/after examples: `external/functional-csharp/FunctionalLint/docs/rules/`. Some rules fix
themselves: `external/functional-csharp/FunctionalLint/heal.sh src/Connascence.Core/Connascence.Core.csproj`.
