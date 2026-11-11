---
name: connascence-review
description: Review code changes through the connascence lens. Runs the connascence tool, weighs each costly coupling against likely futures, then refactors or accepts it with a logged reason. Use before committing, when the connascence pre-commit check fails, or when asked to review coupling or changeability.
---

# Review with the connascence lens

The tool measures coupling. It cannot know whether the code will change. You decide that, using the likely futures. High coupling in code that won't change costs nothing. High coupling in code that will change is expensive every time.

## 1. Run the tool

If the user asked only for a review, stop at recommendations: report each finding with the decision you'd make and why, and don't edit code or log decisions until they say go. (Writing futures, step 2, is fine; say that you did.)


- Before a commit, or when the hook failed: `connascence check --staged --format markdown`
- For a wider review: `connascence analyze --format markdown --top 30`

Each finding has an id, a cause, a suggested fix, a change probe, the places involved, and the futures that touch it. A finding marked "grew from N to M places" means this change made existing coupling worse.

## 2. Make sure there are futures

If `.connascence/futures.json` is missing, or older than about three months, or the spec has changed a lot since: run the futures skills first. Use connascence-futures-interview if the owner is available, otherwise connascence-futures-explore. Without futures you are guessing at step 3.

## 3. Decide each finding

Work from the top (highest priority) down. For each one:

1. Open every listed place. Understand what is shared: a value's meaning, an order, an algorithm, a call sequence, a timing, an instance.
2. Read the change probe and the futures that touch it. Add any plausible change you can see that the list misses.
3. For each plausible change, trace it: which of the listed places would it force you to edit, and would a missed edit be caught by the compiler or a test?
4. Decide:

**Refactor** when any high or medium likelihood change would force edits in two or more of the places, and a missed one would not be caught at compile time. Also refactor when the fix is small and makes the code clearer anyway.

**Accept** when one of these holds, and say which:
- No plausible change touches it (the code is stable, or in an area the owner marked stable).
- The places already always change together in one small, obvious spot, and the fix would add indirection that makes it harder to read.
- It is generated code, seed data, a test fixture, or a format fixed by someone outside the team.
- The fix belongs in its own planned change. Name it.

Never accept just to get past the gate, and never suggest skipping the hook (`--no-verify`) as a way through. If you aren't sure, say so to the user rather than accepting. If the user is in a hurry and the right fix is small, make it rather than stopping to ask.

**Split a finding when only some places share a meaning.** The tool groups places by what they look like, so a finding can mix real coupling with coincidence: a display date format and a bank file's date format that happen to be the same string, or two limits of 50 that mean different things. Decide each group on its own. Refactor the places that must change together (give them one home), and leave the coincidental ones separate and untouched. Never accept the whole finding because one place doesn't belong. Log one decision for the finding and say how you split it.

## 4. Refactor well

- Use the suggestion as a starting point, not a script. Aim for one of two moves: make the connascence weaker (meaning → named type, position → names, algorithm → one shared function, execution order → types that make the wrong order impossible, identity → one owner passed in), or bring the places closer together (same method, same class, same module).
- Make the smallest change that does it. Keep it readable: a little obvious repetition beats a clever abstraction.
- Stay inside the scope of the current task. If the fix sprawls into unrelated code, accept with "planned as its own change" and tell the user.
- Build and run the tests. Run `connascence check --staged` again.

## 5. Log the decision

Every decision is logged so humans can audit it later:

```
connascence decide <id> refactor --reason "Status is now the OrderStatus enum; returns (F1) adds a member, not five edits" --future F1
connascence decide <id> accept   --reason "Seed data for the demo store; rewritten wholesale if it changes, never edited in place"
```

A good reason names the change you considered and why it is or isn't hurt. For a refactor, that means the future change it protects against ("adding PartiallyPaid is now one enum member"), not only what you changed ("replaced 5 with InvoiceStatus.Overdue"). An accepted finding comes back for review if the coupling grows (a new place joins it), because its id changes.

For a refactor, log the decision after the change, using the id from before the change (it will no longer appear in the report).

Pass `--future` only for a future that would actually force edits in these places. If none would, leave it off and say so in the reason.

Commit `.connascence/accepted.json` and `.connascence/log/decisions.jsonl` together with the change they belong to. An acceptance that stays uncommitted fails the gate for everyone else.

## 6. Leave old findings alone unless asked

Findings in the baseline are existing debt. Don't fix them as a side effect of an unrelated change. Mention any that a likely future will hit soon, so the user can plan them.

## 7. Report back

Finish with a short table: id, finding, decision, reason, future. Note anything you weren't sure about, and any future that touches several costly findings: that is a good candidate for a planned refactor.

## Quick guide by kind

| Kind | What's shared | Usual fix |
|---|---|---|
| Meaning | What a value means (3 = shipped, "A" = admin, true = draft) | Enum, named constant, or small type |
| Position | Argument, tuple or field order | Named arguments, a record, distinct types |
| Algorithm | A copied rule, format, regex or hash | One shared function or class |
| Execution | Call order (Open before Send) | Constructor or factory; return a new type once set up |
| Timing | A fixed sleep or delay | Wait on the real event; one retry policy |
| Value | Limits or constants that must stay equal | One source of truth |
| Identity | Shared mutable static or global | One owner, passed in; or immutable |
