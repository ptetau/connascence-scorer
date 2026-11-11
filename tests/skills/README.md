# Skill evals

These check that the three skills in `skills/` lead an agent to good decisions, not just to running commands.
Each case starts a real Claude Code session (`claude -p`) in a throwaway copy of the Tally fixture, with the
skills installed in `.claude/skills/`, the CLI on `PATH` and the pre-commit hook installed. Then it grades
the result two ways:

- **Checks**: scripted pass/fail on the repo afterwards. For example: was the decision logged, does the gate
  pass, was the commit made with the hook running, does `futures.json` hold the owner's roadmap and not what
  they ruled out, was unrelated code left alone.
- **Rubric**: judgement calls graded by a separate model call that sees the skill text, the agent's messages
  and commands, the diff, `futures.json` and the decision log. For example: did it keep the bank's date format
  apart from the display format, did it push back when told to accept everything.

Interview cases give the agent an `ask-owner` command instead of a live user. A separate model answers in
character from `owner.md`, which the agent never sees, so vague questions get vague answers.

## Running

Needs a logged-in `claude` CLI, dotnet and a built CLI (`dotnet build`). It costs real usage: roughly $0.50 to $2
per case on the default model.

```sh
python3 tests/skills/run.py --list
python3 tests/skills/run.py --dry-run                 # build every case's repo and run the hook; no model calls
python3 tests/skills/run.py --case review-            # one skill's cases
python3 tests/skills/run.py --runs 3 --case explore   # repeat to see how consistent it is
python3 tests/skills/run.py --regrade <out folder>    # re-apply checks and judge to saved runs
```

Options: `--model`, `--judge-model`, `--owner-model` (default `haiku`), `--jobs` (default 3), `--out`, `--no-judge`.

Each run writes `summary.md` and `results.json` to the out folder (default under the system temp folder),
plus a folder per case with the repo as the agent left it, `transcript.jsonl`, the judge's prompt and, for
interviews, `owner-log.md`.

## Cases

| Case | Skill | What good looks like |
|---|---|---|
| `review-refactor-grown-status` | review | Refactors status codes a high-likelihood future will hit; decides the cache growth; logs both; commits with the hook |
| `review-accept-stable-bank-format` | review | Accepts positional coupling to the bank's fixed file layout, citing it; no restructure |
| `review-pressure-to-accept` | review | Told "accept whatever", still doesn't accept costly coupling blindly; says so |
| `review-no-futures` | review | Notices futures.json is the init placeholder; generates futures or says decisions are guesses; doesn't commit |
| `review-coincidental-format` | review | Gives the display date format one home, but keeps the bank's identical-looking format separate |
| `explore-fresh` | explore | 5-15 grounded futures; README roadmap rated high; bank files stable; every future tied to code |
| `explore-merge-existing` | explore | Keeps the owner's interview future exactly; ids stable; fills roadmap gaps |
| `interview-fresh` | interview | Short, specific interview; owner's "no" answers produce no futures; derived futures name parents; notes saved |
| `interview-refine-existing` | interview | Refines an explored file: keeps ids, drops what the owner rejected, raises what they confirmed |

## Adding a case

Create `cases/<id>/case.json`:

- `skill`, `description`, `prompt`
- Repo setup: `fixture` (default `tally`), `history` (hot-file commits, default on), `futures` (object, or `null`
  to keep the `init` placeholder), `baseline` (default on), `agentsSnippet` (write the docs/AGENTS-snippet.md
  text into CLAUDE.md), `writes` / `edits` (the user's pending change), `stage`, `owner` (persona file)
- `checks`: see the `c_*` methods in `run.py`. Types: `futures_valid`, `futures_sources`, `futures_mention`,
  `futures_absent`, `futures_touch_code`, `futures_preserved`, `future_likelihood`, `stable_mention`,
  `derived_parents`, `decision`, `no_decision`, `finding`, `gate_passes`, `committed`, `not_committed`,
  `untouched`, `command_ran`, `skill_used`, `final_mentions`, `file_exists`, `owner_calls`, `any_of`
- `rubric`: plain-language criteria for the judge

Run `--dry-run` first to confirm the pending change trips the hook the way the case assumes.
