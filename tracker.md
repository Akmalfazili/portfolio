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

**Defects D1–D43 are all closed except D6**, which is the measurement described below rather
than a fault. The register is kept as history, not as a to-do list.

**Test suites, re-run 2026-08-28 (dividend income tracking added, then D41/D42 fixed same day, then
surfaced in the Angular app):** backend `dotnet test portfolio.slnx` **268/268** (258 unit + 10
integration, up from 227/227 — the dividend work added 35 unit tests and 2 integration tests, the
D41/D42 fixes added 6 more unit tests, and the existing delete-cascade test was extended in place).
Frontend `ng test` **302/302** across 32 files (up from the last-verified 2026-08-26 baseline of
211/211 across 29 files -- the dividend UI work added 3 new spec files' worth of cases across
`local-date.spec.ts`, `holdings-table.spec.ts`, `portfolio-overview.page.spec.ts` and
`asset-detail.page.spec.ts`, then a same-day two-round browser-verification follow-up added 4 to
`money.pipe.spec.ts` for `MoneyPipe`'s new `'price'` mode and 2 more to
`portfolio-overview.page.spec.ts` pinning the `.overview__tiles--five` modifier's presence/absence
-- all personally re-run this session, not carried over from memory).
Backend builds clean under `TreatWarningsAsErrors`; `ng build` carries the same two accepted
warnings as before (the ~57 kB-over initial bundle budget from the chart libraries, and — new this
entry — `asset-detail.page.scss` landing 131 bytes over its 4 kB per-component style budget, from
the new Dividends panel's rules; both are warnings only, no error, per `angular.json`'s
`anyComponentStyle` ceiling of 8 kB).

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

**A live NYSE window opened on 2026-08-25 and was NOT captured — 11.5% of the session.**
This entry originally read "*and was left running*", with a snapshot taken at 13:52 UTC and a
declared 7-minute contamination at the head of the window. That was written forward, as an
intention, and never confirmed. The user pointed out they had shut the laptop down around 22:00
SGT; the database agrees, and the original wording was wrong.

| Check | Reading |
|---|---|
| NYSE session (Tue 2026-08-25) | 13:30 – 20:00 UTC |
| Stack up from | ~13:37 UTC — a restart 7 minutes after the open |
| **Last `RefreshRuns` row** | **14:22:02 UTC = 22:22 SGT** |
| Session actually covered | **45 of 390 minutes — 11.5%** |
| Never observed | 5 h 45 m — the back **88%** of the window |
| `TwelveDataCreditLedgerEntries` for the day | 109 credits — **not** a full-day figure |

The 109 credits are mostly *closed-market* polling (runs start 05:57 UTC) plus 45 minutes at the
open-market cadence. Quoting it as a D6 answer would understate a real full day substantially.

**The lesson is the wording, not the shutdown.** A laptop going to sleep is ordinary; describing a
run as "left running" before anything had confirmed it kept running is not. The declared 7-minute
gap at the *head* of the window read as diligence and drew attention away from the fact that the
entire *tail* was missing — the same shape as D10/D26/D33/D35/D38, where a report reads healthier
than the reality behind it. **Never write up a measurement window in the future tense.** Close it
by re-reading `RefreshRuns` at the end and stating the observed span, or record nothing.

Verified 2026-08-26 by querying the container database directly:

```bash
docker compose exec -T db bash -lc '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -d Portfolio -W -Q "SELECT CONVERT(date, StartedAt) Day, COUNT(*) Runs, MIN(StartedAt) FirstRun, MAX(StartedAt) LastRun FROM RefreshRuns GROUP BY CONVERT(date, StartedAt) ORDER BY Day DESC;"'
```

**If it is ever picked up again**, the whole procedure is:

0. **The window closes at 20:00 UTC = 04:00 SGT.** That is the middle of the night in this
   timezone, and it is why every attempt so far has died: the laptop goes to sleep long before
   the bell. Either arrange for the machine to stay awake through 04:00 SGT, or accept up front
   that this will be a partial reading and label it as one. Do not start a window intending to
   "leave it running" and write the result up before it has run — see the 2026-08-25 attempt
   above, which captured 11.5% of the session and was recorded as though it had captured all of it.
1. Confirm the stack has been **continuously up** across the session, and confirm it **from
   `RefreshRuns`, at the end, not from `docker compose ps` at the start**. `ps` tells you the
   container is up *now*; only `MAX(StartedAt)` tells you it was still refreshing when the bell
   rang. A restart gap means the refresh may not have run for the whole window — that is exactly
   the contamination D6 exists to avoid, and it is what spoiled every earlier attempt.
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
                               RefreshRun, SourceRefreshState, TwelveDataCreditLedger,
                               DividendEvent, AssetDividendState
                               + AssetClass, TransactionType, QuoteProviderKind, RefreshTrigger
