# Connascence analyzer

Scores coupling that makes code expensive to change (C# via Roslyn, other languages as text), plus a
pre-commit gate and three agent skills that decide what to do about it. See README.md.

## Layout

- `src/Connascence.Core`: detectors (`CSharp/`, `Generic/`), scoring, settings. A functional core (see below)
- `src/Connascence.Cli`: the `connascence` command: workspace walk, futures, baseline, gate, reports
- `src/Connascence.Analyzers`: the same C# findings as Roslyn build warnings
- `skills/`: connascence-review, connascence-futures-explore, connascence-futures-interview
- `tests/Connascence.Tests`: unit tests (a console app, not xUnit)
- `tests/fixtures/tally`: realistic sample app with planted coupling, shared by scenarios and skill evals
- `tests/scenarios`: detection, change-prediction and gate scenarios (see its README)
- `tests/skills`: skill evals that run real Claude Code sessions (see its README)
- `external/functional-csharp`: FunctionalLint, a git submodule (`git submodule update --init`)
- `scripts/functional-gate.py`: the FunctionalLint gate for Core

## Checks to run

```sh
dotnet build
dotnet run --project tests/Connascence.Tests --no-build
python3 tests/scenarios/run.py
python3 scripts/functional-gate.py
```

Skill evals cost API usage; run them when a skill or the CLI output an agent reads has changed:
`python3 tests/skills/run.py --case <name>`.

## Conventions

- When a detector change fixes a known gap, the scenario runner reports `FIXED`: remove that `gap` marker.
- When you find a new weakness, add a scenario with a `gap` marker rather than leaving it undocumented.
- Keep the fixture compiling (`dotnet build tests/fixtures/tally/Tally.Api` in a copy) so agents in skill evals can verify refactors.

## Coupling check

This repo runs its own analyzer as a pre-commit hook (`.githooks/pre-commit`, built from this checkout) and in CI on pull requests.
Turn the hook on once per clone: `git config core.hooksPath .githooks`. It fails when a change adds or grows
coupling that makes code expensive to change.

When it fails, load the connascence-review skill (with your skill tool, before deciding anything) and follow it. The
short version below is a reminder, not a substitute:
- Read each finding's cause, suggested fix, change probe and the futures that touch it (.connascence/futures.json).
- If a likely change would force edits in several of the listed places, refactor. Keep the fix small and readable.
- If not, accept it: `connascence decide <id> accept --reason "<change considered> and why it isn't hurt"`.
- Log refactors too: `connascence decide <id> refactor --reason "..." --future <id>`.
- Never accept just to pass the gate. Don't fix unrelated baseline findings unless asked.

If .connascence/futures.json is missing or stale, run the connascence-futures-explore skill first
(or connascence-futures-interview if the owner is available).

The skills live in `skills/`; when working in this repo, read them from there (for example `skills/connascence-review/SKILL.md`)
if they aren't installed as Claude Code skills. The futures are in `.connascence/futures.json`; `samples/` is excluded on purpose.

## Functional core

`src/Connascence.Core` is checked by FunctionalLint and is being migrated to functional C#. Read
`docs/functional-core.md` before changing it. In short:
- Write new Core code functionally: records with `init`, static classes, LINQ instead of loops, no reassignment,
  immutable collections, `IReadOnlyList<T>` in signatures, `TryGetValue`/`FirstOrDefault` instead of calls that throw,
  `StringComparison.Ordinal` and `CultureInfo.InvariantCulture`. Full guide:
  `external/functional-csharp/FunctionalLint/agents/AGENTS.md`.
- Keep to netstandard2.0 APIs: the Roslyn analyzer compiles the same source.
- `scripts/functional-gate.py` fails if any file gains a violation. After fixing some, lock it in with `--update`.
- Never switch a rule off (`#pragma`, `[SuppressMessage]`, `#nullable disable`, severities), and don't edit
  `functionallint.policy`. Ask the owner if a rule seems wrong.

