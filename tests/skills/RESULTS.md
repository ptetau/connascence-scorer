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
