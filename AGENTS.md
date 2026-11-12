# AGENTS.md: using the connascence tool

This file is for coding agents. It says how to install the tool, set it up in a repository, and act on what it
reports. For changing this repository's own code, read `CLAUDE.md` instead.

The tool finds coupling that makes code expensive to change: places that must change together, where a missed
place isn't caught by the compiler. It scores each case, explains it, and suggests a fix. It can't know whether
the code will change, so it ranks findings against a list of likely changes (**futures**). The skills in
`skills/` make the refactor-or-accept decision. C# gets full analysis (Roslyn); other languages get a rougher
text-based mode.

## Install

The tool isn't on NuGet. Build it from source (needs the .NET 11 SDK):

```sh
git clone --recurse-submodules https://github.com/ptetau/connascence-scorer && cd connascence-scorer
dotnet pack src/Connascence.Cli -o ./nupkg
dotnet tool install -g Connascence.Tool --add-source ./nupkg
connascence version          # prints 0.2.0
```

If `connascence` isn't found afterwards, add `~/.dotnet/tools` to `PATH`. If .NET is installed somewhere
unusual, set `DOTNET_ROOT`.

## Set up a repository

Run these from the target repository's root:

1. **Config, futures template and hook:** `connascence init --hook`. This writes `.connascence.json`,
   `.connascence/futures.json` (a placeholder with one example future) and `.git/hooks/pre-commit`.
2. **Exclude code that is bad on purpose or never edited:** add globs to `exclude` in `.connascence.json`
   (demo apps, vendored code, seed data that is rewritten rather than edited). Tests, mocks, generated files
   and migrations are already skipped.
3. **Skills:** copy `skills/connascence-review`, `skills/connascence-futures-explore` and
   `skills/connascence-futures-interview` from this repository into the target's `.claude/skills/`.
4. **Futures:** replace the placeholder. Use the connascence-futures-interview skill if the product owner is
   available, otherwise connascence-futures-explore. Check with `connascence futures`: every future should touch
   some findings, or say in its description why not.
5. **Baseline:** `connascence baseline`. Existing findings then don't block commits; only new or growing ones do.
6. **Agent instructions:** paste the block from `docs/AGENTS-snippet.md` into the target's `CLAUDE.md` or
   `AGENTS.md`. It tells agents to load the review skill when the hook fails.
7. **Commit** `.connascence.json` and everything under `.connascence/` except `log/runs.jsonl` (already ignored).

Optional: also run the gate in CI, since a local hook can be skipped. The checkout needs full history
(`fetch-depth: 0`):

```sh
connascence check . --since origin/main --format markdown
```

## When the pre-commit hook fails

Load the connascence-review skill and follow it. In short:

1. Get the agent-friendly report: `connascence check --staged --format markdown`.
2. For each finding: open every listed place, read its cause and change probe, and the futures that touch it.
3. **Refactor** when a high- or medium-likelihood change would force edits in two or more of the places, and a
   missed edit wouldn't be caught at compile time. Also refactor when the fix is small and makes the code clearer.
4. **Accept** when no plausible change touches it, the places always change together in one obvious spot, it is
   generated code, seed data, a test fixture or a format fixed outside the team, or the fix is planned as its own
   change.
5. **Split** a finding whose places don't all share a meaning (for example, a display date format that only
   happens to match a bank's file format). Refactor the ones that share it, and leave the coincidental ones alone.
6. Log every decision, with a reason that names the change you considered:

   ```sh
   connascence decide <id> refactor --reason "<the change it protects against>" --future F1
   connascence decide <id> accept --reason "<the change considered> and why it isn't hurt"
   ```

   For a refactor, use the id from *before* the change; the finding has usually gone afterwards. Pass
   `--future` only for a future that would actually force edits in those places.
7. Commit `.connascence/accepted.json` and `.connascence/log/decisions.jsonl` together with the change.

Never accept just to get past the gate, and never use `git commit --no-verify` to get past it. If unsure, tell
the user what you found instead of accepting. If the user asked only for a review, recommend decisions and stop:
don't edit code or log decisions.

## Commands

| Command | Use it to |
|---|---|
| `connascence analyze [path] --format markdown --top 30` | See the costliest findings, ranked by priority |
| `connascence analyze --format json --all` | Get every finding as JSON (see below), including accepted ones |
| `connascence check --staged` | Gate the staged files (what the hook runs) |
| `connascence check --since <ref>` | Gate everything changed since a commit or branch (CI) |
| `connascence check --files a.cs,b.cs` | Gate specific files |
| `connascence decide <id> accept\|refactor --reason "..." [--future F1]` | Log a decision; accept also stops the finding failing the gate |
| `connascence accept <id> --reason "..."` | Same as `decide <id> accept` |
| `connascence reopen <id>` | Remove an acceptance |
| `connascence baseline` | Record current findings as existing debt |
| `connascence futures` | List futures and the findings each one touches |
| `connascence history` | Score trend and logged decisions |
| `connascence calibrate --months 12` | Check findings against what changed together in git |
| `connascence init [--hook]` | Write config and futures template; `--hook` installs the pre-commit hook |

Every command takes an optional repository path (default: the current folder). Other options:
- `--format console|markdown|json|sarif` (`agent` is an alias for `markdown`)
- `--out <file>`
- `--top N` (console and markdown only; the default is 15 for console, 50 for markdown)
- `--mode auto|csharp|generic`
- `--no-git` (skip the git history analysis, which is faster)
- `--max-cost N` and `--min-score N` (for `check`)
- `--by <name>`, or the `CONNASCENCE_ACTOR` environment variable, to set who a decision is logged as

**Exit codes:** `0` ok or check passed, `1` check failed, `2` usage or runtime error (for example a missing
`--reason`, or an unknown id passed to `accept`).

