# Connascence review

**Score 0/100.** Load 1720 per 1,000 lines. 131 code lines in 4 files. 18 open findings, 1 accepted.

## How to use this

Use this report as a lens, not a verdict. High connascence only costs something when the code has to change.

For each finding:
1. Read the change probe. Imagine the next few features, fixes or policy changes this code is likely to see.
2. Ask: would any plausible change have to touch several of the listed places in step?
3. If yes, refactor. Use the suggestion as a starting point. Aim to make the coupling weaker, or bring the places closer together.
4. If no (the code is stable, generated, a one-off, or the places always change together anyway), accept it and write down why.
   An accepted finding comes back for review if the coupling grows (a new place joins it).

Prefer the fix that is easiest to read. A little repetition that is obvious beats a clever abstraction that hides the rule.

Record each decision so it is logged:

```
connascence decide <id> refactor --reason "<what you changed>" [--future <future id>]
connascence decide <id> accept   --reason "<why no plausible change is hurt>" [--future <future id>]
```

## Likely futures

- **F1 Order status workflow grows** (high, next quarter). Returns and partial shipping add new statuses.
- **F2 Second payment provider** (medium). Stripe alongside the current provider.
- **F3 Multi-instance web tier** (medium). More than one web server behind a load balancer.

## Findings

### Same logic copied 2 times (`cee6e83e99`)

- Rule CNA01, connascence of algorithm. Degree 2, across projects. Cost 108, priority 243. Churn medium (8 commits). In baseline.
- **Why:** The same 9-statement body appears in 2 places across 2 projects (OrderService.Discount and OrdersController.QuoteDiscount). If the rule it encodes changes, every copy must change the same way. The copies also repeat: Repeated limit 500, Magic value 0.05, Repeated limit 1000 and Magic value 0.1. Fixing the copy fixes those too.
- **Suggested fix:** Move the logic into one method, owned by the concept it belongs to, and call it from each place.
- **Change probe:** If this rule changes (a bug fix, a new case, a policy tweak), will every copy be found and fixed? Is the rule likely to change?
- **Futures that touch this:** F3 Multi-instance web tier (medium)
- **Where:**
  - `Shop.Orders/OrderService.cs:40` copy: `public decimal Discount(Order order)`
  - `Shop.Web/OrdersController.cs:45` copy: `public decimal QuoteDiscount(Order order)`

### Shared mutable state OrderCache.Items (`6451e3b13b`)

- Rule CNI01, connascence of identity. Degree 3, across projects. Cost 54, priority 121.5. Churn medium (8 commits). In baseline.
- **Why:** OrderCache.Items is static and can change. It is used in 3 members across 2 types. They all depend on the same single instance and on each other's changes to it.
- **Suggested fix:** Give the state one owner: make it an instance field of one class and pass that object to whoever needs it (dependency injection), or make it immutable. If it must be shared, put every read and write behind one small class.
- **Change probe:** If you ever need two of these (per tenant, per request, per test) or run work in parallel, every place that touches it must change. Is that plausible?
- **Futures that touch this:** F3 Multi-instance web tier (medium)
- **Where:**
  - `Shop.Orders/Order.cs:18` declaration: `public static Dictionary<int, Order> Items = new Dictionary<int, Order>();`
  - `Shop.Orders/OrderService.cs:14` use: `OrderCache.Items[order.Id] = order;`
  - `Shop.Web/OrdersController.cs:21` use: `var order = OrderCache.Items[id];`
  - `Shop.Web/OrdersController.cs:31` use: `OrderCache.Items[order.Id] = order;`

### MaxNameLength defined 2 times (`cbfe09ae66`)

- Rule CNV01, connascence of value. Degree 2, across projects. Cost 32, priority 72. Churn medium (8 commits). In baseline.
- **Why:** MaxNameLength is defined separately in Order (50) and OrderDto (50). They have to stay equal, but nothing links them.
- **Suggested fix:** Define it once, in the type that owns the rule, and reference it from the others. If the rules really are different, give them names that say so.
- **Change probe:** If this value changes, will someone know to change both copies? Is it the kind of value that gets tuned?
- **Futures that touch this:** F3 Multi-instance web tier (medium)
- **Where:**
  - `Shop.Orders/Order.cs:7` definition: `public const int MaxNameLength = 50;`
  - `Shop.Web/OrdersController.cs:10` definition: `public const int MaxNameLength = 50;`

