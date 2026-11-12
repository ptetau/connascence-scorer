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
| `functionallint.policy` | The owner's rules. Lists Core as the only core, allows Roslyn as its only reference, and lowers no rule. |
| `.functionallint-baseline.json` | Known violations per file. Empty: Core breaks no rule. |
| `scripts/functional-gate.py` | The gate. Fails on any error, or when a file breaks a rule more often than its baseline. |
| `.githooks/pre-commit` | Runs the gate when a commit touches Core, the policy or the baseline. |
| CI | Runs the gate on every pull request, against the base branch's policy. |

## The migration

Core was written imperatively. Turning FunctionalLint on found about 420 problems, and all are fixed: every rule is a
build error in Core. The baseline is empty and the gate fails if anything appears in it. (It is kept for a future
rule: lower the rule in `functionallint.policy`, record today's cases with `python3 scripts/functional-gate.py --update`,
fix them, and `--update` again; it only ever lowers counts.)

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
- **Collections in records: `ValueList<T>` and `ValueMap<T>`.** A record compares by value, but an `ImmutableArray`,
  list or dictionary inside it compares by reference (FP0014). Use Core's `ValueList<T>` and `ValueMap<T>`
  (`ValueCollections.cs`): they compare by contents and work in the netstandard2.0 analyzer. `ValueMap` keeps its
  entries' order and has no indexer: use `TryGetValue`. The CLI reads and writes both as plain JSON arrays and
  objects (`src/Connascence.Cli/ValueJson.cs`), so Core stays free of serialization.
- **Don't switch rules off.** No `#pragma`, `[SuppressMessage]`, `#nullable disable` or lowered severities: the gate
  reports them (FP0016). Agents shouldn't edit `functionallint.policy`; ask the owner.

Rule docs, with before/after examples: `external/functional-csharp/FunctionalLint/docs/rules/`. Some rules fix
themselves: `external/functional-csharp/FunctionalLint/heal.sh src/Connascence.Core/Connascence.Core.csproj`.
