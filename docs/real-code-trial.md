# Real-code trials

Two real codebases: dotnet/eShop (small, slow-moving) and bitwarden/server (large, busy).

## dotnet/eShop

Run on 2026-11-11 against [dotnet/eShop](https://github.com/dotnet/eShop) at its latest commit: Microsoft's
reference shop (catalog, basket, ordering, identity, web and mobile clients). The Tally fixture is too small to
show how noisy the tool is at scale or whether its weights hold up, so this checks both.

### Reproduce

```sh
git clone --filter=blob:none https://github.com/dotnet/eShop.git
connascence analyze eShop --format markdown --top 40
connascence calibrate eShop --months 24
```

### Speed and coverage

11,000 code lines in 490 files analysed in about 6 seconds, with no compiler warnings from the tool.

### What it found

The top findings are mostly real:

- **Claim names** (`"address_street"`, `"card_number"`, `"sub"` and others) written out in both the identity
  server and the mobile client. If one side renames a claim, the other breaks at run time.
- **Copied service code**: `CatalogService` in the web components and the hybrid app; the extension methods in
  `WebApp` and `WebhookClient`.
- **`"api-version"`** written out in 14 places across projects.
- **`AddOrderItem`** called with six positional arguments from two command handlers.

### Noise, and what changed

The first run gave 156 findings. Three kinds were noise, and are now fixed (see the `real-code-noise` scenario):

| Noise | Example | Fix | Findings removed |
|---|---|---|---|
| Statics set once and only read, reported as shared mutable state | `public static int MockCatalogItemId01 = 1;` | Scalars need a write outside their declaration | 14 |
| Mock services shipped with the app | `Task.Delay(10)` in `BasketMockService` | Mocks, fakes and stubs count as test code | 11 fixed waits, plus mock data |
| Values fixed by the browser | `typeof x === 'function'`, `document.readyState === 'loading'`, `event.key === 'Enter'` | `IgnoredSubjects` setting | 3 |

After the fixes: 122 findings, score 51 (load 68 per 1,000 lines).

Still noisy:

- **UI animation delays** (`Task.Delay(100)` in a toggle button) are deliberate, not waits on another event.
  They're reported as fixed waits. Accept them inline, or tune them out.
- **Seed data**: `UsersSeed.cs` gives about ten magic-value findings (the demo user's city, state, card
  number and so on, each written twice). They're cheap, because each stays inside one file, but they clutter
  the list. Add `"**/*Seed*.cs"` to `exclude` in `.connascence.json` if seed data is rewritten wholesale rather
  than edited.
- **Simulated work**: `Task.Delay(10000)` in three order-status command handlers stands in for real
  processing in the demo. The finding is correct for production code, but here it's deliberate.

### Calibration

Over the last 24 months (45 commits):

- File pairs linked by a finding changed together **9.7%** of the time (3 of 31 pairs). Random pairs: **0.1%**.
  That's a lift of about 69x, so findings do point at code that changes together.
- By kind: meaning 20% of 10 pairs, algorithm 20% of 5, position 0% of 12, execution 0% of 4.

The sample is small. eShop changes slowly, and 31 linked pairs is too few to tune the weights from. The
position and execution results (0% co-change) are a hint, not proof, that those kinds are weighted too high. A
repository with a few hundred commits a year would settle it.

The pairs that changed together most often with no finding linking them were the shared OpenAPI setup code and
each service's `Program.cs`. That is wiring by convention (registration and configuration), which the detectors
don't model.

## bitwarden/server

Run on 2026-11-12 against [bitwarden/server](https://github.com/bitwarden/server): the password manager's
backend, 1,774 commits in the last year. Enough history to test the weights.

```sh
git clone --filter=blob:none https://github.com/bitwarden/server.git
connascence analyze server --no-git --format json --all --top 100000 --out bw.json
connascence calibrate server --months 12
```

### Speed and noise

173,000 code lines (after exclusions) in about 30 seconds, about 65 seconds for `calibrate` over 1,214 commits.

The first run gave 4,001 findings and a score of 0. About 2,600 of them were copied blocks between dated SQL
migration scripts in `util/Migrator/DbScripts`: append-only history that never changes together. Migration
scripts are now excluded by default (dated `.sql`, Flyway `V3__x.sql`, Rails `db/migrate`, `DbScripts`,
lowercase `migrations/`). That leaves **1,396 findings, score 42**.

Most of the remaining copied-block findings (about 340) are in the SQL Server stored procedures in `src/Sql`.
Those are edited code, so the duplication is real, but it is boilerplate a SQL project accepts.

### Calibration

Over 1,214 commits (commits touching more than 50 files left out of the breakdown below):

- File pairs linked by a finding changed together **17%** of the time. Random pairs: **0.2%**. A lift of about
  100x: findings do point at code that changes together.
- Share of touched findings where at least one pair of their files changed together:

| Rule | Findings touched | Co-changed |
|---|---|---|
| CNP02 tuples by position | 11 | 82% |
| CNI01 shared mutable state | 8 | 75% |
| CNP01 positional parameters | 164 | 74% |
| CNM02 boolean flags | 49 | 61% |
| CNA01 copied logic | 94 | 57% |
| GNA01 copied lines (text) | 48 | 52% |
| CNM01 magic values | 100 | 36% |
| CNV02 repeated limits | 11 | 27% |
| CNV01 duplicated constants | 28 | 21% |

- Cross-project findings co-change more than same-project ones (40% of pairs against 23%). Distance points at
  real coupling, not just at neighbouring files that change together anyway, which supports weighting it.

### What this means for the weights

The defaults stay. Every rule sits far above the random rate, so no detector is noise. Co-change counts
how *often* coupled code changes, while strength is meant to measure how bad a *missed* change is. Value
coupling (a Stripe status constant defined four times) changes rarely, but a missed edit breaks billing. If
you'd rather rank by how often findings bite in your repo, lower `Value` and raise `Position` in
`.connascence.json`; this data supports that for bitwarden.

`calibrate`'s "findings at or above the gate" line is misleading: it counts file pairs, and large findings
make many pairs, so costly findings look as if they co-change less. A per-finding measure (as in the table)
is the better read.

The pairs that changed together most with no finding linking them are interfaces and their implementations
(`IUserService` and `UserService`, a repository interface and its EF and Dapper versions). That is coupling
the compiler checks, which the tool deliberately doesn't score.