### Repeated format "yyyy-MM-dd" (`f0e6ad33f0`)

- Rule CNA02, connascence of algorithm. Degree 2, across projects. Cost 20, priority 60. Churn medium (8 commits). In baseline.
- **Why:** The format "yyyy-MM-dd" is written out in 2 places across 2 projects. Code that writes and code that reads this format must agree on it exactly.
- **Suggested fix:** Keep the format in one place (a constant, or a small formatter/parser class) and use it from both the writing and reading side.
- **Change probe:** If the format changes (a new locale, an extra field, a stricter pattern), will every copy change at once? A missed one fails at runtime, not compile time.
- **Futures that touch this:** F1 Order status workflow grows (high); F3 Multi-instance web tier (medium)
- **Where:**
  - `Shop.Orders/OrderService.cs:28` use: `Console.WriteLine(DateTime.UtcNow.ToString("yyyy-MM-dd") + " " + invoice.Total);`
  - `Shop.Web/OrdersController.cs:22` use: `if (order.Status == 3) return "Shipped on " + DateTime.UtcNow.ToString("yyyy-MM-dd");`

### PaymentClient.Connect() must be called first (`6bf8725c61`)

- Rule CNE01, connascence of execution. Degree 2, across projects. Cost 24, priority 54. Churn medium (8 commits). In baseline.
- **Why:** Callers must call Connect() before Charge(). 2 callers in other classes each have to remember this. The class only finds out at runtime (it tracks a _connected flag).
- **Suggested fix:** Make the wrong order impossible: do the setup in the constructor or a static factory (for example `PaymentClient.CreateConnectedAsync()`), or have Connect() return a different type that is the only one with Charge().
- **Change probe:** If setup gains another step, or more callers are added, each one must get the order right by hand. Is this class likely to be used in new places?
- **Futures that touch this:** F2 Second payment provider (medium); F3 Multi-instance web tier (medium)
- **Where:**
  - `Shop.Orders/OrderService.cs:70` setup method: `public void Connect(string endpoint) { _connected = true; }`
  - `Shop.Orders/OrderService.cs:74` runtime guard: `if (!_connected) throw new InvalidOperationException("Call Connect first");`
  - `Shop.Web/OrdersController.cs:33` caller: `client.Connect("https://pay.example");`
  - `Shop.Web/OrdersController.cs:67` caller: `client.Connect("https://pay.example");`

_13 lower-priority findings not shown. Use --top to see more._

## Generic mode

1 files (py) had no language-specific analyser.

Some files were analysed in generic mode. Generic mode reads the code as text. It cannot see types, calls or scopes, so it only finds the obvious cases and its distances are rough (same file or not).

Things to check by hand in these files:
- Values with hidden meaning: status codes, magic strings, true/false arguments. Give them names in one place.
- Long argument lists of similar types passed by position. Use named arguments or a small type.
- Strings split and read by index. Parse them once into a named structure.
- The same format, regex, hash or rule written in more than one place. Keep one copy.
- Methods that must be called in order (init, open, connect). Make the wrong order impossible.
- Fixed sleeps or delays waiting for other work. Wait for the real event.
- Global or module-level state that several functions change. Give it one owner.

## The kinds, weakest to strongest

- **Name:** Places must agree on a name. This is the weakest kind. Rename tools handle it. Nothing to do.
- **Type:** Places must agree on a type. The compiler checks it. Usually fine.
- **Meaning:** Places must agree on what a value means (3 = shipped, "A" = admin, true = draft). Give the value a name in one place: an enum, a constant, or a small type. Then the compiler checks it.
- **Position:** Places must agree on order (argument order, tuple order, field order in a string). Use names instead of positions: a small record, named arguments, or distinct types.
- **Algorithm:** Places must agree on how something is computed or encoded (a hash, a format, a copied rule). Put the algorithm in one place and call it from everywhere.
- **Execution:** Places must do things in a set order (call Open before Send). Make the wrong order impossible: set up in the constructor, or return a new type once set up.
- **Timing:** Places must agree on how long things take. Wait for the real event, not a fixed delay. Keep any timings that remain in one policy.
- **Value:** Several values must change together to stay correct (a limit checked in two places). Keep one source of truth and derive the rest from it.
- **Identity:** Places must share the very same object instance (static or global mutable state). Give the state one owner and pass it in, or make it immutable.
