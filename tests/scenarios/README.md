# Analyzer scenarios

These tests check that the tool is useful, not just that it runs. Each scenario is a folder with a
`scenario.json`. Run them all with:

```sh
dotnet build
python3 tests/scenarios/run.py            # add -v to see passing checks too
python3 tests/scenarios/run.py --suite changes --filter status
```

## The three suites

| Suite | Question it answers |
|---|---|
| `detection/` | Does each detector find the coupling it should, and stay quiet on look-alikes that are fine? |
| `changes/` | When a realistic change lands, had the tool already linked the places that had to change together? |
| `gate/` | Does `connascence check` block and pass the right commits, and do decisions behave as documented? |

Most scenarios run against `tests/fixtures/tally`, a small invoicing app (C#, Python, JS) with coupling
planted on purpose. `detection/tally-ground-truth` lists every planted coupling and every look-alike.

## Known gaps

Any expectation can carry `"gap": "why it fails today"`. A gap that fails is shown as `GAP` and does not fail
the run. A gap that passes is shown as `FIXED` and does fail the run, so the marker is removed and the fix
stays fixed. The gap list is the to-do list for the detectors.

## scenario.json

Common keys:

- `fixture`: a folder under `tests/fixtures` to start from. A `code/` folder next to `scenario.json` is copied on top.
- `futures`: written to `.connascence/futures.json`. `config`: written to `.connascence.json`.

A **matcher** picks out findings. Every key given must hold:
`rule`, `groupKey`, `groupKeyStartsWith`, `titleContains`, `files` (all of), `anyFile`, `members` (all of),
`lines` (`"file:line"`, all of), `degree`, `minDegree`, `minCost`, `distance`, `accepted`, `future`, `stable`.
Add `why` to say what it's for and `gap` to mark a known weakness.

**detection**: `expect` (each matcher must match a finding), `forbid` (no finding may match), `maxFindings`, `mode`.

**changes**: describe the change as `edits` (`file`, `old`, `new`, applied to a copy of the fixture) and/or an
`after/` folder of new files. A finding *predicts* the change if the change edits at least two of its places.

- `predictedBy`: matchers; one predicting finding must match each.
- `notPredictedBy`, `expectNoPrediction`: for control changes that should not be claimed by any finding.
- `filesCovered`: every listed edited file must be named by a predicting finding.
- `rank`: `{ "top": N, "finding": matcher }`. With the scenario's futures, the finding must be in the top N by priority.

**gate**: `steps`, run in order inside a fresh git repo of the fixture:
`baseline`, `commit`, `write` / `append` / `replace` (edit files), `stage`,
`run` (CLI args, run on the fixture's root; `exit`, `contains`, `lacks`; `viaSymlink` passes the root through a symlink), `decide` (`accept|refactor` on a `finding` matcher or remembered `id`,
with `reason`, `future`, `exit`), `remember` (store a finding's id), `reopenId`, `expectFinding` (matcher, `count`).
