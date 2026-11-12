# Skill eval results

One run per case on the default Claude Code model. Re-run after changing a skill and add a section here.

## 2026-11-11: first run

| Case | Checks | Rubric | Cost |
|---|---|---|---|
| review-refactor-grown-status | 8/8 | 6/6 | $0.64 |
| review-accept-stable-bank-format | 7/7 | 4/4 | $0.30 |
| review-pressure-to-accept | 4/4 | 3/4 | $0.39 |
| review-no-futures | 4/4 | 4/4 | $0.57 |
| review-coincidental-format | 5/6 | 2/3 | $0.34 |
| explore-fresh | 11/11 | 5/5 | $0.78 |
| explore-merge-existing | 5/5 | 3/3 | $0.65 |
| interview-fresh | 12/12 | 6/6 | $0.90 |
| interview-refine-existing | 6/6 | 3/3 | $0.84 |

Total about $5.40.

What went wrong:

- **review-coincidental-format** (real skill weakness). The agent correctly saw that the bank parser's
  `dd/MM/yyyy` only happens to match the display format. It then accepted all four places, reasoning that a
  shared constant would tie the bank parser to display. The better move was to give the three display places
  one home and leave the bank parser alone. The skill has no guidance for a finding where only some places
  truly share a meaning.
- **review-pressure-to-accept**. The agent pushed back on "accept whatever" and refactored. Two of its three
  logged reasons described the fix instead of the change it guards against.
- **review-pressure-to-accept** didn't invoke the Skill tool at all. The CLAUDE.md snippet alone was enough to
  steer it.
- **interview-fresh**: rounds 2 and 4 asked more than four questions. After the owner confirmed the list, the
  agent dropped areas from six derived futures without saying so.
- **interview-refine-existing**: the agent first proposed keeping the rejected multi-instance future at
  "low", and the owner had to correct it ("I said no, not low").
- **explore-fresh**: marked all of `BankFile.cs` stable while also predicting the statement parser in that
  file would change. It noticed the conflict but didn't resolve it.

## 2026-11-11: three runs each, after the first round of fixes

Fixes since the first run: the review skill splits mixed findings and names the protected change; interview
and explore skill tweaks; analyzer changes (near-copies, status codes 0-2, cross-language links, precise
futures tagging).

27 sessions, $17.12. All checks passed in 23 runs; judge criteria passed in 111 of 114.

- **review-coincidental-format: 3/3**, up from 0/1. The split-the-finding guidance works.
- **review-pressure-to-accept: 1/3 committed.** One run stopped to ask the user and offered `--no-verify`.
  In another, the gate blocked the agent's own refactor because it moved a status check (same size,
  different places). That was a gate bug, now fixed.
- **review-accept-stable-bank-format**: the agent left `accepted.json` and the decision log uncommitted
  in 2 of 3 runs.
- **review-refactor-grown-status #2**: tagged the cache decision with the GST future.
- **review-no-futures**: all three edited code during a review-only request.
- **interview-refine-existing #2**: wrote likelihood "unknown".
- **explore** (all 6 runs): the judge flagged generic keywords (`Status`, `Items`, `Table`).

## 2026-11-11: re-run of affected cases after the second round of fixes

Fixes: the gate no longer fails a same-size reshuffle; the review skill says to commit decision files, tag
`--future` only when it applies, stop at recommendations for review-only requests, never suggest
`--no-verify`, and make a small fix rather than asking a user in a hurry; explore and interview limit
likelihood to high, medium or low and prefer specific keywords.

17 sessions, $8.46. Checks passed in 16 (the exception: one run did the right thing without loading
the skill).

| Case | Runs | Checks | Rubric |
|---|---|---|---|
| review-refactor-grown-status | 3 | 3/3 | 18/18 |
| review-accept-stable-bank-format | 3 | 2/3 (skill not loaded once) | 12/12 |
| review-pressure-to-accept | 3 | 3/3, all committed | 10/12 |
| review-no-futures | 3 | 3/3, no code edits | 12/12 |
| review-coincidental-format | 3 | 3/3 | 9/9 |
| interview-refine-existing | 1 | 1/1 | 3/3 |
| explore-fresh | 1 | 1/1 | 5/5 |

Still open:

- In the pressure case the agent often works from the CLAUDE.md snippet without loading the skill (3 of 3
  runs). It behaves well, but the snippet could tell it to load the skill.
- In the same case, it tagged the shared-cache decision with the GST future in 2 of 3 runs. The tool
  suggests that future because the case's futures file uses the whole of `InvoiceService.cs` as its area, and
  the agent copies the tool's suggestion.

## 2026-11-12: CLAUDE.md snippet tells agents to load the review skill

The snippet in `docs/AGENTS-snippet.md` now says to load the connascence-review skill before deciding,
and that its short version is a reminder, not a substitute.

6 sessions, $2.75.

| Case | Runs | Skill loaded | Checks | Rubric |
|---|---|---|---|---|
| review-pressure-to-accept | 3 | 3/3 (was 0/3) | 3/3 | 12/12 (was 10/12) |
| review-accept-stable-bank-format | 3 | 3/3 (was 2/3) | 3/3 | 12/12 |

With the skill loaded, the pressure case's vague reasons and wrong future tags went away.
