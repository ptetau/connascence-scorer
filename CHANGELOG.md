# Changelog

## Unreleased

- **Needs .NET 11.** The tool, the CLI and the tests now target .NET 11 (the analyzer package still targets
  netstandard2.0). Install the .NET 11 SDK or runtime before updating the tool.
- `Connascence.Core` is now a functional core, checked by FunctionalLint (see `docs/functional-core.md`).
  Findings are unchanged. On .NET 11 a per-file cost in the summary can differ by 0.1 from .NET 8, from rounding.
- `check` fails closed: if git can't list the changed files (a missing ref, a shallow clone), it exits 2 instead of
  passing. File names with non-ASCII characters and roots reached through a symlink now match.
- `check --staged` analyses what is staged (git's index), not the working tree.
- Growth includes cost: a finding whose places moved further apart (same count, higher cost) fails like one that
  gained places. An inline accept lapses when its finding grows past its size in the baseline. The baseline now
  records accepted findings' sizes too; run `connascence baseline` again to get this for existing inline accepts.
- Generic mode: `return f(...)` is no longer read as a definition of `f`, and the long-parameter check reads the
  code once instead of once per function (45 s to under 2 s on 25k lines of Python).
- `scoring.strength` and `scoring.distanceWeight` merge over the defaults and ignore key case; unknown keys are warned
  about. Before, a partial map dropped the other defaults to 1.
- Core API: findings are immutable records, `Scoring.Score` returns the scored finding, and `CSharpEngine.Analyze`
  returns detector warnings with the findings instead of adding them to the context.

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