src/Portfolio.Application/     services, DTOs, calculators, provider interfaces, background services
src/Portfolio.Infrastructure/  PortfolioDbContext + 6 migrations, provider clients
src/Portfolio.Api/             4 endpoint groups, PricesHub, DI wiring
src/Portfolio.Web/             Angular 22 workspace
docker/db-init/                Portfolio.DbInit — the one-shot migration runner (NOT in the .slnx)
tests/Portfolio.UnitTests/     252 tests
tests/Portfolio.IntegrationTests/  10 tests — the only place decimal precision and the delete cascade are genuinely proven
```

**Endpoints**

| Route | Notes |
|---|---|
| `GET/POST /api/assets`, `GET/PUT/DELETE /api/assets/{id}` | `PUT` is full-replace including `IsActive` — there is no separate deactivate route. `DELETE` is a hard, cascading delete: `204`, or `404` for an unknown id |
| `GET/POST /api/transactions`, `PUT/DELETE /api/transactions/{id}` | filters: `assetClass`, `assetId` |
| `GET /api/portfolio/{assetClass}/summary`, `/allocation` | both asset classes; `summary` now also carries `totalDividendsTrailing12MonthUsd` / `totalDividendsAllTimeUsd` / `dividendsUncoveredCount` (Stock only — null/0 for Crypto) |
| `GET /api/portfolio/stock/annual-returns` | stocks only |
| `GET /api/assets/{id}/performance` | stocks only — a crypto id returns a clean `400` |
| `GET /api/assets/{id}/dividends` | stocks only — a crypto id returns a clean `400`; full payment history plus trailing-12-month/all-time totals and `coverageStatus` |
| `GET /api/prices/status` | free by design: **never** spends a Twelve Data credit |
| `POST /api/prices/refresh` | 30 s cooldown → `429` + `secondsRemaining`; a large paced batch is queued onto a background task and returns promptly |
| `POST /api/prices/backfill` | bounded; detached onto a background task (HTTP 202) so an nginx 504 cannot cancel it |
| `POST /api/dividends/backfill` | mirrors `/api/prices/backfill`'s own conventions (detached, its own in-flight gate, HTTP 202) — also how D41's zero-asset lockout is recovered by hand |
| `/hubs/prices` | SignalR: `QuoteUpdated`, `RefreshStatus`, plus a status snapshot on connect |

**Key services** — `PriceRefreshService` (+ background service), `PriceBackfillService` (+ daily
background service), `DividendBackfillService` (+ daily background service), `DividendService`,
`PortfolioSummaryService`, `PortfolioPerformanceService`, `AverageCostCalculator`,
`AnnualReturnCalculator`, `DividendIncomeCalculator`, `PerformanceSeriesBuilder`,
`TwelveDataCreditThrottle` / `…CreditPolicy` / `…CadenceCalculator`, `PriceRefreshStatusStore`,
`QuoteProviderRouter`.

**Migrations** (one set, applied identically to SQLEXPRESS and to the container DB):
`InitialCreate` → `AddAssetQuoteProviderKind` → `AddSourceRefreshState` → `AddAssetCreatedAt` →
`AddTwelveDataCreditLedger` → `AddDividendTracking`.

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
- **Page state and action outcome are different things and use different UI.** A resource that is
  loading, empty, or failed to load is *persistent* state that needs a Retry sitting next to the
  content it describes — that stays `app-state-message` and must never be a toast that can vanish
  before it is read. A discrete action that just finished (save, delete, activate) is *transient*
  and gets a snackbar via `NotificationService`. Form validation is neither: the dialog stays open
  on failure, so the message belongs beside the field, not in a toast the user has to correlate
  back to the form. The transient inline warning banners the assets and transactions pages used to
  carry were **replaced** by snackbars, not duplicated by them.
- **`NotificationService` is the only entry point for action feedback, and the error interceptor is
  deliberately not wired to it.** An outcome message has to be phrased by the caller that knows
  what the user was trying to do; a global hook on `errorInterceptor` would also double-toast every
  flow that already reports its own failure. `PriceStore` stays out for the same reason — the 429
  refresh cooldown is UI state on the refresh indicator, never an error toast.
- **The snackbar renders in a CDK overlay attached to `<body>`, outside every component's style
  encapsulation and outside the shell's layout.** Two consequences that are easy to get wrong:
  its container colours can only be reached by repointing `--mat-snack-bar-*` in the global layer
  (not `::ng-deep`), and a `verticalPosition: 'top'` toast anchors to the *viewport*, not below the
  sticky toolbar — it needs an explicit `margin-top` of `--ui-layout-toolbar-height` or it covers
  the toolbar. Read that token rather than hardcoding 64px, so the offset follows its own 56px step
  under the 599px breakpoint. Material's structural CSS puts `margin: 8px` on that same container
  at single-class specificity, and its snack-bar styles are injected into `<head>` lazily on first
  use — *after* the global stylesheet loads — so the override needs a compound selector
  (`.mat-mdc-snack-bar-container.app-snackbar-panel`) to win regardless of injection order.

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

### Average cost per unit is derived at the DTO boundary, not stored (2026-08-26)

The holdings tables on `/stocks` and `/crypto` showed cost basis and current price but nothing
per-unit, so "am I up or down on each share?" could only be answered by dividing two columns in
your head. `HoldingDto.AverageCostUsd` now carries it, and the asset detail page shows it beside
the hero price and as a tile on the gain/loss card.

It is a **derivation, not a new calculation**: `AverageCostCalculator` and `ICostBasisCalculator`
were not touched. `PortfolioSummaryService` divides at the point it builds the DTO, and divides the
**already-`DisplayRounding.Money`-rounded** `CostBasisUsd` — the exact value the DTO carries — so a
reader who divides the two displayed columns by hand gets the third back exactly rather than a
figure off in the last place. Rounding still happens once, at the edge.

Two things it would have been easy to get wrong:

- **Rounded with `Price` (10 dp), not `Money` (4 dp).** It is a per-unit price, not a monetary
  total. ANVL near $0.0005326 rounds to `0.0000` at money precision — the same class of silent
  destruction the `decimal(28,10)` columns exist to prevent, reintroduced at the wire instead of
  the schema.
- **`null` when `QuantityHeld` is zero, never `0m`.** `PortfolioSummaryDto.Holdings` deliberately
  includes fully closed positions for their realised P&L, and `0` there would render as "average
  cost of $0.00" rather than "not applicable" — the D17/D20 family of mistake, a zero that means
  ignorance looking identical to a zero that means a real value. The frontend renders the em-dash
  the `money` pipe already gives for null, and does **not** gate the field behind `hasPrice()`:
  an unpriced holding still has a perfectly good average cost.

The detail page's hero price is in the asset's **native** currency while this figure is USD, so
the label spells out `Avg cost (USD)` whenever the asset is not USD-native — Z74 reads
`SGD 4.51` beside `Avg cost (USD) $1.94`, rather than two dollar-shaped numbers sitting side by
side inviting a subtraction that means nothing. No second percentage was added: the delta between
average cost and current price *is* the unrealised % the gain/loss card already shows.

The holdings table reached eight columns with this, and now scrolls horizontally inside its own
`.holdings-table__scroll` wrapper — the page body never scrolls sideways.

**Verified live**, not from tests: real `GET /api/portfolio/{class}/summary` responses carried
`averageCostUsd` 100.5 for an open position, `null` alongside a `realizedPnlUsd` of 500 for a fully
closed one, and `0.0005333333` for a sub-cent ANVL position. The rendered pages were checked at
1440px and 390px in both themes.

---

### Units held joins the detail-page header row (2026-08-27)

The overview tables always had a `Quantity` column but the asset detail page never surfaced it —
`HoldingDto.quantityHeld` was loaded (the gain/loss card and the transactions list both depend on
the same `holding()` signal) and simply never rendered as its own fact. Added as a header stat,
sitting between the hero price and `Avg cost` in `.detail__price-row`: identity (how many you
hold) reads before cost (what you paid), and both are visually subordinate to the price in the
same way `Avg cost` already was — same type scale, same muted colour, reusing (not duplicating)
the `.detail__avg-cost` SCSS rules under a shared `.detail__units-held, .detail__avg-cost`
selector so the two stats can't drift apart.

Label varies on `isStock()` — "Shares held" for stocks, "Units held" for crypto — rather than one
word doing service for both, and the value goes through the existing `QuantityPipe` (10 dp,
untruncated) instead of `MoneyPipe` or a default `DecimalPipe`, so ANVL-scale and multi-decimal
crypto holdings display in full instead of rounding to 2 dp or 0.

Unlike `Avg cost`, this does **not** gate on the value being non-null: `quantityHeld` is always a
real number (never `null` in the DTO, unlike `averageCostUsd`), and a fully sold-down position
(`quantityHeld === 0`) has a perfectly good answer — "0" — that is just as informative as "1.48".
Copying `Avg cost`'s null-gating pattern here would have been the D17/D20 mistake in reverse:
hiding a real, meaningful zero instead of risking a fake one. The whole stat still only renders
inside the existing `@if (holding(); as h)` branch, so an asset with no transactions at all shows
nothing, same as `Avg cost`.

**Verified live** against the real running stack (rebuilt `portfolio-web` image, real
`/api/portfolio/{class}/summary` data): NVDA read "Shares held 1.48" beside "Avg cost $202.68",
ETH read "Units held 0.0007714" beside "Avg cost $0.00" (its crypto accent colour), and AAPL — a
fully sold-down real position — read "Shares held **0**" beside "Avg cost **—**", confirming the
zero and the null render as visibly different things. Checked at 1440px, 390px, and in dark theme.

---

### Sorting, pagination and virtual scrolling added to all four tables (2026-08-27)

The holdings table, both transaction tables and the assets table were plain `@for` markup with no
way to sort or page — fine at the six-holding, low-double-digit-transaction scale the app was built
against, but not indefinitely. Added without rewriting any of the four as `mat-table` — that
decision was deliberate up front, since the cells carry real behaviour (`routerLink`, `app-gain-loss`,
the D20 close caption, the D27 unpriced hint, edit/delete buttons) that `matColumnDef` would fight
more than help.

**`shared/table/table-state.ts`** is a small signal-based sort/page store, since `MatTableDataSource`
is RxJS-based and does not fit a zoneless app. It takes a rows signal (already filtered by the
caller — this helper composes with filtering, it does not own it), a typed value accessor per
sortable column, a default sort, and an optional tiebreak comparator; it exposes `sorted()`,
`paged()`, `total()`, `pageIndex()` (clamped), and setters. Two rules worth restating because they
are the D17/D20 mistake family applied to sorting: **nulls always sort last, in both directions**
(`compareSortValues` — never coerced to a sortable zero), and **the sort accessor reads the
underlying typed value, never the formatted string** — `tradeDate` compares as its `YYYY-MM-DD`
string directly (lexicographic order is correct; parsing it through `Date` would be the wire-contract
mistake CLAUDE.md already warns about), and price/quantity columns compare real `decimal(28,10)`-scale
numbers so ANVL's $0.0005326 sorts correctly against a $0.00050448 sibling that a naive 2dp-rounded
comparison would treat as equal.

The holdings table's `unrealized` column is a deliberate exception to "always the raw field": the
backend's `unrealizedPnlUsd` is populated even for a holding with no price yet (a -100%-shaped
artefact of cost-basis-minus-zero), but the UI never shows that number — it renders "Awaiting first
price" instead — so the sort accessor returns `null` for exactly those rows, matching what's
actually on screen rather than a number nobody sees.

**Pagination** is `25 / 50 / 100 / All`, via a shared `app-table-pager` component wrapping a
`mat-button-toggle-group` (the size choice) and a standalone `mat-paginator` (`hidePageSize`, prev/
next/first/last + range label only). `mat-paginator`'s own page-size dropdown has no way to render
one option as the word "All" without forking its template — every option renders as a bare number —
so that control is the button-toggle-group instead, a pattern already used elsewhere in this app
(the asset-class filter, the allocation-basis toggle). `ALL_ROWS` (`Infinity`) is translated to the
real row count for `mat-paginator`'s own input, which collapses it to one page for free. The pager
is hidden entirely — not just the size options, the whole control — when the row count fits the
smallest page size (25): under that, a pager is noise.

**Page-index clamping**, not just a page-0 reset, is what stops a delete from stranding the user:
`pageIndex()` is `min(requestedIndex, pageCount - 1)`, recomputed from the live row count on every
read, so deleting the last row on the last page lands on the new last valid page automatically —
covered by `table-state.spec.ts`'s clamping test, which deletes down to zero rows in three steps
and asserts the index never goes negative or points past the end. Sort changes, page-size changes,
and (on the transactions page) the asset-class filter all explicitly reset to page 0 — the filter
reset is a `tableState.resetPage()` call inside `setFilter()`, since the filter itself lives outside
the table-state helper.

**Virtual scrolling — deliberately only the two transaction tables.** Their rows are uniformly one
line tall. The holdings and assets tables are not: the D20 "Close · <date>" caption and the D27
unpriced-identifier hint both add a second line to *some* rows, and `cdk-virtual-scroll-viewport`'s
fixed-size strategy needs one constant `itemSize` for every row. Guessing a row height, or reaching
for the experimental autosize strategy, would have been exactly the kind of thing that passes a
component test and renders as a collapsed/misaligned mess on screen — so for those two tables
selecting "All" simply renders every row unvirtualized, which is fine at the single-digit/low-double-
digit row counts this app actually has. **Do not "fix" this by turning on virtualization for those
two tables** — the reasoning above is the fix already applied.

For the two transaction tables, selecting "All" switches the body from the real `<table>` to a
CSS-grid **ARIA table** (`role="table"/"row"/"columnheader"/"cell"` on plain `<div>`/`<span>`
elements) inside a `cdk-virtual-scroll-viewport`, never a virtualized `<tr>`/`<td>` — wrapping real
table rows in the viewport fights HTML's own table layout algorithm, which expects to size every
row and column itself, not have a CDK transform reposition them. `TRANSACTION_ROW_HEIGHT_PX` (44,
in `shared/table/table-row-height.ts`) is a deliberate duplicate of `--ui-size-table-row-height` in
`ui.tokens.scss` — the CDK fixed-size strategy needs a real JS number for its scroll-offset maths
and cannot read a CSS custom property, the same class of exception `ui.mixins.scss`'s breakpoint
variables already are. Both the real `<table>`'s rows and the virtualized grid's rows read that
same token, so switching page size never visibly re-flows row height.

The header row for the virtualized view lives **outside** the `cdk-virtual-scroll-viewport`,
not pinned inside it with `position: sticky`. This is not a shortcut: `cdk-virtual-scroll-viewport`
positions its rendered content by applying a CSS `transform: translateY(...)` to a wrapper element,
and a `transform` on an ancestor establishes a new containing block that `position: sticky` cannot
escape — a header placed inside the viewport would visually scroll away with the data despite the
`sticky` declaration, a well-known CDK gotcha. Placing the header above the viewport, in normal
document flow, sidesteps the bug entirely and needed no extra CSS to "stick."

`mat-sort-header` reaches every sortable header in both the real `<table>` (on the `<th>`) and the
virtualized grid (on a `<span role="columnheader">`) — it is a plain attribute-selector component
that projects its host's content plus an arrow indicator, tag-agnostic, so it works identically on
either. Both live inside one `[matSort]` container wrapping the `@if`/`@else` branch, so the same
`MatSort` instance serves whichever branch is actually in the DOM. Its own internal
`.mat-sort-header-container` is a block-level flex box that ignores the header cell's `text-align`
(only inline content responds to that), so every right-aligned numeric column needed an explicit
`justify-content: flex-end` on that inner container — otherwise the arrow (and the header text)
sit flush left while every number below reads flush right.

**Trap 10 recurred, mildly.** `MatPaginator`'s enabled prev/next/first/last icon buttons read
`--mat-paginator-enabled-icon-color`'s fallback, `--mat-sys-on-surface-variant` — a role this app's
`--mat-sys-*` repoint block had never touched, so those icons would have painted with `mat.theme()`'s
native seed-palette grey instead of this app's own muted-ink token, a shade apart from every other
muted label (axis text, the D27 hint, table header text). Confirmed by grepping the built bundle for
`--mat-paginator-*` (the literal token names are obscured behind a `%NS%` namespace-substitution
placeholder in the minified output — `-%NS%mat-paginator-container-background-color` — but the
suffix and its `var(--%NS%mat-sys-*, ...)` fallback chain are readable regardless). Fixed the same
way as every other trap-10 case: added `--mat-sys-on-surface-variant: var(--ui-color-on-surface-muted)`
to `styles.scss`'s existing `--mat-sys-*` block, not a component-local override, so anything else
that reads this Material role gets it for free too.

**Known limitation, not fixed:** the transactions tables' Price and Fees columns are the
transaction's own **native** currency (USD for US stocks and crypto, SGD for Z74) — sorting those
two columns therefore compares raw numeric magnitudes across currencies when both are present in
the unfiltered list. The frontend does no FX math (CLAUDE.md), so there is no converted value to
sort on instead without a backend change; left as-is and documented here rather than silently
"fixed" by sorting on a number that would then disagree with what's printed in the cell.

**A virtualized ARIA table silently loses ownership of its rows.** Caught in review, after the
tables were otherwise finished and every `role` assertion in the specs was green. The virtualized
"All" view declares `role="table"` on `.transactions__grid` / `.detail__grid` and `role="row"` on
each row, but CDK renders two elements of its own in between:

```
<div role="table">
  <cdk-virtual-scroll-viewport>                      <- generic, no role
    <div class="cdk-virtual-scroll-content-wrapper"> <- generic, no role
      <div role="row">
