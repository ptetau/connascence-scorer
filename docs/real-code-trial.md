# Real-code trial: dotnet/eShop

Run on 2026-11-11 against [dotnet/eShop](https://github.com/dotnet/eShop) at its latest commit: Microsoft's
reference shop (catalog, basket, ordering, identity, web and mobile clients). The Tally fixture is too small to
show how noisy the tool is at scale or whether its weights hold up, so this checks both.

## Reproduce

```sh
git clone --filter=blob:none https://github.com/dotnet/eShop.git
connascence analyze eShop --format markdown --top 40
connascence calibrate eShop --months 24
```

## Speed and coverage

11,000 code lines in 490 files analysed in about 6 seconds, with no compiler warnings from the tool.

## What it found

The top findings are mostly real:

- **Claim names** (`"address_street"`, `"card_number"`, `"sub"` and others) written out in both the identity
  server and the mobile client. If one side renames a claim, the other breaks at run time.
- **Copied service code**: `CatalogService` in the web components and the hybrid app; the extension methods in
  `WebApp` and `WebhookClient`.
- **`"api-version"`** written out in 14 places across projects.
- **`AddOrderItem`** called with six positional arguments from two command handlers.

## Noise, and what changed

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

## Calibration

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
