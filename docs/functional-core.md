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
| `functionallint.policy` | The owner's rules. Lists Core as the only core, allows Roslyn as its only reference, and lowers the rules Core still breaks to warnings. |
| `.functionallint-baseline.json` | How many of each rule every Core file still breaks. |
| `scripts/functional-gate.py` | The gate. Fails on any error, or when a file breaks a rule more often than its baseline. |
| `.githooks/pre-commit` | Runs the gate when a commit touches Core, the policy or the baseline. |
| CI | Runs the gate on every pull request, against the base branch's policy. |

## The migration

Core was written imperatively. Turning FunctionalLint on found about 420 problems:
- Fixed now: every nullable warning, every culture rule, and the 104 fixes FunctionalLint makes by itself
  (readonly fields, `init` properties, records, read-only collection types in signatures).
- Left for later, as warnings: 350 violations of FP0001 (loops), FP0002 (mutation), FP0003 (mutable fields),
  FP0004 (classes), FP0005 (mutable collections), FP0008 (mutable types in signatures), FP0014 (records holding
  lists) and FP0015 (calls that can throw).

The baseline is a ratchet:
- New code must be functional. Adding a loop to a file that already has some still fails the gate.
- After fixing violations, run `python3 scripts/functional-gate.py --update` to lower the baseline. It refuses to
  raise it.
- When a rule reaches zero everywhere, `--update` says so. Then remove it from both lists in `functionallint.policy`,
  and it becomes a build error.

The refactor so far doesn't change what the tool reports: the old and new code give byte-identical JSON reports on
this repo, `tests/fixtures/tally` and functional-csharp, in both C# and generic mode.

## Things to know when changing Core

- **Keep to netstandard2.0 APIs.** The Roslyn analyzer compiles Core's source for netstandard2.0. For example, use
  `ImmutableHashSet<T>`, not `IReadOnlySet<T>` (which netstandard2.0 lacks). The analyzer project has polyfills
  for records, `init` and `required` (`src/Connascence.Analyzers/Polyfills`).
- **`Finding` stays a class.** The CLI updates findings as it goes (priority, acceptance, cross-language merges) and
  keeps them in sets, so a finding needs identity, not record equality. Its FP0004 is in the baseline on purpose.
- **Records holding lists (FP0014).** FunctionalLint's fix is `ValueList<T>` from functional-csharp's sample library,
  which Core doesn't reference. That's an open question for the owner, so FP0014 stays a warning.
- **Don't switch rules off.** No `#pragma`, `[SuppressMessage]`, `#nullable disable` or lowered severities: the gate
  reports them (FP0016). Agents shouldn't edit `functionallint.policy`; ask the owner.

Rule docs, with before/after examples: `external/functional-csharp/FunctionalLint/docs/rules/`. Some rules fix
themselves: `external/functional-csharp/FunctionalLint/heal.sh src/Connascence.Core/Connascence.Core.csproj`.
