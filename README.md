# Connascence analyzer

Scores how much of a codebase's coupling will make it expensive to change, explains each case, and suggests how to fix it. Built for C#, with a rougher generic mode for other languages. Designed to run in a pre-commit hook that a coding agent (or a person) responds to.

The tool is deterministic: it finds and scores coupling. Whether that coupling matters depends on whether the code will change, which the tool can't know. So it works with a list of likely futures, written by the futures skills, and asks the reviewer to refactor or accept each finding with a logged reason.

## How the score works

Connascence (Page-Jones, 1992) means two pieces of code must change together to stay correct. There is no agreed formula for scoring it. This tool uses the three properties the literature agrees on:

- **Strength**: how hard the coupling is to see and fix. Name and type are free (the compiler handles them). Then meaning 3, position 4, algorithm 5, execution order 6, timing 7, value 8, identity 9.
- **Degree**: how many places must change together.
- **Distance**: how far apart they are. Same member 0.1, same type 0.25, same file 0.5, same project 1, across projects 2.

`cost = strength x degree x distance weight`

Strong coupling close together is cheap, which matches Page-Jones ("keep connascence within boundaries") and Weirich's rule of locality. The headline is the **load**: total open cost per 1,000 code lines, also shown as a 0-100 score (`100 x e^(-load/100)`). Treat it as a trend for one codebase, not a grade to compare across codebases.

**Priority** ranks the fix list: cost, raised for files that change often in git (volatility) and for code a likely future would touch, lowered for code the owner marked stable.

All weights are in `.connascence.json`. Nobody has validated them, so `connascence calibrate` checks them against your own git history: file pairs linked by findings should change together more often than random pairs.

## Quick start

```sh
dotnet tool install -g Connascence.Tool          # or build from source, below
cd your-repo
connascence init --hook                          # config, futures template, pre-commit hook
connascence analyze                              # see where you stand
connascence baseline                             # existing debt won't block commits
```

Then write real futures with the skills (see below), and commit as normal. The hook runs `connascence check --staged`.

## Commands

| Command | What it does |
|---|---|
| `analyze [path]` | Full report. `--format console\|markdown\|json\|sarif`, `--out file`, `--top N`, `--mode auto\|csharp\|generic`, `--no-git`, `--all` (include accepted) |
| `check [path]` | The gate. Fails (exit 1) if a finding costing `maxFindingCost` or more is new or grew, and touches the changed files. `--staged`, `--since <ref>`, `--files a,b`, `--max-cost`, `--min-score` |
| `decide <id> accept\|refactor --reason "..." [--future F1]` | Records a decision. Accept also stops the finding failing the gate |
| `accept <id> --reason "..."` | Same as `decide <id> accept` |
| `reopen <id>` | Removes an acceptance |
| `baseline` | Records current findings as existing debt |
| `history` | Score trend, tracked findings that came and went, and decisions |
| `futures` | Each likely future and the findings it touches |
| `calibrate [--months N]` | Tests the scoring against what changed together in git, and lists coupling the tool can't see |
| `init [--hook]` | Writes `.connascence.json`, a futures template and a `.gitignore`; `--hook` installs the pre-commit hook |

## The workflow

1. **Futures.** Run the `connascence-futures-interview` skill with the product owner, or `connascence-futures-explore` without them. Both write `.connascence/futures.json`: likely changes, how likely, and which code they touch. Plus `stable` areas the owner expects to stay put.
2. **Check.** The pre-commit hook fails when a change adds or grows costly coupling. Each finding says why it was flagged, how to fix it, and which future it would hurt.
3. **Decide.** The `connascence-review` skill walks each finding: if a plausible change would force edits in several of the places, refactor; if not, accept with a reason. Both are logged.
4. **Track.** `history` shows the trend and the decisions. An accepted finding comes back for review if the coupling grows, because its id changes when a new place joins it.

## What it detects (C#)

