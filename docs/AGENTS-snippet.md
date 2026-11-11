# Snippet for CLAUDE.md / AGENTS.md

Paste this into the repo's agent instructions.

```markdown
## Coupling check

This repo runs `connascence check --staged` as a pre-commit hook. It fails when a change adds or grows
coupling that makes code expensive to change.

When it fails, follow the connascence-review skill:
- Read each finding's cause, suggested fix, change probe and the futures that touch it (.connascence/futures.json).
- If a likely change would force edits in several of the listed places, refactor. Keep the fix small and readable.
- If not, accept it: `connascence decide <id> accept --reason "<change considered> and why it isn't hurt"`.
- Log refactors too: `connascence decide <id> refactor --reason "..." --future <id>`.
- Never accept just to pass the gate. Don't fix unrelated baseline findings unless asked.

If .connascence/futures.json is missing or stale, run the connascence-futures-explore skill first
(or connascence-futures-interview if the owner is available).
```