```

ARIA requires a `table` to **own** its `row`s — directly or through a `rowgroup`. Two role-less
generic elements in between sever that, so the column and row semantics the template so carefully
declares were not reliably exposed to assistive tech *at all*. This is invisible to tests of the
obvious kind: every `role` attribute is present, every assertion on it passes, and the accessibility
tree is still broken. It is the same shape as the D10/D26/D33/D35/D38 family — a healthy-looking
report over a thing that is not actually working.

Fixed structurally in `shared/table/virtual-rowgroup.ts`: the viewport takes `role="presentation"`
so it drops out of the accessibility tree and its children are exposed to the nearest ancestor still
in it, and the content wrapper becomes the `rowgroup`. CDK exposes the wrapper as neither an input
nor a public member, so it is reached by query in `afterNextRender` — the rare case where a
`setAttribute` beats a template binding. The header row got its own `role="rowgroup"` wrapper.

Compounding it, and fixed in the same pass: virtualization keeps only the visible rows in the DOM,
so a screen reader announced a **12-row table when there were 140**. `aria-rowcount` on the table
(rows + 1 for the header) and `aria-rowindex` on every row (header 1, body `index + 2`) restore the
true size. These are correct only because "All" is the *only* page size that virtualizes, so
`paged()` is the full sorted set and the index really is the row's position in the table — if
virtual scrolling is ever extended to a paged view, both must be offset by `pageIndex * pageSize`.

The non-virtualized path needs none of this: a real `<table>` with `<thead>`/`<tbody>` carries the
same semantics natively, which is exactly why it stayed a real `<table>`.

**Verified by test, not by a real browser.** The **full** suite was run to completion on a quiet
machine: **268 passed, 2 failed**, both `Hook timed out in 10000ms` in a `beforeEach` with zero
assertion failures, and both pass 30/30 when the same files are re-run in isolation. One of the two
(`transaction-form.dialog.spec.ts`) is a file this work never touched. That is
`vitest-base.config.ts`'s own documented flake class, not a regression. The ARIA fix above is
covered by new DOM assertions in both transaction specs (viewport `role="presentation"`, wrapper
`role="rowgroup"`, `aria-rowcount`, `aria-rowindex`), alongside the existing ones that
`mat-sort-header` reaches the `<th>` and emits a real `aria-sort`. `ng build` passes with a **+57KB
initial-bundle budget warning** (557KB against a 500KB budget) from pulling in CDK Scrolling,
MatSort and MatPaginator — a real trade-off left visible rather than silenced by raising the budget.

**Nothing here has been looked at in a browser.** The Chrome extension needed for a real-browser
pass was not available in this session (the same trap 8 gap prior entries record). **1440px/390px,
light/dark, the rendered "All" virtual-scroll view, the sticky header, and the paginator's actual
on-screen colour are all unconfirmed** — say so plainly rather than claiming a check that did not
happen. Note also that the local `SQLEXPRESS` dev DB has the six assets but **zero transactions**,
so it renders every table's empty state; the browser pass needs the Docker stack's data (25 assets,
22 stock holdings, 140 transactions) — run the dev server with a `--proxy-config` pointed at
`http://localhost:8080`. With that data only the transactions table exercises paging at all: 22
holdings and 25 assets both sit at or under the smallest page size, so their pagers are correctly
hidden.

