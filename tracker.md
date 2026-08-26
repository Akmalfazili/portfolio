# Portfolio Tracker — development record

This file is the **reference record of how this application was built**: what shipped, what was
decided and why, what broke and what the breakage taught. It is no longer a session-handoff
document — development is complete apart from one measurement that is deliberately on hold.

Read it before changing behaviour in an area you have not touched before. Most of the sharp edges
in this project are not visible in the code, and every one of them below cost a real debugging
session to find.

**The rule this project ran on, and the reason it holds up:** *tick a box only for something you
have personally seen pass, and say plainly what you did not verify.* That flag is what caught the
real defects in Phases 4, 5, 6, 7 and 9. Confident arithmetic was disproved by measurement **five**
separate times (D4/D13's cost estimates, D38's "8 req/min", D39's per-call cost model, D6's
daily-vs-per-minute error, D36's "that state is unreachable"). Measure before you conclude.

---

## Status

| # | Phase | Status |
|---|---|---|
| 0 | Repo skeleton + agent definitions | ✅ Done |
| 1 | Backend scaffold | ✅ Done — verified |
| 2 | Domain + EF Core | ✅ Done — verified against live SQL Server |
| 3 | Transactions CRUD API | ✅ Done — verified |
| 4 | Market data providers | ✅ Done — verified against the real provider APIs |
| 5 | Auto-refresh + SignalR | ✅ Done — verified with a real SignalR client |
| 6 | Portfolio calculations | ✅ Done — hand-checked against real backfilled data |
| 7 | Angular scaffold + shell | ✅ Done — verified over a real WebSocket |
| 8 | Transactions UI | ✅ Done — verified against a live API |
| 9 | Overview + detail pages | ✅ Done — browser-verified |
| 10 | Docker stack | ✅ Done — real four-service stack run end to end |
| 12 | Asset management + theme toggle | ✅ Done — browser-verified |
| 11 | End-to-end verification | 🟨 **5 of 6 — the last box is D6, on hold** |

**Defects D1–D39 are all closed except D6**, which is the measurement described below rather
than a fault. The register is kept as history, not as a to-do list.

**Test suites, re-run 2026-08-26:** backend `dotnet test portfolio.slnx` **227/227** (219 unit +
8 integration), frontend `ng test` **211/211** across 29 files. Backend builds clean under
`TreatWarningsAsErrors`; the Angular build carries one accepted bundle-budget warning (~10 kB over
the 500 kB initial budget, from the chart libraries).

---

## ⏸ The one thing outstanding — D6, on hold

**D6 is not a defect. It is a measurement**, and the user has put it on hold (2026-08-25). Nothing
in the application is known to be wrong; what is missing is one number.

**What it would prove:** that a full NYSE trading day of the live 5-minute refresh cadence lands
well under the Twelve Data free tier's 800 credits/day. Steady state *projects* to ~650/day at 21
symbols — but projection is exactly what D6 exists to replace, and this project's arithmetic has
been wrong before.

**Why it was never taken:** for months the stack only ever ran between SGX close and NYSE open, so
the market gate meant the stock path never executed. That blindness is what hid **D38** until
2026-08-21, the first time the stack was up during a live NYSE window.

**A measurement was started and left in flight.** Baseline `daily_usage` = **24/800** at 2026-08-25
06:11:51 UTC, cross-checked against `GET /api/prices/status` reading 23 — a divergence of exactly
1, which is the `/api_usage` call paying for itself, and confirms D39 has not regressed.

**A live NYSE window opened on 2026-08-25 and was left running.** State captured at 13:52 UTC,
19 minutes after the 13:30 UTC open:

| Check | Reading |
|---|---|
| NYSE gate | `nyseOpen: true` |
| Stack uptime | up since **~13:37 UTC** — a restart **7 minutes after the open** |
| Container freshness | images built 2026-08-24 09:21 UTC; all four commits since touched only `tracker.md` / `CLAUDE.md` → **containers hold HEAD's application code** |
| `creditsUsedToday` | **62/800** (free read; cost nothing) |
| Effective TD interval | 720 s, derived at runtime |

**The 7-minute gap at the head of the window is a declared contamination, not a clean run.** It is
small enough that the day's total is still informative, but the figure must be reported *with* the
gap stated — a partial window reported as a full one is precisely the failure mode this file's
opening rule exists to prevent.

**If it is ever picked up again**, the whole procedure is:

1. Confirm the stack has been **continuously up** across the session (`docker compose ps`). A
   restart gap means the refresh may not have run for the whole window — that is exactly the
   contamination D6 exists to avoid, and it is what spoiled every earlier attempt.
2. Confirm the containers are **not stale** — see the trap note under *Traps* below.
3. Read `GET /api_usage` **once**, after NYSE closes (20:00 UTC) and before the UTC day rolls.
   ~23:00 UTC = 07:00 SGT is the comfortable slot. **It costs 1 credit. Do not poll it.** Twelve
   Data's own counter is *assumed* to roll at UTC midnight; that has never been verified here, so
   do not cut it fine against 24:00 UTC.
4. Cross-check against `GET /api/prices/status` → `creditsUsedToday`, which is free by design and
   never spends a credit. D39's measured residual is **3**, not 200 — treat ~3 as healthy and
   anything near 200 as a D39 regression.
5. Report the figure against 800. **Do not close it on arithmetic.**

---

## Architecture as built

Dependencies point inward; `Portfolio.Domain` references nothing.

```
src/Portfolio.Domain/          Asset, Transaction, PriceQuote, PriceHistory, FxRate,
                               RefreshRun, SourceRefreshState, TwelveDataCreditLedger
                               + AssetClass, TransactionType, QuoteProviderKind, RefreshTrigger
src/Portfolio.Application/     services, DTOs, calculators, provider interfaces, background services
src/Portfolio.Infrastructure/  PortfolioDbContext + 5 migrations, provider clients
src/Portfolio.Api/             4 endpoint groups, PricesHub, DI wiring
src/Portfolio.Web/             Angular 22 workspace
docker/db-init/                Portfolio.DbInit — the one-shot migration runner (NOT in the .slnx)
tests/Portfolio.UnitTests/     215 tests
tests/Portfolio.IntegrationTests/  7 tests — the only place decimal precision is genuinely proven
```

**Endpoints**

| Route | Notes |
|---|---|
| `GET/POST /api/assets`, `GET/PUT/DELETE /api/assets/{id}` | `PUT` is full-replace including `IsActive` — there is no separate deactivate route. `DELETE` is a hard, cascading delete: `204`, or `404` for an unknown id |
| `GET/POST /api/transactions`, `PUT/DELETE /api/transactions/{id}` | filters: `assetClass`, `assetId` |
| `GET /api/portfolio/{assetClass}/summary`, `/allocation` | both asset classes |
| `GET /api/portfolio/stock/annual-returns` | stocks only |
| `GET /api/assets/{id}/performance` | stocks only — a crypto id returns a clean `400` |
| `GET /api/prices/status` | free by design: **never** spends a Twelve Data credit |
| `POST /api/prices/refresh` | 30 s cooldown → `429` + `secondsRemaining`; a large paced batch is queued onto a background task and returns promptly |
| `POST /api/prices/backfill` | bounded; detached onto a background task (HTTP 202) so an nginx 504 cannot cancel it |
| `/hubs/prices` | SignalR: `QuoteUpdated`, `RefreshStatus`, plus a status snapshot on connect |

**Key services** — `PriceRefreshService` (+ background service), `PriceBackfillService` (+ daily
background service), `PortfolioSummaryService`, `PortfolioPerformanceService`, `AverageCostCalculator`,
`AnnualReturnCalculator`, `PerformanceSeriesBuilder`, `TwelveDataCreditThrottle` / `…CreditPolicy` /
`…CadenceCalculator`, `PriceRefreshStatusStore`, `QuoteProviderRouter`.

**Migrations** (one set, applied identically to SQLEXPRESS and to the container DB):
`InitialCreate` → `AddAssetQuoteProviderKind` → `AddSourceRefreshState` → `AddAssetCreatedAt` →
`AddTwelveDataCreditLedger`.

---

## Decisions that shaped the code

Each of these is load-bearing. Where one was reversed, the reversal is stated rather than the
history erased — a decision that looks arbitrary is usually one whose reason was lost.

### Scope and product

- **No auth, single user. Reporting currency is USD.**
- **Cost basis is average cost**, behind `ICostBasisCalculator` so FIFO can drop in later.
- **Annual performance is time-weighted return**, so deposits are not counted as gains.
- **Crypto is gain/loss only — no time series** (decided 2026-07-26, after CoinGecko's 365-day cap
  surfaced). Crypto gets cost basis, market value, absolute and percentage gain, and its slice of
  the allocation pie. It gets **no** cost-vs-market line chart and **no** annual-return chart — the
  components are not imported into the crypto path at all, rather than rendering empty.
- **No crypto price history is stored, and this is accepted as a one-way door for the past.**
  `PriceBackfillService` is the only writer of `PriceHistory` and it filters to `AssetClass.Stock`;
  the refresh service only upserts the live `PriceQuote`. Persisting a daily close was offered and
  **declined**. Days that pass unrecorded cannot be bought back from CoinGecko's free tier. If
  crypto charts are ever wanted they start from the day history-keeping is switched on. Stocks are
  unaffected — their past stays backfillable on demand.
- **Stocks and crypto never aggregate.** `AssetClass` is filtered on in every portfolio-level query.

### Market data and providers

- **Twelve Data's free tier cannot serve Z74, and never could.** The original planning decision that
  it was "the only free source covering both US and SGX" was wrong: `symbol=Z74&exchange=SGX`
  returns *"This symbol is available starting with the Pro or Venture plan"*. The symbol **is** in
  their catalogue (SGX, MIC `XSES`, SGD) — it is a plan gate, not a bad string. Twelve Data is
  **US equities + USD/SGD FX only**.
- **Z74 comes from Yahoo** (`query1.finance.yahoo.com/v8/finance/chart/Z74.SI`), chosen over manual
  entry or a paid plan. Free, keyless, serves quote *and* daily history in SGD. It is **unofficial,
  undocumented and has no SLA**, so it sits behind the same `IQuoteProvider` seam and a failure
  degrades to the last stored quote rather than taking down a multi-asset refresh. **It requires a
  browser-like `User-Agent` or it 403s.** Its raw JSON carries float noise
  (`4.440000057220459`), rounded to 6 dp **for Yahoo only** — never on the crypto path, where
  sub-cent precision is the entire point.
- **`Asset.QuoteProviderKind` is the provider dispatch key, not `AssetClass`** — stocks span two
  providers. Routing is never inferred from currency, exchange or symbol shape.
- **CoinGecko runs keyless.** The `x-cg-demo-api-key` header is *ignored* on `api.coingecko.com`.
  Setting `CoinGecko:ApiKey` switches the client to `pro-api.coingecko.com` + header. Configured-vs-
  absent is decided by **`CoinGeckoOptions.HasApiKey`** (`!string.IsNullOrWhiteSpace`), never by a
  bare null check — see D32 for why that distinction is not pedantry.
- **CoinGecko keyless caps history at 365 days**, returning HTTP **401** with `error_code 10012`.
  A free Demo key **probably does not lift it** (their 401 body calls keyless *and* Demo the "Public
  API"; historical depth is listed only for paid plans) — **unverified**, so do not plan around it
  without testing.
- **Twelve Data's JSON is internally inconsistent.** `/quote` returns numbers as *strings*
  (`"close":"333.019989"`), `/exchange_rate` as a bare *number*; a single-symbol `/quote` is flat
  while a batch is keyed by symbol; `/time_series` comes back **descending** by date; and errors
  arrive as **HTTP 200** with `{"status":"error"}` bodies, including a per-symbol failure nested
  inside an otherwise-successful batch. `IsSuccessStatusCode` alone will hand you garbage.
- **Rate limits: 800 credits/day, and the per-minute ceiling is 8 _credits_, not 8 requests.** Each
  symbol in a batch costs a credit, so one request carrying more than 8 symbols 429s on its own
  even as the only request in its minute. Everything goes through `ITwelveDataCreditThrottle`, in
  chunks of at most 8. `TwelveDataCadenceCalculator` derives the open-market interval at runtime
  from the live active-symbol count and the remaining daily budget, so the cadence widens
  automatically past ~21 symbols. Yahoo and CoinGecko are unmetered — **Z74 costs zero Twelve Data
  credits**, which matters every time credit arithmetic is done.
- **Twelve Data deliberately does not retry a 429**, so a rate-limit cannot burn more credits.
- **The credit ledger reconciles against Twelve Data's own counter rather than trusting itself.**
  A new UTC day's row is seeded from `GET /api_usage` instead of from zero, and an existing row is
  reconciled hourly. Reconciliation is **periodic, never per call** — `/api_usage` costs a credit
  itself, so credit monitoring cannot be a tight loop. `GetStatusAsync` was deliberately left out of
  it: polling `/api/prices/status` can never cost a credit.
- **Manual refresh respects the market gate.** `POST /api/prices/refresh` bypasses the *interval*,
  not the *calendar*; only crypto is unconditional. Clicking refresh at 3 am spends nothing.
- **A manual refresh always records a `RefreshRun`, even when it does nothing** — the 30 s cooldown
  is derived from persisted manual runs, so a no-op that wrote no row left the endpoint with no
  cooldown at all. The **scheduled** path still writes nothing in that case, or the 30-second poll
  loop would flood the audit table with thousands of no-op rows a day.
- **Refresh status is persisted in `SourceRefreshState`** (one upserted row per provider, three
  rows forever), because `NextDueAt` **gates the cadence**: losing it on restart made every provider
  due immediately, so a debugging session with a dozen restarts re-spent real credits.
  `LastRefreshedAt` is **derived** from the newest `LastSuccessAt`, not stored, so it cannot drift.

### Data and wire contracts

- **Decimal precision is explicit on every column.** SQL Server's EF Core default `decimal(18,2)`
  silently destroys this domain: ANVL trades near `$0.0005326` and would round to `0.00`.
  Quantities / unit prices / closes `decimal(28,10)`, fees and monetary totals `decimal(19,4)`,
  FX rates `decimal(18,8)`. Verified by querying `INFORMATION_SCHEMA.COLUMNS`, not by reading the
  config. **The unit tests cannot prove this** — the EF Core InMemory provider does not enforce
  precision, so `tests/Portfolio.IntegrationTests` against real SQL Server is the actual tripwire.
  Keep it green.
- **Enums are strings, ids are numbers.** `JsonStringEnumConverter` is registered globally, so
  `AssetClass`/`TransactionType` cross the wire as `"Crypto"`/`"Buy"`. Ids are C# `int` and the API
  registers no `AllowReadingFromString`, so `POST`ing `"assetId": "3"` is **rejected with a 400**,
  not coerced. It is easy to get exactly backwards.
- **SignalR does _not_ inherit `ConfigureHttpJsonOptions`.** It has its own protocol serializer, so
  the global enum converter did not reach the hub: `QuoteProviderKind` crossed as `"source":0` while
  REST sent `"source":"TwelveData"` — one field, two encodings, in payloads the frontend has to
  merge. Fixed with `AddSignalR().AddJsonProtocol(...JsonStringEnumConverter...)` and guarded by
  `PricesHubProtocolTests`, which resolves the real `IHubProtocol` from DI and was confirmed to fail
  when the fix is reverted. **Any future hub payload carrying an enum depends on that one line.**
- **`tradeDate` is a `DateOnly`, serialised `YYYY-MM-DD`.** Never round-trip it through
  `toISOString()` — at SGT (UTC+8) an evening entry lands on the previous day.
- **`DisplayRounding` rounds only at the DTO boundary** — money 4 dp, price 10 dp, percent 4 dp —
  and never inside a calculator. Added after FX-chained divisions emitted 20+ decimal digits on the
  wire. Internal arithmetic stays unrounded so rounding happens once, at the edge.
- **Exponent notation on the wire is fine.** JavaScript serialises `0.0000000001` as `1e-10`;
  `System.Text.Json` binds it to `decimal` losslessly (verified twice, `201` then read back exact).
  String-serialising decimals was proposed as a fix for a **non-problem** and is not needed.
- **The ~15-significant-digit limit is real and the loss is entirely client-side.** A raw
  `12345678.1234567891` sent by `curl` round-trips exactly; `JSON.stringify` on the same value emits
  `12345678.12345679`, so the digits are gone before the request leaves the browser. The fix
  therefore lives in the form — `decimalPrecisionValidator` caps at 15 significant digits and fails
  loudly at the input. That cap is **deliberately conservative**: between 15 and ~17 digits survival
  is magnitude-dependent (`1000000.0000000001` is fine, `999999999.9999999999` collapses), and a
  magnitude-aware rule would be unreasonable at the call site. If a legitimate holding ever trips
  it, widen it deliberately — do not remove it.
- **`AssetClass` route and query binding is case-insensitive** via `AssetClassRouteValue`
  (`IParsable`), applied to the route segment *and* the query parameter together (D11).
- **`IPortfolioDbContext`** in `Application/Abstractions` exposes `IQueryable<T>` plus save/find, so
  services stay testable and free of the SQL Server provider — at the cost of `Portfolio.Application`
  taking a package reference on provider-agnostic `Microsoft.EntityFrameworkCore`. Same deliberate
  trade as the `Microsoft.Extensions.Hosting.Abstractions` reference the background services need.
- **The solution file is `portfolio.slnx`** (.NET 10's XML format). `dotnet build` / `dotnet test`
  with no argument will not find it — always pass it explicitly.
- **FluentAssertions is pinned at 8.10.0.** Version 8 moved to a paid licence for commercial use.
  Fine for a personal project; worth a look before this goes near work.

### Calculations

- **Cash flows are attributed to the first valuation on or after their own date, never matched by
  exact date.** Valuations come from stored `PriceHistory` dates, flows from each transaction's
  `TradeDate`, and the two are not guaranteed to align. Exact-date matching silently dropped every
  flow dated a weekend, a holiday, or any day with no stored close — **and a dropped deposit is
  reported as a gain**: a $500 buy between two flat valuations came back as **+50% instead of 0%**.
  Pinned by `AnnualReturnCashFlowAlignmentTests`. A flow dated past the last valuation is correctly
  ignored — no valuation reflects that purchase either.
- **FX carry-forward:** the most recent stored rate at or before the date, falling back to the
  earliest stored rate for dates preceding any. Chosen over throwing, which would blank a whole
  series because one day is missing. **Historical series use the rate for that date, not today's.**
- **Cost basis:** fees capitalised into basis on buys, deducted from proceeds on sells; a full-close
  sell snaps quantity and basis to **exactly** zero rather than leaving a division remainder that
  makes a closed position look infinitesimally open.
- **Annual return:** daily-valuation TWR, `r = (V(t) − CF(t)) / V(t−1) − 1`, geometrically linked
  per calendar year. A sub-period starting from a **zero** valuation contributes no return — there
  is no rate to compute from a zero base. Sub-periods link **across** the year boundary.
- **A held position with no quote reports `currentPriceUsd: null` and `marketValueUsd: 0`**, never
  an error — a freshly seeded asset must not fail a whole summary. The UI renders that as
  "Awaiting price", never the naive −100% the raw numbers would read as, and the summary and
  allocation payloads carry `UnpricedHoldings` / `UnpricedHoldingsCount` so totals are caveated
  rather than quietly wrong.
- **A close is never written into `PriceQuote`.** The two tables keep their distinct meanings and
  the fallback happens at **read time**, carrying `priceSource: "Live" | "Close"` and the close's
  own `priceAsOf` date. Write-time seeding was deliberately rejected: it collapses "live" and "stale
  close" into one field and repeats D4's mistake of making a stale price look current. **Any future
  consumer must treat `priceSource: "Close"` as stale data.**
- **`PriceHistory` grows only for assets that have transactions** — the backfill runs from an
  asset's earliest trade date, so an asset nobody holds is skipped by design. Z74's history stays
  frozen for as long as no transaction references it, even with the daily backfill running.
- **FX gets first claim on the backfill's call budget.** It is a hard prerequisite (one
  unconvertible asset takes down the whole page); price history is degradable. The asset loop runs
  after, in **least-recently-backfilled order** (nulls first) so no asset can be starved twice
  running.

### Frontend

- **`src/styles/ui.tokens.scss` is the single source of design truth.** New UI *reads* tokens; it
  does not introduce values. **Angular Material is derived from it, not configured beside it** —
  `mat.theme()` contributes only the M3 structural scaffold, then a `--mat-sys-*` block repoints
  every colour Material paints at the `--ui-*` tokens. Without that block there would be two
  palettes drifting apart. The tree is grepped clean of hex colours and raw `px` outside the token
  files (the `1px` hairline-border exception aside). **One deliberate exception:** breakpoints are
  SCSS variables in `ui.mixins.scss`, because `@media` cannot read a custom property.
- **The section accent is a token swap, not a forked stylesheet** — `data-section="stock"|"crypto"`
  on `<html>` re-aliases `--ui-color-accent`, and Material follows because `--mat-sys-primary`
  points at it.
- **The frontend does no FX math.** The backend has already converted; a row renders the converted
  USD price, never the native one.
- **The hub is the only source of live prices.** There is no "current quotes" REST endpoint, so the
  60-second polling fallback keeps refresh *status* alive but **cannot** keep per-asset prices
  current — `PriceStore.connectionState` exposes `'polling-fallback'` so the UI says so. If live
  prices must survive a dropped socket, the backend needs a `GET /api/prices`.
- **Never derive "which market was skipped" from `PriceRefreshCycleResult.sources`** — a gated
  provider is omitted from that list entirely (D10). The closed-market set comes from the status
  snapshot's `nyseOpen`/`sgxOpen` flags. `attempted` remains reliable for exactly one thing:
  telling a provider that was called and failed from one that was never called.
- **"Follow OS" means _removing_ the `data-theme` attribute and the `localStorage` key**, not
  writing the currently-resolved value. Pinning the resolved value silently stops following the OS
  at the next system change while still claiming to.
- **Angular's production `inlineCritical` is OFF, deliberately.** Its critical-CSS extraction knows
  nothing about `data-theme` and inlines only the plain `:root` (light) rules, dropping both the
  dark `@media` block and the `[data-theme='dark']` override — so first paint is always light
  whatever the preference, defeating the anti-flash script in `index.html`. **Do not turn it back
  on to shave bytes without re-testing the dark-mode first paint.**
- **jsdom needs the stubbed 2D canvas context in `test-setup.ts`.** ECharts measures text through
  `getContext('2d')` **even with `renderer: 'svg'`**, so without it the whole vitest suite gets
  slower and flakier — and the symptom surfaces in a completely unrelated spec (D22). Do not delete
  the stub because "no test uses canvas".
- **Gains and losses are distinguishable without colour** — an explicit `+`/`-`, an arrow glyph with
  an `aria-label`, *and* the colour token, so a grayscale render or a screen reader gets the same
  answer.

### Containers

- **Use the `*-noble-chiseled-extra` base images, never the plain `*-noble-chiseled` ones.** The
  plain tags set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true`, under which `Microsoft.Data.SqlClient`
  **cannot open a SQL Server connection at all** — every attempt throws
  `System.NotSupportedException: Globalization Invariant Mode is not supported.` before a query runs.
  Found live as `migrate` exiting `139` on the very first `up`. Same reason
  `<InvariantGlobalization>true</InvariantGlobalization>` was removed from `Portfolio.DbInit.csproj`.
- **Migrations run in a one-shot `migrate` service**, not from the API at startup. It runs
  `docker/db-init/Portfolio.DbInit` (references `Portfolio.Infrastructure` only, **zero changes
  under `src/`**, not part of the `.slnx`), calling the same `Database.MigrateAsync()` codepath EF
  always uses — there is only ever one migration set in this repo. `api` gates on
  `migrate: condition: service_completed_successfully`, so it never starts against an unmigrated
  database. Diagnose migration problems with `docker compose logs migrate`, never `logs api` — the
  chiselled API image has no code path that could emit that line.
- **The app connects as `portfolio_app`, never `sa`.** `migrate` is the only container ever handed
  `MSSQL_SA_PASSWORD`; it creates the login and grants `db_datareader` + `db_datawriter`, no DDL.
  Proven from the app's own live connection via `sys.dm_exec_sessions`, and `CREATE TABLE` as that
  login was rejected with `Msg 262 ... permission denied` — the boundary is real, not merely unused.
- **`ASPNETCORE_ENVIRONMENT=Production` is set explicitly in `Dockerfile.api`**, so
  `appsettings.Development.json`'s `Trusted_Connection=True` is never even loaded. Windows Auth
  cannot work from a Linux container — never assume it is available.
- **A compose `.env` cannot omit an empty variable.** `${VAR:+...}`, `${VAR:-}` and the bare-name
  passthrough all still emit `VAR=` into the container once `.env` defines the key at all. That is
  why present-but-empty must be handled in code (the `HasApiKey` pattern), not worked around in YAML.
- **`Dockerfile.web`'s Node base must be 22.22.3 or newer** — Angular 22's CLI hard-refuses anything
  older, so a floating `node:22-alpine` tag is only safe while it happens to resolve above that.
  Pin the patch.
- **`docker compose down` (never `-v`)** preserves the named `mssql-data` volume; verified by
  round-tripping a probe transaction through a full `down`/`up`.

### Asset deletion cascades explicitly, in the service, not through the database (2026-08-26)

`/assets` could create and deactivate but not delete, and an asset row alone is not a meaningful
unit to remove — orphaned transactions would still be summed into portfolio totals with no asset
to attribute them to. `AssetService.DeleteAsync` therefore removes transactions, price history and
the quote itself before removing the asset, in one `SaveChangesAsync`.

Every child is deleted **in code**, and the reason is not style:

- The `Asset → Transaction` foreign key is `DeleteBehavior.Restrict` and stays that way. Nothing
  may take transactions away by accident — only a call that says so in its name. Switching it to
  `Cascade` would have needed a migration *and* would have made every future accidental asset
  delete silently destructive.
- The EF Core InMemory provider the unit tests run on has no foreign keys at all, so it cascades
  only to entities the change tracker already holds. A database-level cascade would have made the
  unit tests pass while proving nothing about SQL Server — the same class of false comfort as the
  decimal-precision tests. `AssetDeleteCascadeTests` in `Portfolio.IntegrationTests` is the real
  tripwire, and it is the test to extend when a new child table appears.

The UI counts the asset's transactions with one extra `GET /api/transactions?assetId=` before it
asks, so the confirmation can say *"Its 3 transactions and all of its price history will be
permanently deleted"*. The row shows symbol, provider and status and nothing about how much
history hangs off it, and the count is the only fact on that screen that would make someone press
Cancel. If that `GET` fails the delete is still offered, with vaguer wording — never with a
fabricated count of zero, which would understate exactly the risk being warned about.

Unlike the deactivate toggle, delete is **pessimistic**: the row is dropped only after the `204`.
Showing a row vanish and then reappear on failure reads as data loss.

The red on both the row's Delete and the confirmation's Delete comes from repointing Material's
**component** tokens (`--mat-button-text-label-text-color`, `--mat-button-filled-container-color`)
to `--ui-color-loss`. The obvious `color="warn"` was tried first and is inert — see trap 10; it
also revealed that `ConfirmDialog`'s `destructive` flag had never rendered red since Phase 12.
`--mat-sys-error` was repointed to `--ui-color-loss` in the same pass, but for `mat-error`
validation text (12 uses across the two form dialogs), which is what actually reads that role —
not for the buttons.

**Verified live**, not from tests: a probe asset with two transactions, one price-history row and
one quote was deleted through the running API, and SQL Server's own `fn_dblog` showed exactly
1 asset + 2 transactions + 1 history + 1 quote removed and nothing else — with the six real assets
and all 1,176 price-history rows untouched. `DELETE` on the now-missing id returns `404`.

---

## Traps — the lessons that cost a session each

These are general, and every one of them was learned the expensive way here.

1. **A healthy running stack proves nothing about which code it holds.** A session found all three
   containers up and green and took them at face value; `portfolio-api` predated its own fix by
   4½ hours and `portfolio-web` was **fifteen days stale**. The first test failed in exactly the way
   a broken fix would, and the fix had simply never been deployed. What identified it was
   **behaviour, not timestamps** — the API returned the *old* validation string while the source
   held the new one. Before trusting a running stack:

   ```bash
   docker image inspect portfolio-api --format '{{.Created}}'   # and portfolio-web
   git log -1 --format=%cI                                      # HEAD's commit time
   ```

   Cheap shortcut: if `git log --name-only` shows every commit since the image build touched only
   `tracker.md` / `CLAUDE.md`, the images still hold HEAD's application code.

2. **Before declaring a state unreachable, query for it.** This file spent two sessions asserting
   that a zero-cost-basis holding "is not reachable without polluting the portfolio". Two such
   holdings already existed and had for months — the claim came from reasoning over the *assets*
   list without checking the *transactions* that produced it.

3. **Re-derive a stale cost estimate before trusting it.** D4 was rated "needs a maintained per-year
   table" and became a read-time classification with no table at all. D13 was parked as needing "a
   real holding period" — closing D12 had already made multi-year history reachable. In both cases
   the prerequisite had been paid off by earlier work and nobody re-checked.

4. **Measure before you fix, especially when you are confident.** D39's leading theory — that
   `/time_series` is priced by output size — was killed by two direct calls returning 5 rows and
   1,668 rows for **1 credit each**. Had the fix been written to the theory, it would have made the
   ledger wrong in the opposite direction.

5. **Watch for the reporting layer telling a cleaner story than reality.** This is the single most
   repeated defect family here: D10, D26, D33, D35 and D38 are all a healthy-looking payload over a
   real failure. A skip list whose *name asserts a reason* (`assetsSkippedForBudget`) is how a
   silent data-staleness bug hides. **Always distinguish "not attempted" from "attempted and
   failed".**

6. **Rendered output catches what tests cannot.** The first browser pass over Phase 9 produced six
   findings, four of them defects no unit test could have caught — including a live price rendering
   at a **1.01:1** contrast ratio. A contrast bug that unit-tests green is exactly what produced D14.

7. **Check the confounds before drawing a conclusion from a browser.** A live-update check ran in a
   tab whose `visibilityState` was `"hidden"` the whole time. That was verified *first* — the
   frontend has no `visibilitychange` handler and the timestamp arrives over a push-only hub, so
   hidden was harmless. A session that skipped the check would have had a result it could not
   defend.

8. **The Chrome extension connection is intermittent and not diagnosable.** Two sessions were lost
   to it; a contention theory was disproved by measurement; the next session connected on the first
   probe with nothing done differently. **Probe twice — if it connects, work; if not, switch to
   non-browser work.** When it does connect, grant site permission for `localhost:8080` and prefer
   **element refs over screenshot coordinates** — the viewport was observed resizing between calls,
   which makes coordinate clicks land in the wrong place.

9. **Never list `dotnet user-secrets` to obtain a key.** A redaction regex failed to match
   PowerShell's `Key = Value` spacing and printed a real API key into a transcript. Read the single
   value you need and pipe it straight into the call, or let the app make the call for you.

10. **Angular Material's `color` input is M2-only and silently does nothing here.** This app themes
    with `mat.theme()`, an M3 theme, where `color="warn"` / `color="primary"` are documented as
    having *no effect* — `button.d.ts` says so outright. `ConfirmDialog`'s destructive confirm had
    carried `[color]="'warn'"` since Phase 12 and had **never once rendered red**; the same mistake
    was repeated on the new assets-page delete button, and every test still passed, because a
    colour that never arrives is invisible to a component test. It was caught by a user looking at
    the screen. The M3 mechanism is to repoint the component's own tokens on a class:

    ```scss
    .my-delete-button {
      --mat-button-text-label-text-color: var(--ui-color-loss);      /* text button   */
      --mat-button-filled-container-color: var(--ui-color-loss);     /* filled button */
    }
    ```

    Note the naming is `--mat-button-<variant>-<property>`; the `--mat-text-button-…` form quoted
    in Material's own `_definition.scss` comment is stale. Do not guess these names — grep the
    built output for what Material actually consumes:

    ```bash
    grep -ho "\-\-mat-button-[a-z-]*" src/Portfolio.Web/dist/Portfolio.Web/browser/*.js | sort -u
    ```

    The give-away is the fallback: Material emits
    `var(--mat-button-text-label-text-color, var(--mat-sys-primary))`, so an unset token renders in
    the **primary accent** — which reads as "styled", not as "broken". The remaining
    `color="primary"` bindings in this app are equally inert but happen to be correct, since
    primary is the M3 default anyway.

---

## Known gaps, deliberately accepted

Not defects — decisions. Each was considered and left as-is.

- **SGX lunar holidays are not modelled.** Fixed-date Gregorian holidays are handled; Chinese New
  Year, Vesak, Hari Raya and Deepavali are not, so the calendar reports SGX open and the refresh
  calls Yahoo anyway. Harmless in credit terms (Yahoo is free and unmetered) and it returns a stale
  close that is now *labelled* stale. A per-year table nobody refreshes rots into the same wrong
  answer it was added to fix. **NYSE holidays are fully rule-based.**
- **A well-formed but wrong provider symbol is still not caught** (D27). `APPL` for `AAPL` creates a
  permanently unpriceable asset. Verifying the symbol at creation costs a credit per asset and needs
  its own failure-mode design (what happens when the provider is simply down?). Mitigated instead:
  a never-priced asset is surfaced in the UI once its provider has demonstrably succeeded for
  others.
- **No screen reader has been near the theme toggle.** Its menu items use `role="menuitemradio"` +
  `aria-checked`, verified by DOM assertion only.
- **D39's residual under a full day of sustained load is unverified.** One seed and one
  reconciliation event were observed live, not a day's worth. The measured residual was 3 credits,
  which is the contract (the verification read itself plus drift since the last ledger write), not
  a leak.
- **Crypto keeps no price history, permanently.** See the decision above — this is a one-way door,
  not an oversight to tidy up.
- **The Twelve Data API key was exposed once in a local transcript and the user chose not to rotate
  it** (asked and answered 2026-08-24). It was never written into a project file, logged from
  application code, or committed. Recorded so it is not re-litigated — **do not raise it again
  unless the user does.** If the key ever starts 429ing or behaving as though someone else is
  spending it, this note is the first thing to reread.

---

## Defect register — D1 to D39

Kept as a record of what broke and why, so it is not rediscovered. **D6 is the only row not closed,
and it is a measurement on hold, not a defect** — see the top of this file.

| # | What was wrong | Resolution |
|---|---|---|
| D1 | Manual cooldown could fail to engage when every source was gated — reachable the moment the coins are deactivated | 2026-07-31 — a manual cycle persists its `RefreshRun` even when nothing ran |
| D2 | Background refresh loop had no automated test | 2026-07-31 — driven by `FakeTimeProvider`, since the loop waits via `Task.Delay(…, TimeProvider, …)` |
| D3 | Refresh status was in-memory and reset on restart | 2026-07-31 — persisted in `SourceRefreshState`. The real cost was not a blank indicator but **re-spent credits**: losing `NextDueAt` made every provider due immediately on every restart |
| D4 | SGX lunar holidays are not modelled, so a stale Yahoo close was stored with a fresh-looking timestamp | 2026-08-08 — closed by making the **data honest**, not by modelling holidays. The calendar is still wrong on lunar days, deliberately; a stale quote can no longer masquerade as current |
| D4a | A pushed SignalR quote bypassed the D4 fix — `isCloseSourced` assumed "a genuine SignalR push is always live", which is false when the provider returns a stale close | 2026-08-08, same change. Fixing the read path alone would have left D4 half-closed on the page where it shows most |
| D5 | Manual refresh is a no-op for stocks outside market hours, with no explanation | 2026-07-31 — frontend messaging |
| **D6** | **The 5/60-minute cadence has never been measured over a real trading day** | **On hold — see the top of this file** |
| D7 | Two independent serializer configurations could drift | 2026-08-08 — `PortfolioJsonSerialization.Apply` is the single definition, applied to both pipelines, and sets the naming policy **explicitly** rather than inheriting it twice |
| D8 | `decimal(28,10)` over JSON was assumed unsafe in a JS client | 2026-08-01 — **premise measured and found wrong.** `1000000.0000000000` is exactly 10⁶ and never was at risk. String serialisation was a fix for a non-problem; the real 15-significant-digit limit lives client-side and is capped in the form |
| D9 | Angular CLI's Node check was patched inside `node_modules` | 2026-07-31 — Node upgraded to 22.23.2, patch and `postinstall` hook deleted, pristine CLI gate confirmed restored |
| D10 | A gated provider vanishes from `PriceRefreshCycleResult.sources` | 2026-08-07 — resolved as a **doc correction, not a payload change**: emitting gated outcomes would make `outcomes.Count == 0` stop meaning "nothing happened", which is what suppresses a `RefreshRun` write and a broadcast on every closed-market tick |
| D11 | `AssetClass` route/query binding was case-sensitive | 2026-08-07 — `AssetClassRouteValue` (`IParsable`) applied to the route segment **and** the query parameter together |
| D12 | `PriceHistory` was never refreshed — the backfill service had **no caller at all**, so annual returns were computed over a frozen window and looked current | 2026-08-07 — `RunIfDueAsync` + `PriceBackfillBackgroundService` + a bounded endpoint. **The single most consequential item in the project.** Proven by watching MSFT go 0 → 14 rows past the frozen window, and idempotent on re-run |
| D13 | Annual returns had only ever run over 5 days of real data | 2026-08-08 — closing D12 had already made multi-year history reachable by backdating a probe |
| D14 | The live price rendered at **1.01:1** contrast in dark mode on every detail page — measurably invisible | 2026-08-07 — two root causes: missing `--mat-sys-background`/`-on-background` overrides, and `body { color-scheme: light }` pinning every `light-dark()` in body's subtree to the light branch. Now 17.42:1, and a full-page sweep finds **0** elements under 3:1 |
| D15 | The cost and market end labels overlapped illegibly when the two series are close — the normal case for a new position | 2026-08-07 — collision detected relative to the visible value range, so it works for a $5 position and a $50k one alike |
| D16 | Charts baked theme colours at construction and never repainted on a theme change | 2026-08-07 — a `themeVersion` signal bumped by a `matchMedia` listener and a `MutationObserver` on `[data-theme]` |
| D17 | Portfolio totals treated a missing quote as $0 market value | 2026-08-07 — D20's fallback removes the *common* case but not a never-priced asset, so the summary DTO gained explicit unpriced-holding fields and the UI caveats the total instead of reading as a crash |
| D18 | Material icon ligature names leaked into the accessible text (`arrow_upward+$218.09`) | 2026-08-07 — `aria-hidden` on the icon inside a `role="img"` wrapper carrying the label |
| D19 | The line chart's x-axis showed day-of-month only, meaningless over the 1Y/All ranges | 2026-08-07 — ticks format by range span |
| D19a | The D19 fix rendered the **entire axis one day early** at any positive UTC offset | Found and fixed in the same pass. `timeZone: 'UTC'` was correct for `new Date("2026-07-20")` — but the series hands raw strings to a `type: 'time'` axis, so **ECharts** parses them, not the native `Date` |
| D20 | No last-close fallback: a stored price sat on disk while the UI said "Awaiting price" | 2026-08-07 — read-time fallback carrying `priceSource` and the close's **own** date. Write-time seeding was rejected because it repeats D4's mistake |
| D21 | Stat-tile values broke mid-number | 2026-08-07 — the real defect was `overflow-wrap: break-word` on a numeric value; the sizing question was then solved with a container query |
| D22 | `transaction-form.dialog.spec.ts` failed intermittently | 2026-08-07 — root cause was **nowhere near the failing test**: ECharts calls `getContext('2d')` on an offscreen canvas to measure text even in SVG mode, and jsdom returns `null` |
| D23 | `POST /api/assets` would happily create an asset that can never be priced | 2026-08-07 — shared `ValidateCore` requires the provider field matching `QuoteProviderKind`, rejecting with a `400` keyed on the offending field and a message that teaches the rule |
| D24 | There was no UI for adding an asset — the six seeded ones were all you got | 2026-08-07 — `/assets` page with create, deactivate and reactivate |
| D25 | No in-app light/dark toggle | 2026-08-07 — three-state toolbar toggle, persisted, with an anti-flash inline script |
| D26 | The backfill reported provider *failures* as budget skips, so an asset could stop advancing behind a clean-looking report | 2026-08-07 — found by **calling the endpoint and reading the payload**, not by reading the code |
| D27 | A well-formed but wrong provider symbol is permanently silent | 2026-08-08 — **mitigated**, not solved; see *Known gaps* |
| D28 | Nothing applied EF Core migrations — the container stack would start against an empty database | 2026-08-09 — the one-shot `migrate` service |
| D29 | The container app would have connected to SQL Server as `sa` | 2026-08-09 — `portfolio_app` least-privilege login, proven from the app's own live session |
| D30 | The allocation pie had no unpriced caveat | 2026-08-08 — carried on the allocation payload itself, not only the summary's, so a caller fetching just that endpoint can still tell the pie is partial |
| D31 | A real OS dark-mode flip could not be confirmed from an automated window | 2026-08-08 — closed **by the user** in a normal focused window; the confound was the whole story |
| D32 | CoinGecko routed to the paid Pro API and 401'd whenever `CoinGecko__ApiKey` was **present-but-empty** — exactly the shape a compose `.env` produces for an unset key | 2026-08-09 — `HasApiKey` applied at **every** call site the pattern appeared, not just the one first reported |
| D33 | A refresh cycle where every symbol in a batch failed still reported `LastRunSuccess: true` | 2026-08-09 — found while verifying D32. `Success` was hardcoded true whenever the HTTP call itself did not throw |
| D34 | D27's escalated "check this identifier" warning fired on a fresh database with no evidence | 2026-08-09 — gate the escalation on the **provider**, not the individual asset: seeded assets share a static `CreatedAt`, and D33's false success made a failing provider look proven |
| D35 | The FX backfill was starved of its one provider call by the asset loop **and reported success anyway** — `FxRates` stayed empty and every stocks endpoint 500'd | 2026-08-10 — user-reported as *"Couldn't load your holdings"*. One unconvertible asset took down the whole page including the 19 holdings needing no FX. FX now has first claim on the budget, and `FxHistoryFetchResult` mirrors the `Success`/`Error` split. **Re-running the backfill could never have fixed it** |
| D36 | A zero-price acquisition could not be entered — free shares, bonus issues and scrip dividends were unrecordable | 2026-08-24 backend + frontend; last browser gaps closed 2026-08-25. `pricePerUnit` now rejects only `< 0`; `quantity` keeps its positive validator |
| D37 | The backfill had no pacing and no ordering, so the same tail was starved every run and advanced **zero days** across two runs | 2026-08-21 — least-recently-backfilled ordering (nulls first) plus the shared throttle, with the run budget derived from remaining daily credits |
| D38 | **The live stock quote call could never succeed.** One `/quote` batch of all 21 symbols spends 21 credits against an **8-credit-per-minute** ceiling, so it 429'd every cycle — measured three times, with zero stock rows ever written. Every price ever seen on `/stocks` was the D20 close fallback | 2026-08-21 — chunking to ≤8 symbols behind the shared credit throttle, a runtime-derived cadence, and a persisted ledger. **All 21 stocks then read `priceSource: Live`**, including five that had never held a price. `CLAUDE.md`'s own "8 req/min" line was the origin of the design error and was corrected |
| D39 | The daily credit ledger under-counted real spend by **200** — the mechanism meant to prevent D38-style overspending was itself miscalibrated | 2026-08-24 — made **self-correcting** rather than chasing each drift source: seed a new day's row from Twelve Data's real counter, reconcile hourly. The ledger only ever counted what *this process* granted, so anything else drifted it low permanently |

---

## Development timeline

| Date | What happened |
|---|---|
| 2026-07-26 | Repo skeleton and agent definitions; backend Phases 1–4 — domain, EF Core with verified column precision, CRUD API, and all three market-data providers against their real APIs. Twelve Data's inability to serve Z74 discovered here, and Yahoo added in response |
| 2026-07-31 | Phase 5 (refresh + SignalR) and Phase 7 (Angular shell + design system). SignalR's separate serializer found by connecting a real client. D1–D3, D5, D9 closed |
| 2026-08-01 | Phase 6 (calculations — TWR hand-checked), Phase 8 (transactions UI), Phase 9 (overview + detail pages), then the **first rendered-pixel verification this app ever had**, which produced D14–D19 |
| 2026-08-07 | Ten drawbacks closed in one session (D10–D12, D17, D20–D25) plus Phase 12 (asset management + theme toggle), all browser-verified. D12 — the backfill having no caller — was the most consequential |
| 2026-08-08 | Every remaining pre-Phase-10 drawback closed (D4, D4a, D7, D13, D27, D30, D31, plus D16/D18 residuals). Podman → Docker retarget, documentation only |
| 2026-08-09 | Phase 10 — the real four-service Docker stack, built and run end to end against a genuinely empty volume. D28, D29 closed; D32–D34 found and fixed by running the containerised app for real |
| 2026-08-10 | D35 — a user-reported page failure traced to structurally guaranteed provider starvation |
| 2026-08-21 | **First live NYSE window in project history**, which immediately exposed D38: the stock quote path had never once succeeded. D37 and D38 fixed and verified during real market hours |
| 2026-08-24 | D39 (credit ledger reconciliation) and D36 (zero-price acquisitions) closed. The stale-container trap found and documented |
| 2026-08-25 | Phase 11's four browser checks closed, D36's browser gaps closed, `CLAUDE.md`'s stale market-data routing corrected. D6 baseline captured |
| 2026-08-26 | Asset deletion added — `DELETE /api/assets/{id}` cascading to transactions, price history and quote, plus the `/assets` delete action. Verified against the live API and SQL Server's own transaction log; not browser-verified (the Chrome extension is still unavailable). A user then spotted that the delete buttons were not red, which exposed trap 10: Material's `color` input is inert under M3, and `ConfirmDialog`'s destructive styling had been dead since Phase 12 |

---

## Running it

```bash
dotnet build portfolio.slnx && dotnet test portfolio.slnx     # always pass the .slnx explicitly
dotnet ef database update -p src/Portfolio.Infrastructure -s src/Portfolio.Api
dotnet run --project src/Portfolio.Api --launch-profile http  # port 5100

cd src/Portfolio.Web && npm start                             # :4200, proxies /api and /hubs
cd src/Portfolio.Web && npm run build && npm test

docker compose up -d                                          # full stack on http://localhost:8080
docker compose logs migrate                                   # where migration problems surface
```

> ⚠️ Pass `--launch-profile http` (or plain `dotnet run`). Suppressing the launch profile leaves
> `ASPNETCORE_ENVIRONMENT` unset, so `appsettings.Development.json` never loads and the API dies at
> startup with *"Connection string 'Portfolio' is not configured"* — which reads like a missing
> secret rather than a missing environment.

**Secrets:** `dotnet user-secrets` locally (`TwelveData:ApiKey` under `src/Portfolio.Api`), `.env`
under compose. CoinGecko needs no key. Never commit a key, write one into `appsettings.json`, or
log one — and never list user-secrets to read one back.

**Environment as built:** .NET 10 SDK 10.0.302, Node 22.23.2 (pinned in `package.json` `engines`;
Angular 22's CLI hard-refuses below 22.22.3), Docker 29.6.2 / Compose v5.3.1, SQL Server via local
`MSSQL$SQLEXPRESS` for dev and `mssql/server:2022-latest` under compose.

**Agents.** Work was split one agent per area, and the boundaries are still the cleanest way to
pick up a change: **`backend-dotnet`** for `src/Portfolio.{Domain,Application,Infrastructure,Api}`
and `tests/`, **`frontend-angular`** for `src/Portfolio.Web`, **`container-docker`** for
`Dockerfile.*`, `compose.yaml`, `nginx.conf` and Docker operations.