## How `check` decides

A finding fails the check when all of these hold:

- its cost is at least `thresholds.maxFindingCost` (default 20)
- it isn't accepted (by `decide ... accept` or an inline comment)
- it isn't in the baseline (with the default `failOn: "new"`)
- at least one of its places is in the changed files
- it is **new**, or it **grew** (a place joined it). A finding that shrank, or kept the same size with its places
  moved around, doesn't fail.

The check also fails if `minScore` is above 0 and the score is below it.

## Reading a finding

Each finding has:

- **`ruleId`, `kind`, `title`**: what was found.
- **`cause`**: why it was flagged.
- **`suggestion`**: how to fix it.
- **`changeProbe`**: the question to answer before deciding.
- **`locations`**: the places involved. Each has `file`, `line`, `endLine` (for copied blocks), `project`, `type`, `member`, `snippet` and `role`.
- **`degree`**: how many places must change together.
- **`distance`**: how far apart they are: `SameMember`, `SameType`, `SameFile`, `SameProject` or `CrossProject`.
- **`cost`**: strength × degree × distance weight.
- **`priority`**: cost, raised for files that change often in git and for futures that touch the finding, and lowered for stable areas.
- **`futures`**: ids of the futures that touch it.
- **`change`**: compared with the baseline, one of `new`, `grew from N to M places`, `shrank from ...` or `changed places`. It is absent when the finding is unchanged or there is no baseline.
- **`accepted`, `acceptedReason`, `inBaseline`, `stableReason`**: its status.
- **`fingerprint`**: the **id**.

An id survives edits elsewhere and line moves, but changes when the coupling itself changes (a place joins or
leaves). So an accepted finding comes back for review when it grows. Upgrading the tool can change ids too: run
`connascence baseline` again after an upgrade.

The JSON report has the same fields in `findings[]`, plus `summary` (`score`, `loadPerKloc`, `lines`, `byKind`,
`byFile`), `futures` and `warnings`. `--format sarif` gives SARIF 2.1.0 for code-scanning tools.

## Accepting inline

A comment containing `connascence: accept` on any one of a finding's places, or on the line above it, accepts
the whole finding. The rest of the comment is the reason. It works in any language's comment syntax:

```csharp
// connascence: accept - column order fixed by the bank's file spec
var amount = parts[3];
```

## futures.json

```json
{
  "product": "Shop",
  "updated": "2026-11-12T00:00:00Z",
  "futures": [
    {
      "id": "F1",
      "title": "Order statuses grow (returns, partial shipping)",
      "description": "Why it's likely, and why it touches no code if it doesn't.",
      "likelihood": "high",
      "horizon": "next quarter",
      "source": "interview",
      "areas": ["src/Orders/**"],
      "keywords": ["OrderStatus", "Ship"]
    }
  ],
  "stable": [
    { "reason": "Fixed by the bank's published spec", "areas": ["src/BankExport/**"], "keywords": [] }
  ]
}
```

- **`likelihood`** is `high`, `medium` or `low`.
- **`source`** is `interview` (the owner said so), `derived` (follows from an interview future; name the parent
  id in the description) or `explore` (inferred from the code).
- **`areas`** are path globs from the repository root (`*`, `**` and `?`).
- **`keywords`** match whole words, against a finding's title, subject, files, types and members, or against
  the code of at least half its places. Prefer type and member names; words like `Status` or `Items` match too
  much.
- A finding is **stable** only when every one of its places is in a stable area. Don't mark code stable if a
  future expects part of it to change.
- Keep ids unchanged when editing the file, so logged decisions still point at the right future.

## Files it keeps

| File | Commit it? | Contents |
|---|---|---|
| `.connascence.json` | yes | Settings: excludes, thresholds, weights |
| `.connascence/futures.json` | yes | Likely futures and stable areas |
| `.connascence/baseline.json` | yes | Existing debt |
| `.connascence/accepted.json` | yes | Accepted findings and reasons |
| `.connascence/log/decisions.jsonl` | yes | Every refactor and accept decision |
| `.connascence/futures-interview.md` | yes | Interview notes, if the interview skill was used |
| `.connascence/log/runs.jsonl` | no (ignored) | One line per run |

## Rules

C#: `CNM01` magic value, `CNM02` boolean flag argument, `CNP01` positional parameters, `CNP02` tuple unpacked
by position, `CNP03` fields read by index, `CNA01` copied logic, `CNA02` repeated format or regex, `CNA03`
independently set-up hash or crypto, `CNE01` setup method that must be called first, `CNT01` fixed wait,
`CNV01` constant defined in several types, `CNV02` repeated limit, `CNI01` shared mutable static.

Other languages (text-based): `GNM01` magic value, `GNM02` boolean flag, `GNP01` long positional parameter list,
`GNA01` copied lines, `GNT01` fixed wait, `GNV02` repeated limit, `GNI01` global state. When the same value is
used with the same subject in C# and another language, the two become one `CNM01` or `CNV02` finding.

## Gotchas

- **Score:** treat it as a trend for one codebase. Don't compare it across codebases.
- **Missing dependencies:** C# analysis compiles the source without NuGet packages, so some calls into external
  libraries don't resolve. Calls between your own types do.
- **Coupling it can't see:** execution order, timing and identity are only found where they leave a mark in the
  code. The tool also doesn't read project files (`.csproj`), and doesn't score coupling the compiler checks
  (names, types, interface implementations).
- **Inline accepts:** `connascence: accept` with no reason is accepted with "(no reason given)". Always write a
  reason.
- **The hook passes without checking** when `connascence` isn't on `PATH`. It prints a one-line notice, which is easy
  to miss. Make sure the tool is installed wherever commits are made.