---

### Dividend income tracking added (2026-08-28, backend only)

Per-stock dividend income (trailing-12-month headline, all-time secondary) and a portfolio-level
total, sourced from Yahoo Finance's chart endpoint (`events=div`) — free, keyless, zero Twelve Data
credits. Crypto is excluded entirely, the same as every other reporting feature in this project.

**New tables.** `DividendEvent` (`Id`, `AssetId`, `ExDate` `DateOnly`, `AmountPerShare`
`decimal(28,10)`, `Currency`) — unique on `(AssetId, ExDate)`, mirroring `PriceHistory`'s
`(AssetId, Date)` index — and `AssetDividendState` (`AssetId` PK, `LastAttemptedAt`,
`LastSuccessAt`, `LastRunSuccess`, `LastError`), a **per-asset** mirror of `SourceRefreshState`.
The second table is not incidental: Yahoo's dividend endpoint has no batch form and is called once
per asset, so a genuinely non-dividend-paying stock (zero `DividendEvent` rows forever) is
otherwise indistinguishable from "never fetched" or "last fetch failed" — exactly the
D10/D26/D33/D35/D38 reporting-layer mistake, applied to a new feature before it could repeat it.
`DividendCoverageStatus` (`Covered` / `NotYetFetched` / `FetchFailed`) is derived from this state
and travels on the wire on both `HoldingDto` and the new detail-page DTO; the two USD income
fields are `null` — never a bare `0` — whenever the status is `NotYetFetched`. Migration
`AddDividendTracking`; both FKs are `DeleteBehavior.Restrict`, with the delete written out
explicitly in `AssetService.DeleteAsync` (per the asset-deletion decision above) and
`AssetDeleteCascadeTests` extended to seed one `DividendEvent` and one `AssetDividendState` row
and assert both are gone after delete.

**Routing is wider than the quote router, and that is written down explicitly, not inferred.**
`YahooDividendSymbolResolver` (`Infrastructure/MarketData/Yahoo`) is a two-line static class, but
its doc comment is the one place this project states that Yahoo is the dividend source for *every*
stock — including Twelve Data-routed US equities — because Twelve Data's free tier gates
fundamentals data. It does not switch on `QuoteProviderKind` at all: `AssetClass.Stock` →
`asset.ProviderSymbol` unchanged (Twelve Data and Yahoo happen to agree on US ticker spelling, and
a Yahoo-routed asset's `ProviderSymbol` is already Yahoo's own form, e.g. `Z74.SI`);
`AssetClass.Crypto` → `null`. Per CLAUDE.md's "never infer routing" rule, this mapping had to be
explicit somewhere rather than assumed at each call site.

