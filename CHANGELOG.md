# Changelog

## Unreleased

- `calibrate` reports, per finding, how often two of its files changed in the same commit, overall, by kind and
  either side of the gate. The old per-pair figures made large findings look worse than they are.
- Refactor decisions recover a finding's rule, title and cost from the baseline when the run log doesn't have
  it (the run log only tracks findings costing 10 or more). Baselines now store titles.
- Location roles are constants in `Roles`; the strings in reports are unchanged.
- The Roslyn analyzer skips the same files as the CLI (generated code, migrations, tests and mocks), using rules
  shared in `Paths`. It used to count migrations, so its warnings disagreed with the report. New setting:
  `connascence.include_tests`. The analyzer now has scenario tests (`tests/scenarios/analyzer`).

## 0.2.0 (2026-11-12)

**Finding ids changed.** Several detectors now group places differently, so ids in an existing
`.connascence/baseline.json` and `.connascence/accepted.json` won't match. After upgrading, run
`connascence baseline` again and review any accepted findings that come back.

Detection:
- Near-copies: the longest shared run of statements between members, with local and parameter names
  normalised. Copies tweaked at the end, or with renamed variables, are found. Repeated guard clauses aren't.
- Status codes 0, 1 and 2 count when compared with, assigned to or switched on a code-like name, in two or more places.
- Generic mode reads `row["status"]`, counts assignments to code-like names, and catches `setTimeout`/`setInterval` delays.
- The same value with the same subject in C# and in another language is one finding.
- Futures tag a finding only when the keyword is about it: whole words, matched on its title, subject, files,
  types and members, or in the code of at least half its places.

Noise (found on dotnet/eShop and bitwarden/server):
- Statics that are never written after their declaration are no longer shared mutable state.
- Mocks, fakes and stubs count as test code.
- Generic mode ignores values the platform defines (`typeof x === 'function'`, `document.readyState`,
  `event.key`, `__name__`), configurable with `IgnoredSubjects`.
- Migration scripts are excluded by default: lowercase `migrations/`, Rails `db/migrate`, `DbScripts`,
  dated `2021-04-07_x.sql` and Flyway `V3__x.sql`.

Gate:
- A finding that moved to different places without growing no longer fails `check`, as the README says.

Skills: see the commit history and `tests/skills/RESULTS.md`.

## 0.1.0

First version.
