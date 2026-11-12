---
name: connascence-futures-interview
description: Interview the product owner about which futures are plausible for their software, then generate focused likely changes and stable areas into .connascence/futures.json. Use when the owner is available and wants the connascence review grounded in their real roadmap.
---

# Interview for likely futures

Goal: replace guesswork with the owner's view of where the product is going, then expand their answers into concrete, predictable changes tied to code. The connascence tool uses the result to decide which coupling matters.

Keep the interview short: four rounds, one to four questions each. Use a multiple-choice question tool if you have one (the owner can always type their own answer). Offer a sensible option for every question so the owner can answer fast.

## 0. Prepare (before asking anything)

Do a quick scan so your questions are specific to this product, not generic:

- Read the README or spec and the top-level code layout.
- Note where it sits on each growth axis in the explore skill (connascence-futures-explore, section 2): interaction style, data store, users, team, tenancy, scale, geography, integrations, interfaces, business rules, operations, compliance, commercial.
- If `.connascence/futures.json` exists, read it. You are refining it, not starting over.

## 1. Context

Ask:

- What stage is it at? (prototype, MVP with real users, established product, legacy in maintenance)
- What horizon matters for design decisions? (next 3 months, next year, 2+ years)
- Who uses it now, and who will in a year? (just me, my team, the company, outside customers)
- How many people will work on the code? (just me, a small team, several teams)

## 2. Which axes are plausible

Show only the axes that apply, pre-filled with what you found ("Today: one Postgres database. Next step might be: database-agnostic or a second store"). Ask the owner to mark each: **likely**, **maybe**, **no**. Always add: "Anything on your roadmap I haven't listed?" An axis marked **no** gets no future, not a low one, and an existing future the owner rules out is removed (check the decision log first; if a decision cites it, mark it low and say why).

Good prompts, matched to what you found:

- "It's API calls today. Do you expect an event-driven or queue-based design?"
- "It's tied to one database. Will it need to run on others?"
- "One person edits a record at a time. Will people edit together?"
- "You use it on your own now. Is a team likely to use it?"
- "Is there a second payment, email, auth or AI provider in your future?"
- "Will it need other languages, currencies, timezones or tax rules?"
- "Will it run as more than one instance?"

## 3. Specifics for the likely ones

For each axis marked likely (and the important maybes), ask one follow-up that shapes the change: which provider, which regions, how soon, which part first. Two or three questions in total is plenty. Don't interrogate.

## 4. What won't change

Ask: "Which parts do you expect to stay put?" Offer what you found: generated code, fixed external formats, settled legacy modules, regulatory files. These become `stable` areas. They are as useful as the futures: they tell the review where high coupling is fine.

## 5. Generate the futures

Turn the answers into futures:

1. **Owner-stated futures** (`"source": "interview"`): one per likely axis or roadmap item. Likelihood: likely → high, maybe → medium, "not sure" → low (and say so in the description). Use only `high`, `medium` or `low`.
2. **Derived futures** (`"source": "derived"`): the predictable sub-changes that come with each one. Examples:
   - Team use → accounts and roles, invitations, permissions checks, audit trail, per-user settings.
   - Multi-instance → no in-memory state, shared cache, idempotent background jobs, sticky-session removal.
   - Event-driven → message contracts, retries, ordering and duplicate handling, outbox.
   - Database-agnostic → repository boundary, no vendor SQL, migrations per provider.
   - Collaborative editing → concurrency tokens, merge or locking, change feed, presence.
   - Second provider → provider interface, per-provider config, mapping of errors and statuses.
   - More locales → formatting, money, dates and text pulled out of business logic.
   Give derived futures the same or one step lower likelihood than their parent, and mention the parent id in the description.
3. Tie each to code with `areas` (path globs) and `keywords`, as in the explore skill, section 5.
4. Record stable areas with the owner's reason.

## 6. Confirm and write

Show the owner a compact list: id, title, likelihood, horizon, and the code it touches. Ask for corrections once. Write what they confirmed; if you change anything after that (even narrowing areas), tell them what changed and why. Then write `.connascence/futures.json` (schema in the explore skill) and save the interview notes, in the owner's words, to `.connascence/futures-interview.md` with today's date.

Keep existing ids where a future is unchanged so past accept and refactor decisions still point at the right one.

## 7. Hand off

Run `connascence futures` to show which futures touch the costliest findings. Suggest running the review skill (connascence-review) on those first.