**`IDividendProvider` is implemented by `YahooQuoteProvider` itself**, not a second typed
`HttpClient`. The task's instruction to "reuse the existing Yahoo HttpClient registration" is
satisfied literally: `YahooQuoteProvider` now implements both `IQuoteProvider` and
`IDividendProvider`, and `GetDividendHistoryAsync` reuses the exact same `HttpClient` (browser
User-Agent, `RedactingLoggingHandler`) the quote/history methods already had, registered once via
`services.AddScoped<IDividendProvider>(sp => sp.GetRequiredService<YahooQuoteProvider>())` —
the same pattern already used for `IQuoteProvider`, `IFxRateProvider` and `ITwelveDataUsageProvider`
in that file. No second `AddHttpClient<T>()` call, no drift risk between two configurations of the
same host.

**The calculator stays FX-agnostic, matching `ICostBasisCalculator`'s established split.**
`IDividendIncomeCalculator.Calculate` takes raw `Transaction`s (for units-held arithmetic, which is
currency-invariant) and a list of `DividendIncomeInput` records whose `AmountPerShareUsd` the
*caller* (`DividendService`) has already resolved via `FxRateResolver` at the ex-date's own
historical rate — never today's, the same rule CLAUDE.md states for every other historical figure
in this codebase. This mirrors `CostBasisTransactionFactory.ToUsd` feeding `AverageCostCalculator`,
and means the calculator's own unit tests need no FX fixtures at all to pin the one rule that
actually matters here:

- **The ex-date boundary is strict.** A buy dated *on* the ex-date earns nothing from that
  event — real market convention, and the reason `DividendIncomeCalculatorTests` pins `<`, not
  `<=`, with a dedicated test for a buy on the exact ex-date next to one the day before and one the
  day after.
- **A fully sold-down position still gets a payment-history row, at zero income** — not silently
  dropped — so a caller can see the payment happened while the position was closed rather than the
  event vanishing from the list entirely.
- Rounding happens once, at the `DividendService` DTO boundary (`DisplayRounding.Money` for USD
  totals, `DisplayRounding.Price` — 10 dp, not 4 — for the native per-share amount, the same
  reasoning `HoldingDto.AverageCostUsd` already established for a per-unit figure), never inside
  the calculator.

**Wire shape.** `HoldingDto` gained three nullable fields — `dividendsTrailing12MonthUsd`,
`dividendsAllTimeUsd`, `dividendCoverageStatus` — null for every `Crypto` holding (never `0`,
preserving the asset-class segregation rule) and null for a `NotYetFetched` stock. `PortfolioSummaryDto`
gained `totalDividendsTrailing12MonthUsd` / `totalDividendsAllTimeUsd` (null for a `Crypto` summary,
a real summed total — zero-contribution from uncovered holdings — for a `Stock` one) and
`dividendsUncoveredCount`, the same "zero-contribution total plus a separate caveat count" pattern
`UnpricedHoldingsCount` already established for market value. The new detail-page endpoint,
`GET /api/assets/{id}/dividends`, rejects a crypto asset id with a clean `400` at the service
boundary (`DividendService.GetAssetDividendHistoryAsync`), the identical shape
`IPortfolioPerformanceService.GetAssetPerformanceAsync` already uses — verified live against the
running API (seeded dev DB): AAPL came back `"coverageStatus":"NotYetFetched"` with both USD
totals `null` and an empty `payments` array (no backfill has run against this dev database yet),
and ETH came back a clean `400` with the expected validation message.

**Refresh cadence.** `DividendBackfillService` mirrors `PriceBackfillService`'s
`RunAsync`/`RunIfDueAsync` split (unconditional entry point vs. a once-per-day gate for the
background poller) but needed no Twelve-Data-style credit budget — Yahoo is unmetered here — so
`DividendBackfillOptions.MaxAssetsPerRun` is a plain safety ceiling (default 500), and ordering is
least-recently-attempted-first, the same D37 anti-starvation pattern `PriceBackfillService` uses,
so a portfolio larger than the ceiling never permanently strands the same tail of assets. Fetches
run from each asset's earliest transaction date forward, so an all-time total is genuinely complete
rather than "since dividend tracking was switched on." `RefreshTrigger` gained
`DividendBackfillScheduled`/`DividendBackfillManual`, sharing the one `RefreshRun` audit table with
every other refresh kind in this project, per its own documented reasoning.

**What was not verified.** The task's brief supplied Yahoo's `events=div` response shape
(confirmed live by the user for AAPL/MSFT/Z74.SI, at zero Twelve Data credit cost) and explicitly
said not to re-verify it — so `YahooQuoteProvider.GetDividendHistoryAsync`'s parsing was written
against that description and exercised only by mocked-provider unit/integration tests, never a
real live call to Yahoo's dividend endpoint by this session. The background `DividendBackfillBackgroundService`
was wired into DI and the app was confirmed to start cleanly with it registered (a brief local run
against the real dev SQLEXPRESS database, `GET /api/assets/{id}/dividends` and
`GET /api/portfolio/stock/summary` both checked live), but the server was stopped within seconds —
a real scheduled dividend-backfill cycle actually reaching Yahoo and inserting `DividendEvent` rows
has not been observed. Nothing in the frontend was touched by this change — see "Dividend income
surfaced in the Angular app" below for that follow-up.

**D41/D42, found the same day by live verification against that same dev database (2026-08-28).**
The coordinator ran the isolated Yahoo-parser probe this section says was never done by this
session — confirming `YahooQuoteProvider.GetDividendHistoryAsync` and
`YahooDividendSymbolResolver` against the real endpoint (AAPL 15 points, MSFT 15 points, Z74.SI 6
points, Z74 correctly carrying **SGD** from `meta.currency`) — and separately found two real
defects by reading the live `RefreshRuns`/`DividendEvents`/`AssetDividendStates` tables:

- **D41 — a run that did nothing still consumed the day.** `RunAsync` wrote its `RefreshRun`
  unconditionally, even when the stock asset list was empty (no stock had a transaction yet), and
  `RunIfDueAsync`'s gate only checked "any scheduled run today" — so that empty run still locked
  the real backfill out for up to 24 hours. Verified live: a `Trigger=4` (`DividendBackfillScheduled`)
  row existed from that day with `SymbolsRefreshed=0, Success=1`, while `DividendEvents` and
  `AssetDividendStates` were both completely empty. This is the D10/D26/D33/D35/D38 family again,
  in a new place: a healthy-looking successful run that accomplished nothing, indistinguishable from
  one that did the work. Fixed by gating on the most recent scheduled run with `SymbolsRefreshed > 0`
  instead of any scheduled run — a zero-asset run (or one where every asset failed, which also
  yields `SymbolsRefreshed == 0`) no longer consumes the day, and retrying it on the next poll costs
  nothing because Yahoo is free and keyless, unlike Twelve Data's credit-limited price backfill
  where a retry has a real budget cost.
- **D42 — `RefreshTrigger.DividendBackfillManual` was dead.** The enum member existed but nothing
  ever wrote it, and there was no way to trigger a dividend backfill on demand — which also meant
  D41's lockout, before it was fixed, could only be waited out, never recovered by hand. Fixed by
  `POST /api/dividends/backfill` (`DividendsEndpoints`), mirroring `POST /api/prices/backfill`'s
  conventions exactly: detached onto a background task so an aborted HTTP connection can never
  misreport a still-running fetch as a provider failure, guarded by its own
  `ManualDividendBackfillInFlightGate` (deliberately a separate type from the price backfill's own
  gate, so the two can run concurrently without tripping each other), `202 Accepted` with
  `DividendBackfillQueuedResult`.