| Id | Kind | What |
|---|---|---|
| CNM01 | Meaning | The same literal compared with or assigned to the same thing in several places (`Status == 3`, `role == "admin"`, `config["Smtp:Host"]`) |
| CNM02 | Meaning | Bare `true`/`false` passed to your own methods |
| CNP01 | Position | Four or more parameters, two or more of the same type, called by position |
| CNP02 | Position | Methods returning tuples that callers unpack by position |
| CNP03 | Position | Fields read by index from a split string |
| CNA01 | Algorithm | The same method body copied (smaller findings inside the copies are folded into it) |
| CNA02 | Algorithm | The same format string or regex written out in several places |
| CNA03 | Algorithm | The same hash or crypto algorithm set up independently in several types |
| CNE01 | Execution | A setup method (`Open`, `Connect`, `Init`...) that must be called first, shown by a runtime guard or several callers |
| CNT01 | Timing | `Thread.Sleep` / `Task.Delay` waiting a fixed time |
| CNV01 | Value | The same named constant defined in several types |
| CNV02 | Value | The same limit checked in several places (`Length > 50`) |
| CNI01 | Identity | Mutable static state used from several members |

Tests are skipped by default (`includeTests`). Generated files, `bin`, `obj` and migrations are excluded.

## Generic mode

Files in other languages are read as text: repeated magic values and limits, boolean flag arguments, long positional parameter lists, copied blocks, fixed sleeps, and globals (Python `global`, Ruby `$var`). Rule ids start with `GN`. Distances are rough (same file or not). The markdown report adds a checklist of things to review by hand. Force it for everything with `--mode generic`.

## Accepting inline

A comment on a flagged line, or the line above, accepts the finding:

```csharp
// connascence: accept - column order fixed by the bank's file spec
var amount = parts[3];
```

## Roslyn analyzer

`Connascence.Analyzers` reports the same C# findings as build warnings, one project at a time (so distances never cross projects). It runs at the end of compilation, so warnings show on build rather than as you type.

```ini
# .editorconfig or .globalconfig
connascence.min_cost = 10
dotnet_diagnostic.CNM01.severity = suggestion
```

## Files it keeps

| File | Commit it? | Contents |
|---|---|---|
| `.connascence.json` | yes | Settings, weights, thresholds |
| `.connascence/futures.json` | yes | Likely futures and stable areas |
| `.connascence/accepted.json` | yes | Accepted findings and reasons |
| `.connascence/baseline.json` | yes | Existing debt |
| `.connascence/log/decisions.jsonl` | yes | Every refactor and accept decision |
| `.connascence/log/runs.jsonl` | no (ignored) | One line per run: score, load, tracked findings |

## Key settings

```jsonc
{
  "mode": "auto",                      // auto | csharp | generic
  "includeTests": false,
  "thresholds": { "minScore": 0, "maxFindingCost": 20, "failOn": "new" },
  "volatility": { "enabled": true, "months": 6, "medium": 3, "high": 11 },
  "log": { "enabled": true, "trackCost": 10 },
  "scoring": {
    "strength": { "Meaning": 3, "Position": 4, "Algorithm": 5, "Execution": 6, "Timing": 7, "Value": 8, "Identity": 9 },
    "distanceWeight": { "SameMember": 0.1, "SameType": 0.25, "SameFile": 0.5, "SameProject": 1, "CrossProject": 2 },
    "scoreScale": 100
  }
}
```

## Building

```sh
dotnet build                                    # needs nuget.org for Microsoft.CodeAnalysis.CSharp
dotnet run --project tests/Connascence.Tests    # unit tests
tests/e2e.sh                                    # full workflow in a throwaway git repo (needs connascence on PATH)
dotnet pack src/Connascence.Cli                 # the dotnet tool
dotnet pack src/Connascence.Analyzers           # the analyzer package
```

Offline, build against the Roslyn that ships inside the .NET SDK: `dotnet build -p:UseSdkRoslyn=true -p:RestoreSources=<empty folder>`. That works for the CLI and tests. The analyzer then has to target `net8.0` (`-p:AnalyzerTfm=net8.0`), which runs in `dotnet build` but not in Visual Studio.

## Known limits

- Detectors are heuristics. Expect some noise. Use `calibrate`, tune weights, or accept with a reason.
- The C# analysis builds one compilation from source files without NuGet references, so some calls into external libraries don't resolve. Calls between your own types do.
- Dynamic kinds (execution, timing, identity) are only found where they leave a mark in the code.
- Connascence of name and type are not scored. They're the healthy kinds.
