---
name: connascence-futures-explore
description: Brainstorm the changes a codebase is likely to face, from its spec and code, and write them to .connascence/futures.json so the connascence tool can rank coupling by real risk. Use when there is no futures file, it is stale, or the owner isn't available to interview.
---

# Explore likely futures

Goal: a short, realistic list of changes this software is likely to need, each tied to the code it would touch. The connascence tool uses the list to rank findings: coupling only costs money when code has to change.

This is judgement work. Prefer boring, predictable changes over clever ones. A future that would surprise the owner is worth less than one they'd nod at.

## 1. Read what the software is today

Read, in this order, and stop when you have a clear picture:

1. The spec: README, docs/, specs/, ADRs, product briefs, issue templates, TODO/FIXME comments.
2. The shape of the code: projects and folders, entry points (controllers, endpoints, CLI commands, handlers), data access, external clients (HTTP, SDKs, queues), config files.
3. What changes often: `git log --since="6 months ago" --name-only --pretty=format: | sort | uniq -c | sort -rn | head -30`. Hot files mark where change is already happening.

Write a one-paragraph "today" summary: who uses it, how they reach it, where data lives, what it talks to, how it is deployed, how many people seem to work on it. State any assumption you make.

## 2. Walk the growth axes

For each axis, note where the product sits now and what the next plausible step is. Skip axes that clearly don't apply.

| Axis | Typical next step | Code it tends to hit |
|---|---|---|
| Interaction style | Request/response API → events, queues, webhooks; polling → push/real-time | Service calls, handlers, anything that calls another component directly |
| Data store | One database → database-agnostic, a second store, read replicas, search index | Repositories, raw SQL, ORM contexts, migrations |
| Data shape | No history → audit log, versioning, soft delete, undo | Entities, save/update paths |
| Users | Single user → multi-user; single editor → concurrent or collaborative editing | Domain state, locking, saves, caches |
| Team | Individual tool → team tool: accounts, roles, permissions, sharing, invitations, audit | Auth, anywhere that assumes "the user" |
| Tenancy | Single tenant → multi-tenant | Statics, caches, config, queries without a tenant filter |
| Scale | One instance → several behind a load balancer; small data → paging, streaming, batching | In-memory state, static caches, file storage, loops over all rows |
| Geography | One language, currency, timezone, tax regime → many | Formatting, money, dates, validation rules, text |
| Integrations | One provider → several (payments, email, auth, AI model); hard-coded → pluggable | Client classes, config, retry and error handling |
| Interfaces | UI only → public API → SDK/CLI; web → mobile or offline | Controllers, DTOs, validation, versioning |
| Business rules | Hard-coded → configurable per customer; one workflow → several (statuses grow) | Status codes, switch statements, rule methods |
| Operations | Manual → scheduled or automated; add observability, feature flags | Startup, background jobs, timing code |
| Compliance and security | Add data retention, encryption, SSO, MFA, consent | Personal data fields, auth, logging |
| Commercial | Free → plans, limits, billing, usage metering | Feature checks, quotas, counters |

## 3. Add the predictable MVP-to-mature features

Most products of a given type grow the same way. Pick the list that fits and keep the ones that this product doesn't have yet:

- **Any app with users:** search, filter, sort, paging; import and export; notifications; settings; admin screens; audit trail; bulk actions; undo.
- **SaaS:** organisations and roles, invitations, billing and plans, usage limits, SSO, public API with keys, webhooks, data export.
- **API service:** versioning, rate limits, idempotency keys, pagination, async jobs for slow work, client SDKs.
- **Internal tool:** more teams using it, permissions, reporting, integration with the company's other systems.
- **Data pipeline:** more sources, schema changes upstream, backfills, late data, reprocessing.
- **Library or CLI:** new platforms, config files, plugins, backwards compatibility.
- **Accounting or finance:** more tax regimes, currencies, reporting periods, rounding rules, audit, regulatory format changes.

## 4. Rate each future

- **high:** the spec or roadmap mentions it, there are TODOs for it, the hot files point at it, or it's the standard next step for this product at this stage.
- **medium:** common for this kind of product, nothing against it.
- **low:** plausible, but no signals. Keep few of these.

Give a horizon when you can ("next quarter", "within a year", "1-2 years").

## 5. Tie each future to code

Each future needs `areas` (path globs from the repo root) and/or `keywords` (type names, concept words) that pick out the code it would touch. Be concrete. "Second payment provider" → `["src/**/Payments/**"]` and `["PaymentClient", "Charge"]`. A future that matches nothing can't inform the review, unless it is genuinely all new code (say so in its description).

Also list **stable** areas you are confident won't change: generated code, a fixed external file format, settled legacy modules. Findings that sit entirely in a stable area and touch no future are ranked lower. Don't mark an area stable if one of your futures expects part of it to change: narrow the stable entry to the part that won't (for example `"keywords": ["DirectCreditLine"]` instead of the whole file).

## 6. Write the file

Write `.connascence/futures.json`. Keep 5 to 15 futures. Mark each `"source": "explore"`.

```json
{
  "product": "Shop",
  "updated": "2026-11-12T00:00:00Z",
  "futures": [
    {
      "id": "F1",
      "title": "Order statuses grow (returns, partial shipping)",
      "description": "Returns are on the roadmap. New statuses and transitions will be added.",
      "likelihood": "high",
      "horizon": "next quarter",
      "source": "explore",
      "areas": ["src/Orders/**"],
      "keywords": ["Status", "Ship"]
    }
  ],
  "stable": [
    { "reason": "Bank file format is fixed by the bank's spec", "areas": ["src/BankExport/**"] }
  ]
}
```

If a futures file exists, merge: keep `interview` and `derived` futures as they are, update your own `explore` ones, and keep ids stable so past decisions still point at the right future.

## 7. Check it

Run `connascence futures`. Every future should touch at least some code or say why not. Look at which futures touch the costliest findings: that is where the review should start. Show the user the list of futures and the assumptions you made, and suggest the interview skill if they want to sharpen it.