Regression-tested at the service level (`DividendBackfillServiceTests`): a zero-asset `RunIfDueAsync`
no longer returns `AlreadyRanToday` and does not call the provider a second time in the same day;
a run that genuinely processes an asset still correctly locks out the rest of the day; and
`RunAsync(RefreshTrigger.DividendBackfillManual, ...)` writes a `RefreshRun` tagged accordingly —
plus a small dedicated `ManualDividendBackfillInFlightGateTests` for the new gate's TryEnter/Exit
semantics and its independence from the price backfill's gate. Not endpoint-tested via
`WebApplicationFactory`: no such test exists for the sibling `/api/prices/backfill` route either,
and hitting the real endpoint here would fire a real (if free) Yahoo call from the test suite —
service-level coverage of the same detach/gate/trigger logic was judged the safer equivalent.

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

    **This bit a second time the same day**, on the snackbar's Dismiss button, and the second case
    is the more instructive one because nothing looked wrong. The button was a plain `mat-button`,
    so it never received Material's `.mat-mdc-snack-bar-action` class and the
    `--mat-snack-bar-button-color` token that *appears* to govern it never applied — the label fell
    straight through to `--mat-sys-primary`, i.e. `--ui-color-accent`. On the new near-white toast
    surface that put the Dismiss label at roughly **3.1:1 on a Crypto route** (`#eb6834` on
    `#fcfcfb`) and 4.3:1 on a Stocks one, both under AA, *and* made its readability depend on which
    section the user happened to be in. It was on the error toast — the only tone with a Dismiss,
    and the one whose message matters most. Two lessons on top of trap 10's: **a token that exists
    for a component is not necessarily the token that paints your element** — check which selector
    Material's rule actually requires — and **when a surface changes from Material's dark
    `inverse-surface` default to an app surface, every colour that assumed the dark background has
    to be re-checked, not just the ones you set.** Fixed by pinning the label to
    `--ui-color-on-surface`: neutral, section-independent, and the tone is already carried by the
    icon and the accent bar.

---

### Dividend income surfaced in the Angular app (2026-08-28, frontend)

The three new wire fields from the backend section above — `HoldingDto`'s
`dividendsTrailing12MonthUsd`/`dividendsAllTimeUsd`/`dividendCoverageStatus`,
`PortfolioSummaryDto`'s totals/`dividendsUncoveredCount`, and the new
`GET /api/assets/{id}/dividends` endpoint — now render in three places: a stocks-only overview
tile, a sortable holdings-table column, and a detail-page panel. Added to `core/api/models.ts`
mirroring the backend DTOs exactly, and `assetDividends(id)` to `core/api/api-routes.ts`.

**Same D17 shape, applied to income instead of market value.** `dividendsUncoveredCount` (how many
`Stock` holdings are `NotYetFetched`/`FetchFailed`, never `null`, always `0` for `Crypto`) drives a
second `overview__caveat` line on `PortfolioOverviewPage`, reusing the exact same amber-banner
markup `hasUnpricedHoldings()` already established rather than inventing a second visual idiom for
the same underlying "this total is honestly incomplete" fact.

**Three states, not two, wherever a dividend figure could appear** — `Covered` (a real number,
including a legitimate `$0.00` for a genuinely non-dividend-paying stock), `NotYetFetched` (never
attempted — the muted-italic "Awaiting ..." idiom, matching D20/D27's existing "Awaiting price"
treatment), and `FetchFailed` (attempted and failed — its own colour and icon, deliberately **not**
the same muted-italic treatment as `NotYetFetched`, since "we tried and it broke" and "we haven't
asked yet" are different facts a reader needs told apart). `holdings-table.ts`'s new `dividends`
sort column follows the exact `unrealized` column precedent: the sort accessor returns `null`
(sorts last, both directions) for anything that isn't `Covered`, never the field's raw value,
matching what the row actually displays rather than a number nobody sees.

**The column is opt-in, not inferred.** `HoldingsTable` is shared unmodified with the crypto
overview, so it gained a `showDividends` input that `PortfolioOverviewPage` sets to `isStock()` —
the same "the parent decides, the shared component never sniffs `assetClass` off its own rows"
rule the crypto-scope decision already established elsewhere in this file. Crypto's dividend
fields are `null` throughout and are simply never read, because the column never renders for it.

**The detail-page panel mirrors the Transactions panel's own machinery** (`createTableState` +
`app-table-pager`) rather than inventing new sort/page plumbing, sorted by ex-date descending by
default (matching the backend's own ordering). It was deliberately **not** given the transactions
panel's CDK-virtualized "All" view — dividend payment histories are small (a handful a year per
stock), so the complexity of a second virtualized ARIA grid bought nothing here; the plain
`<table>` + pager is the honest scope. The panel itself only ever renders inside `@if (isStock())`,
never even mounted for crypto, and its `httpResource` url is `undefined` for a crypto asset for the
same reason `performanceResource` already is — the endpoint 400s for crypto, so it must never be
called, not called-and-discarded. Four distinct panel states, matching the honesty requirement:
loading, `FetchFailed`-or-a-genuine-HTTP-error (one shared retryable error state, since both mean
"we don't have reliable data right now" from the reader's point of view — retrying just re-GETs
the current, possibly since-recovered, state; it does not force a new Yahoo call itself),
`NotYetFetched`, and — once `Covered` — either the payment table or an honest "No dividends paid"
`app-state-message` for a real `Covered`-with-zero-payments stock, never the same wording as
`NotYetFetched`.

