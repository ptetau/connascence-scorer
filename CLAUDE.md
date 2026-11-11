# Connascence analyzer

Scores coupling that makes code expensive to change (C# via Roslyn, other languages as text), plus a
pre-commit gate and three agent skills that decide what to do about it. See README.md.

## Layout

- `src/Connascence.Core`: detectors (`CSharp/`, `Generic/`), scoring, settings
- `src/Connascence.Cli`: the `connascence` command: workspace walk, futures, baseline, gate, reports
- `src/Connascence.Analyzers`: the same C# findings as Roslyn build warnings
- `skills/`: connascence-review, connascence-futures-explore, connascence-futures-interview
- `tests/Connascence.Tests`: unit tests (a console app, not xUnit)
- `tests/fixtures/tally`: realistic sample app with planted coupling, shared by scenarios and skill evals
- `tests/scenarios`: detection, change-prediction and gate scenarios (see its README)
- `tests/skills`: skill evals that run real Claude Code sessions (see its README)

## Checks to run

```sh
dotnet build
dotnet run --project tests/Connascence.Tests --no-build
python3 tests/scenarios/run.py
```

Skill evals cost API usage; run them when a skill or the CLI output an agent reads has changed:
`python3 tests/skills/run.py --case <name>`.

## Conventions

- When a detector change fixes a known gap, the scenario runner reports `FIXED`: remove that `gap` marker.
- When you find a new weakness, add a scenario with a `gap` marker rather than leaving it undocumented.
- Keep the fixture compiling (`dotnet build tests/fixtures/tally/Tally.Api` in a copy) so agents in skill evals can verify refactors.