**The required estimate caveat** ("Estimated from units held on each ex-date — excludes
withholding tax, DRIP and scrip handling") sits as a plain caption under the payment table, reusing
the page's existing muted-caption idiom rather than an alarming banner — these are computed
figures, never a broker statement, and the wording says so without being alarming about it.

**`exDate` renders as the raw `"YYYY-MM-DD"` string**, deliberately the same choice
`transactions.page.html`/`asset-detail.page.html`'s existing `tradeDate` cells already make, rather
than reaching for a formatter — this sidesteps the `toISOString()` day-shift bug (CLAUDE.md) simply
by never touching `Date` at all, and it was the simpler-is-safer call given a `DateOnly` value is
already unambiguous as printed. `amountPerShareNative` renders through `MoneyPipe` with the
payment's **own** `currency` (`payment.amountPerShareNative | money: payment.currency`) — never the
USD default — since Z74 pays in SGD and rendering "S$0.103" as if it were a USD figure would be
exactly the D4/D20 silent-unit-mismatch mistake applied to a new field. Verified with a dedicated
spec case using a real SGD payment fixture; note `Intl.NumberFormat` separates an ISO currency code
from the amount with a **non-breaking space** (U+00A0), not a plain one — already documented in
`money.pipe.spec.ts`'s own SGD case, and worth restating here because it silently broke a
`toContain('SGD 0.10')` assertion with a plain space until switched to a regex tolerant of either.

**`shared/util/local-date.ts` gained `formatDateOnly`**, a generic `"YYYY-MM-DD"` → `"Fri 24 Jul"`
formatter that `formatCloseDate` (the existing D20 close-date formatter) now delegates to after its
own instant-to-date-portion slicing — extracted rather than duplicated, and covered by both the
existing `formatCloseDate` tests (unchanged behaviour, still passing) and two new ones pinning
`formatDateOnly` directly. It ended up unused by the dividend payment table itself (see the
`exDate`-as-raw-string decision above), but is not dead code: `formatCloseDate` calls it.

**A genuine test-file text collision, caught by the suite, not missed by it.** The dividend
payment table's "units held" column very nearly kept that literal name, which would have made
`fixture.nativeElement.textContent` contain the substring `"Units held"` on every stock detail page
— colliding with the *existing*, unrelated "Shares held"/"Units held" toggle text in the page
header that an existing D-something-era test already asserted the exact absence of on a stock page
(`expect(text).not.toContain('Units held')`). Renamed the column header to "Units at ex-date" (no
literal "held") rather than loosening the pre-existing test, since the two facts really are
different things that happen to share a word — the header text is a coincidental collision, not a
duplicated concept worth merging.

**A second, structural table collision, same root cause.** Both the dividends panel and the
transactions panel render a plain `<table>` on the same detail page, and the dividend table
originally carried the same `.detail__transactions` class the real transactions table uses (for
free styling reuse) — which made several pre-existing, page-wide-scoped test selectors
(`'tbody tr'`, `'tbody td:first-child'`, `'table.detail__transactions'`) ambiguous the moment the
dividends panel had any rows on screen, since `querySelector`/`querySelectorAll` no longer had
exactly one table to find. Fixed by giving the dividends table its own `.detail__dividends-table`
class (not `.detail__transactions` at all — the two share their visual rules via a combined SCSS
selector, `.detail__transactions, .detail__dividends-table { ... }`, so there is one styling
source, not two drifting stylesheets) and re-scoping every affected transactions-panel test
selector to `table.detail__transactions ...` explicitly. Worth restating for whoever adds the next
per-asset table to this page: a shared style class and a test-selector scope are two different
concerns, and reusing one for the other is exactly how this kind of collision hides until a test
actually exercises both tables' content at once.

**Browser-verified against the real running stack (2026-08-28, same day).** The coordinator
checked this work against the live API on :5100 and the dev server on :4200 with real AAPL/MSFT/Z74
data: the overview tile ($431.04 / "All-time $904.85"), the holdings column (AAPL $106.00, MSFT
$182.00, Z74 $143.04), the detail panel, the payment table, the caveat caption, dark theme, and a
760px viewport all matched the live API and rendered correctly. Two defects surfaced by that check
are fixed below; everything else in this section stands as originally verified only by `ng
build`/`ng test`, not superseded by this pass.

### Two defects found by browser verification, fixed same day

**Defect 1 (the real one) -- `MoneyPipe` rounded a per-share dividend rate like a monetary total,
making two different numbers render identically.** Z74's real payments are 0.103, 0.082, 0.100 and
0.089 SGD/share; `MoneyPipe` capped every value at or above one cent to 2dp (its sub-cent escape
hatch only ever fires below $0.01), so 0.103 and 0.100 both rendered `SGD 0.10` -- two rows a
reader could not tell apart, against identical 1,000-unit holdings and genuinely different income
figures ($80.32 vs $77.07). This is the exact CLAUDE.md rule ("no ... display pipe may assume
integers or two decimal places") landing on a column this session added, not a pre-existing gap --
the backend already draws the same distinction between `DisplayRounding.Money` (totals, 4dp) and
`DisplayRounding.Price` (per-unit values, 10dp), and the frontend needed the equivalent split.

Fixed by giving `MoneyPipe` an optional third `mode: 'total' | 'price'` parameter (default
`'total'`, so every existing call site -- the TTM/all-time tiles, the holdings column, the
`incomeUsd` column, every other money value in the app -- is untouched and still renders at 2dp).
`'price'` mode always extends `maximumFractionDigits` to 10 (matching the backend's own per-unit
precision) rather than only below one cent, so `0.103` renders as `"0.103"` and `0.1` still
renders as `"0.10"` -- trimmed of trailing-zero noise, never padded out to `"0.1000000000"`. Only
the dividend payment table's "Amount / share" cell passes `'price'`
(`payment.amountPerShareNative | money: payment.currency : 'price'`); `currentPriceNative`,
`averageCostUsd` and the like were left calling the pipe exactly as before -- they already read
correctly via the pre-existing sub-cent branch at the magnitudes this app's real data uses, and
auditing every other price display for the same latent gap was outside what this fix was asked to
cover. Added `money.pipe.spec.ts` cases pinning `0.103`/`0.082`/`0.1` in `'price'` mode (including
the exact SGD non-breaking-space detail already documented there) and confirming `'total'` mode's
behaviour is bit-for-bit unchanged.

**Defect 2, round one -- the flexbox fix shipped in the previous entry was itself wrong, caught
live by the coordinator.** `.overview__tiles` had been reworked from Grid to Flexbox
(`flex: 1 0 var(--ui-layout-tile-min-width)`) to stop the fifth tile orphaning -- that DID stop
the orphan, but replaced it with something worse: measured live, the four regular tiles rendered
231px wide and the fifth (Dividends) rendered 960px wide -- four times the width of every sibling,
since flexbox's per-line free-space redistribution gives a LONE wrapped item ALL of that line's
free space, not a proportionate share. Confirmed live: `[231, 231, 231, 231, 960]`. Reverted.

**Defect 2, round two -- the actual fix, verified with real rendered measurements, not CSS
arithmetic.** `.overview__tiles` now uses CSS Grid with an EXPLICIT column count instead of
`auto-fit` guessing one from `minmax(...)` -- `auto-fit` is what produced BOTH broken states,
because it sizes a row by "however many tiles fit," which is four at this container's 960px cap
(`--ui-layout-content-max-wide`), never five, regardless of how the leftover fifth tile is then
handled. An explicit `repeat(5, 1fr)` (stock, via a `.overview__tiles--five` modifier class that
`isStock()` sets — never inferred from tile count) / `repeat(4, 1fr)` (crypto, the base rule) has
no leftover to handle: every tile is always an equal 1/N share of one row, so none can ever carry
more visual weight than another. The explicit column count only applies at/above the `md`
breakpoint (the closest existing token to this container's own 960px cap, reused rather than
inventing a new one — SCSS breakpoints are the one place a raw px value is allowed at all, per
ui.mixins.scss's own header comment); below `md`, the original `auto-fit, minmax(...)` governs,
the same pattern `.gain-loss-card` already uses at its own narrower container, where a partial
last row is an ordinary responsive reflow, not evidence of the same defect.

Five 1fr columns in a 960px container measure ~182px each after gaps — narrower than the 200px
`--ui-layout-tile-min-width` floor auto-fit had enforced — which surfaced a SECOND, real defect:
the Unrealized gain/loss tile's `app-gain-loss` delta ("+$17,494.91 · +41.29%") overflowed its
tile rather than wrapping, measured live at `scrollWidth 170` inside `clientWidth 149`. Root
cause: the amount/separator/percent spans carry no literal whitespace between them in the
rendered template, so there was no text-level line-break opportunity anywhere in that string,
regardless of container width — plain inline wrapping could never have saved this. Fixed in
`gain-loss.scss` by making `.gain-loss__value` itself a `flex-wrap: wrap` flex container (with
`min-width: 0`), which gives three real break points between the amount/separator/percent
independent of any text whitespace, so they wrap onto a second line when the tile is narrow and
stay on one line exactly as before whenever there's room. `.gain-loss__icon-wrap` got an explicit
`flex: none` so the icon itself never shrinks or wraps away.

**Verified with real rendered measurements via a headless Playwright browser against the live
dev server (:4200) and API (:5100)** — not CSS arithmetic, per the coordinator's explicit
instruction, and not the claude-in-chrome skill, which this agent context does not have access
to. `[...document.querySelector('.overview__tiles').children].map(c => [width, top])` plus a
page-wide `scrollWidth > clientWidth + 1` scan, at both 1600px and 760px viewports:

| Page | Viewport | Tile widths | Rows | Any `.stat-tile`/`.gain-loss` overflow? |
|---|---|---|---|---|
| /stocks (5 tiles) | 1600px | `[182, 182, 182, 182, 182]` | 1 | No |
| /crypto (4 tiles) | 1600px | `[231, 231, 231, 231]` | 1 | No |
| /stocks (5 tiles) | 760px | `[229, 229, 229, 229, 229]` | 2 (3+2) | No |
| /crypto (4 tiles) | 760px | `[229, 229, 229, 229]` | 2 (3+1) | No |

The crypto dev database had zero transactions recorded (a genuinely empty portfolio, not a bug),
so the /crypto measurement required a temporary real transaction — `POST /api/transactions` for 1
ETH tagged `notes: "temp-layout-verification-delete-me"`, measured, then removed with
`DELETE /api/transactions/{id}` immediately after, confirmed by re-fetching
`GET /api/portfolio/Crypto/summary` and seeing `holdings: []` again. The 760px 3+2/3+1 splits are
the same ordinary responsive reflow `.gain-loss-card` already exhibits at its own container width
— a shorter final row, never a stretched or shrunk single tile — and were not flagged as a defect.
`ng build`/`ng test` re-run clean after every change in this round (302/302 frontend tests, two
new ones pinning the `.overview__tiles--five` modifier's presence/absence).

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
- **Dividend income is estimated from ex-date holdings, never recorded cash actually received**
  (2026-08-28). No withholding tax, no DRIP/scrip reinvestment, and no brokerage-reported payment
  date is modelled — only the buy/sell ledger's ex-date entitlement. Stated in the XML docs on
  `IDividendIncomeCalculator` and the DTOs so the caveat propagates to whatever UI reads them.

---

## Defect register — D1 to D43

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
| D40 | The allocation pie drew a leader line to **nothing** — slices under 8% share had their label formatter return `''` while the series-level `labelLine.show` stayed `true`, so a thin slice got a line pointing at empty space | 2026-08-27 — user-reported from a screenshot. ECharts treats "label shown" and "label line shown" as independent settings; the fix ties them to the same per-item decision, and the decision is now *always show*. Every slice carries its ticker and percent. Browser-verified by reading the rendered SVG geometry, not by screenshot alone — see the timeline entry for what that turned up |
| D43 | Every `.stat-tile__value` clipped the bottom 1–2px off its own digits — the Dividends tiles on the asset detail page most visibly, but the overview summary tiles too | 2026-08-28 — user-reported from a screenshot. The element rendered at line-height **1.2**, which sits below this font stack’s actual glyph box, and the `overflow: hidden` that D21’s ellipsis needs then cropped the descenders and the `$`. Fixed with a dedicated `--ui-line-height-numeric-display: 1.3` token. **The first fix attempt was a no-op that passed 302 tests**: it set `line-height: var(--ui-line-height-tight)`, and that token *is* 1.2 — the value already computing. Caught only by measuring `scrollHeight - clientHeight` in the live browser |
| D41 | A scheduled dividend backfill that processed **zero** assets still wrote a `RefreshRun` unconditionally, and `RunIfDueAsync` gated on *any* scheduled run today rather than one that accomplished something — so a fresh portfolio's first stock transaction was locked out of dividend data for up to 24 hours with nothing indicating why | 2026-08-28 — found by the coordinator live against the dev database (`RefreshRuns` had a `Trigger=4`, `SymbolsRefreshed=0` row from that day while `DividendEvents`/`AssetDividendStates` were both empty). Fixed by gating on the most recent scheduled run with `SymbolsRefreshed > 0`; a zero-asset (or all-failed) run costs nothing to retry since Yahoo is free and keyless, so it no longer consumes the day |
| D42 | `RefreshTrigger.DividendBackfillManual` existed but nothing ever wrote it — there was no way to trigger a dividend backfill on demand, and no way to recover D41's lockout by hand | 2026-08-28 — `POST /api/dividends/backfill` added (`DividendsEndpoints`), mirroring `POST /api/prices/backfill`'s own conventions exactly: detached onto a background task, guarded by its own `ManualDividendBackfillInFlightGate` (deliberately independent of the price backfill's gate — the two must be able to run concurrently), `202 Accepted` with `DividendBackfillQueuedResult` |

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
| 2026-08-26 | Snackbar action feedback added — `NotificationService` + `AppSnackbar`, replacing the transient inline warning banners on the assets and transactions pages, positioned below the toolbar. Trap 10 recurred on the Dismiss button, caught in review by tracing the token cascade in Material's compiled source rather than by a test. **Not browser-verified** — the Chrome extension is still unavailable (trap 8), so nothing here has been seen rendered in either theme |
| 2026-08-27 | D40 — allocation pie labelling reworked so every slice carries a leader line and its ticker. **The first browser-verified change in this project**: the Chrome extension connected, so trap 8 did not bite. Four rounds, each one caught by looking at the real render — a threshold that hid small labels, a two-tier leader-line length that starved a mid-size slice of room, text sitting above the line instead of beside it, and a generic `moveOverlap` pass that moved labels without their lines. The last two rounds were settled by measuring the rendered SVG with `getBoundingClientRect()` rather than eyeballing screenshots, which both found a defect screenshots had hidden **and** showed that an earlier "2px overlap" reading from the same method had overstated its own severity. The /crypto cost-basis corner (two $0-cost-basis holdings beside one at 100%) is improved, not clean, and is documented as an accepted limit in the component |
| 2026-08-28 | D43 — stat-tile value clipping fixed via a new line-height token. The lesson is the **no-op fix that verifies clean**: the first attempt reasoned out a plausible cause (inheriting body’s fixed 20px line-height), wrote a confident comment asserting it, changed the line-height to a token holding the value the element already had, and then passed a full build and 302 tests. Both the diagnosis and the fix were wrong and nothing in the suite could tell. What settled it was measuring the real element: computed line-height was *already* 1.2× before the change, and a swept ratio → overflow table across the component’s whole `clamp()` range (1.2 → 1–2px clipped everywhere, 1.25 → still clipped at floor and ceiling, 1.3 → clean) picked the value. Second browser-verified change in the project |

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
