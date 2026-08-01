# Project Tracker

Single source of truth for progress across terminal sessions. Each session is owned by **one
agent**. When a phase completes and the next phase belongs to a different agent, work stops,
this file is updated, and a handoff prompt is issued for the next terminal.

> **Read this file first** at the start of every session. Update it before ending one.

---

## Handoff protocol

1. Open a new terminal in `C:\Users\akmal\source\repos\portfolio`.
2. Paste the prompt from **▶ Next session** at the bottom of this file.
3. Claude reads this tracker, delegates to the named agent, and works only that agent's phases.
4. When the phase set is done, Claude ticks the boxes, appends to the **Handoff log**, writes a
   new **▶ Next session** prompt, commits, and stops.

One agent per terminal. Never let one agent cross into another's phases — the boundaries exist
so each session stays focused and its context stays clean.

---

## Status at a glance

| # | Phase | Agent | Status |
|---|---|---|---|
| 0 | Repo skeleton + agents | — | ✅ Done |
| 1 | Backend scaffold | `backend-dotnet` | ✅ Done — verified |
| 2 | Domain + EF Core | `backend-dotnet` | ✅ Done — verified |
| 3 | Transactions CRUD API | `backend-dotnet` | ✅ Done — verified |
| 4 | Market data providers | `backend-dotnet` | ✅ Done — verified |
| 5 | Auto-refresh + SignalR | `backend-dotnet` | ✅ Done — verified |
| 6 | Portfolio calculations | `backend-dotnet` | ✅ Done — verified |
| 7 | Angular scaffold + shell | `frontend-angular` | ✅ Done — verified |
| 8 | Transactions UI | `frontend-angular` | ✅ Done — verified |
| 9 | Overview + detail pages | `frontend-angular` | ✅ Done — built and wire-verified; no browser driven |
| 10 | Podman stack | `container-podman` | ⬜ Not started |
| 11 | End-to-end verification | — | ⬜ Not started |

**Currently active:** none — Phases 1–9 closed out, Phases 6, 8 and 9 on 2026-08-01. **Phase 9 has
not been confirmed at the rendered-pixel level** — that is the next thing to happen, by a session
with browser automation.

> ✅ **Prices refresh themselves and push over SignalR.** `dotnet build portfolio.slnx` is clean
> with zero warnings under `TreatWarningsAsErrors` and `dotnet test portfolio.slnx` is 95/95 green
> (91 unit + 4 integration). Push delivery was verified with a real SignalR client against a
> running API, not asserted from unit tests.
>
> ✅ **The Angular shell is up and talking to the live API.** `ng build` clean, `ng test` 68/68,
> and the dev proxy plus the SignalR hub were verified against a running API over a real
> **WebSocket** transport — not long-polling fallback, and not mocks.
>
> ✅ **The calculation endpoints are live and hand-checked.** `dotnet test portfolio.slnx` is
> **134/134** (130 unit + 4 integration), build clean. Verified against real backfilled data, not
> only unit tests: per-date historical FX genuinely differs from today's rate on the wire, sub-cent
> prices and 10-dp quantities survive, and the TWR figure was recomputed by hand sub-period by
> sub-period.
>
> ✅ **The transactions UI is complete and D8 is closed.** `ng build` clean, `ng test` **97/97**
> (was 68/68). The D8 question that had blocked the quantity input was settled by measurement
> against a live API, and the answer was **not** the one D8 predicted — see the struck-through
> entry in the register.
>
> ✅ **The overview and detail pages, with all three chart types, are built and wired to the real
> API.** `ng build` clean, `ng test` **138/138** (was 97/97). Every new endpoint's payload shape
> was curled through the dev proxy and matches the frontend DTOs exactly, including the crypto
> `400` on `/performance` and the real "no live quote yet" case (AAPL, in this dev database).
> **This is the one phase that genuinely needed a browser and didn't get one** — read the
> "What was NOT verified" list under Phase 9 before trusting any visual claim about the charts.
>
> **Only Phase 10 is unblocked next**, and it needs Podman installed. A pixel-level pass over
> Phase 9's charts (the orchestrating terminal's own browser tooling) is the other outstanding
> item, not tied to any agent.

---

## Prerequisites

Blocking items you need to handle before the relevant phase can start.

| Item | Status | Needed by | How |
|---|---|---|---|
| Node.js 22.23.2 | ✅ Installed | Phase 7 | Upgraded from 22.13.0 on 2026-07-31 to clear Angular 22's `^22.22.3` CLI gate. Stayed on the 22 line rather than the 24 LTS, to match Phase 10's `node:22-alpine`. Range pinned in `package.json` `engines` |
| `MSSQL$SQLEXPRESS` | ✅ Running | Phase 2 | — |
| .NET 10 SDK | ✅ Installed (10.0.302) | Phase 1 | — |
| **Twelve Data API key** | ✅ Set | Phase 4 | In `dotnet user-secrets` under `src/Portfolio.Api` as `TwelveData:ApiKey` |
| **CoinGecko Demo key** | ✅ Not needed | Phase 4 | Keyless public API works — optional, see Decisions |
| **Podman** | ❌ Missing | Phase 10 | `! winget install RedHat.Podman-Desktop` then `podman machine init && podman machine start` |

.NET SDKs install side by side, so adding 10 will not disturb existing .NET 9 projects.

---

## Phases

### ✅ Phase 0 — Repo skeleton + agents

- [x] `.claude/agents/backend-dotnet.md`
- [x] `.claude/agents/frontend-angular.md`
- [x] `.claude/agents/container-podman.md`
- [x] `CLAUDE.md` — architecture and conventions
- [x] `README.md`, `.gitignore`, `.env.example`
- [x] `git init` + initial commit

---

### ✅ Phase 1 — Backend scaffold · `backend-dotnet`

Verified 2026-07-26. .NET 10 SDK 10.0.302.

- [x] `global.json` pinning the .NET 10 SDK
- [x] `Directory.Build.props` — `net10.0`, nullable, implicit usings, warnings-as-errors
- [x] `portfolio.slnx` with `Portfolio.Domain`, `.Application`, `.Infrastructure`, `.Api`
      *(`.slnx`, the .NET 10 XML solution format — not `.sln`)*
- [x] `tests/Portfolio.UnitTests`, `tests/Portfolio.IntegrationTests`
- [x] Project references wired inward-only — `Portfolio.Domain` has **zero** references
- [x] `dotnet build portfolio.slnx` clean — 0 warnings, 0 errors

### ✅ Phase 2 — Domain + EF Core · `backend-dotnet`

Verified 2026-07-26 against the live `Portfolio` database on `localhost\SQLEXPRESS`.

- [x] Entities: `Asset`, `Transaction`, `PriceQuote`, `PriceHistory`, `FxRate`, `RefreshRun`
- [x] Enums: `AssetClass`, `TransactionType`, `RefreshTrigger`
- [x] `PortfolioDbContext` with **explicit decimal precision on every decimal column** —
      confirmed by querying `INFORMATION_SCHEMA.COLUMNS`, not just by reading the config
- [x] Unique indexes on `(AssetId, Date)` and `(Date, Base, Quote)`
- [x] Initial migration applied — `20260726063301_InitialCreate` present in `__EFMigrationsHistory`
- [x] Seed: AAPL, MSFT, `Z74:XSES`, `ethereum`, `amp-token`, `anvil` (via `HasData`, so
      `EnsureCreated` picks it up too — the integration tests depend on this)
- [x] **Precision test** — 3 tests green against real SQL Server: `0.000123456` units,
      `$0.0005326` price × 1,000,000 units = `532.6000` exactly, and an 8-dp FX rate

**Live schema precision, as verified:**

| Column | Type |
|---|---|
| `Transactions.Quantity`, `.PricePerUnit`, `PriceQuotes.Price`, `PriceHistories.Close` | `decimal(28,10)` |
| `Transactions.Fees` | `decimal(19,4)` |
| `FxRates.Rate` | `decimal(18,8)` |

### ✅ Phase 3 — Transactions CRUD API · `backend-dotnet`

Verified 2026-07-26 by running the API and exercising every endpoint.

- [x] `GET/POST /api/assets`, `GET /api/assets/{id}` (`404` on unknown id confirmed)
- [x] `GET/POST/PUT/DELETE /api/transactions`, with `assetClass` and `assetId` filters
- [x] Validation, all confirmed returning `400` + `ValidationProblemDetails`:
      positive quantity, sell cannot exceed units held, trade date not in future
- [x] DTOs as records; no EF entities leak past the endpoint boundary
- [x] Unit tests + `dotnet test` green — **18 unit + 3 integration, 0 failed**

**Live round-trip proof** — `POST` of 1,000,000 ANVL @ `0.0005326`, read back from SQL Server as
`quantity: 1000000.0000000000`, `pricePerUnit: 0.0005326000`. No precision lost through EF, the
DTO layer, or JSON serialization.

### ✅ Phase 4 — Market data providers · `backend-dotnet`

Verified 2026-07-26 against the real Twelve Data, CoinGecko and Yahoo APIs.

- [x] `IQuoteProvider` + `IFxRateProvider` + `IQuoteProviderRouter` in Application;
      implementations in Infrastructure. No HTTP-client type leaks into Application
- [x] `TwelveDataQuoteProvider` — batched `/quote`, `/time_series` for backfill
- [x] `CoinGeckoQuoteProvider` — all three coins in one `/simple/price` call
- [x] **`YahooQuoteProvider`** — Z74 in SGD, **not in the original plan**, see Decisions
- [x] `TwelveDataFxProvider` — USD/SGD spot + daily history
- [x] Resilience via `Microsoft.Extensions.Http.Resilience` — timeout, retry, circuit breaker;
      Twelve Data deliberately does **not** retry 429 so a rate-limit can't burn more credits
- [x] API keys redacted from logs — default handlers removed via `RemoveAllLoggers()`, replaced
      with `RedactingLoggingHandler`. Grepped the tree: the key appears in no file
- [x] Historical backfill populating `PriceHistory` + `FxRate` from first trade date, idempotent
      against the unique indexes and bounded by `MaxProviderCallsPerRun` (default 20)
- [x] `dotnet build portfolio.slnx` clean, `dotnet test portfolio.slnx` **51/51**

**Live-verified values:** AAPL `333.019989`, MSFT `381.70001`, USD/SGD `1.29073`, Z74 `4.39` SGD,
ETH `1885.08`, ANVL `0.00042369`, AMP `0.00042122`. Backfill re-run inserted 0 rows the second
time — idempotency proven against real SQL Server, not just asserted.

> ⚠️ The dev SQLEXPRESS database now holds 10 `PriceHistory` and 6 `FxRate` rows from that live
> backfill test. Real market data, not test pollution; `Transactions` is back to 0.

### ✅ Phase 5 — Auto-refresh + SignalR · `backend-dotnet`

Verified 2026-07-31 against a running API with a real SignalR client.

- [x] `IMarketCalendar` — NYSE and SGX hours, weekends, holidays, DST-safe via `TimeZoneInfo`,
      no hardcoded UTC offsets. SGX's 12:00–13:00 lunch break is modelled
- [x] `PriceRefreshService` + `PriceRefreshBackgroundService : BackgroundService` — 5 min open /
      60 min closed / 2 min crypto. Grouped by `Asset.QuoteProviderKind`, one batched call per
      provider. A throwing provider is caught and degrades to the last stored quote
- [x] `PricesHub` at `/hubs/prices` broadcasting `QuoteUpdated` + `RefreshStatus`, plus a status
      snapshot on connect so a fresh tab isn't blank until the next tick
- [x] `POST /api/prices/refresh` with 30s cooldown returning `429` + seconds remaining
- [x] `GET /api/prices/status`
- [x] Calendar tests across both 2026 DST boundaries, weekends, holidays and the SGX lunch break
- [x] Background-loop tests: survives a throwing cycle and keeps polling, creates a fresh DI scope
      per tick, shuts down cleanly on cancellation. Uses `FakeTimeProvider`, since the loop waits
      via `Task.Delay(…, TimeProvider, …)` and `MutableTimeProvider` only overrides `GetUtcNow`
- [x] `dotnet build portfolio.slnx` clean, `dotnet test portfolio.slnx` **95/95** (91 unit + 4
      integration)

**Live-verified, 04:0x ET on a Friday** — NYSE closed and SGX inside its lunch break, so both
equity providers were gated and **0 Twelve Data credits** were spent. Over one SignalR connection:
on-connect snapshot, then `QuoteUpdated` for ETH/AMP/ANVL and a `RefreshStatus`, 5 messages total.
Sub-cent precision survived the hub — AMP `0.00039273`, ANVL `0.00051468`. `POST` returned `200`
then `429` with `secondsRemaining: 22`. The background loop ticked on its own before any manual
trigger.

> ⚠️ **SGX holidays are only partially modelled.** Fixed-date Gregorian holidays (New Year, Good
> Friday, Labour Day, National Day, Christmas) are handled; the **lunar ones are not** — Chinese
> New Year, Vesak, Hari Raya, Deepavali. The calendar will report SGX open on those days and the
> refresh will call Yahoo for Z74 anyway. Harmless in credit terms (Yahoo is free and unmetered)
> and it just returns a stale close, but it is a real gap, deliberately left rather than hidden
> behind a per-year table nobody would maintain. NYSE holidays *are* fully rule-based.

> ⚠️ The dev SQLEXPRESS database now holds live `PriceQuote` rows for the three coins and several
> `RefreshRun` rows from the verification runs above. Real data, not test pollution.

> ✅ **Resolved 2026-07-26 — the refresh service writes no history.** It upserts the live
> `PriceQuote` only. `PriceHistory` is written solely by `PriceBackfillService`, for stocks.
>
> **Crypto stores no price history at any point, by decision.** Do not add daily-close persistence
> for crypto "just in case" — it was offered and declined. Crypto's numbers come from transactions
> plus the current quote, which is all the gain/loss card needs.
>
> Consequence to state plainly rather than rediscover later: this is a **one-way door for the
> past**. CoinGecko will not sell back days that go unrecorded, so if crypto charts are ever
> wanted, they can only start from the day history-keeping is switched on — the intervening period
> is gone. Stocks are unaffected; their past stays backfillable on demand from Twelve Data and
> Yahoo.

### ✅ Phase 6 — Portfolio calculations · `backend-dotnet`

Verified 2026-08-01 against a running API and the real backfilled data in dev SQLEXPRESS.

> ⚠️ **Crypto is gain/loss only — no time series.** Decided 2026-07-26, see Decisions. Everything
> below marked *stocks only* must filter to `AssetClass.Stock`. Crypto needs no `PriceHistory` at
> all: its numbers come from transactions plus the current quote.

**Both classes:**

- [x] `ICostBasisCalculator` → `AverageCostCalculator` (fees capitalised into basis on buys,
      deducted from proceeds on sells; a full-close sell snaps to exactly zero)
- [x] Unrealised and realised P&L, including partial sells
- [x] `GET /api/portfolio/{assetClass}/{summary,allocation}` — works for stocks *and* crypto,
      since neither endpoint needs history

**Stocks only:**

- [x] `PerformanceSeriesBuilder` — cost basis step series vs daily market value, USD-converted
      at each date's historical FX rate
- [x] `AnnualReturnCalculator` — time-weighted return per calendar year
- [x] `GET /api/assets/{id}/performance`, `/api/portfolio/stock/annual-returns`
- [x] Heavy unit coverage — TWR verified against a hand-computed multi-year example, with the
      arithmetic written out per sub-period in the test's own comment
- [x] Requesting a series or annual returns for a **crypto** asset returns a clean `400`/`404`
      rather than an empty chart that looks like a flat line at zero

- [x] **Also:** crypto excluded from `PriceBackfillService` — it now filters to `AssetClass.Stock`,
      so no provider call is spent on data nothing reads
- [x] `dotnet build portfolio.slnx` clean, `dotnet test portfolio.slnx` **134/134** (130 unit + 4
      integration)

**Live-verified against real backfilled data (AAPL + Z74, 2026-07-20→24).** A probe ANVL buy of
1,000,000 @ `0.0005326` and a Z74 buy of 100 @ `4.30` SGD with 5 SGD fees were created, read back
through every new endpoint, hand-checked and deleted; dev `Transactions` is back to 0.

- **Per-date FX is genuinely historical, not today's rate.** Z74 on 2026-07-20 came back as
  `340.7973` = `440 SGD / 1.29109` (that date's stored rate). Today's rate `1.29074` would give
  `340.89` — different at 2dp, so this is measured, not assumed.
- **Sub-cent and 10-dp precision survive the new DTOs** — `currentPriceUsd: 0.0005061500`,
  `quantityHeld: 1000000.0000000000`.
- Cost basis renders as a flat **step** across all five points while market value moves.
- SGD cost basis `336.9246` = `435 / 1.29109`, hand-checked.
- Annual TWR `-0.1871%` recomputed by hand across all four sub-periods and the geometric link.
- Crypto id → `400` + `ValidationProblemDetails`; unknown id → `404`; stock with no transactions →
  `200` with an empty series.

> 🐛 **Real defect found in verification and fixed — a deposit was being reported as a gain.**
> `AnnualReturnCalculator` matched cash flows to valuations on **exact date equality**, but the two
> series come from different places: valuations from stored `PriceHistory` dates, flows from each
> transaction's own `TradeDate`. A buy dated a weekend, a market holiday, or any day with no stored
> close was therefore **silently dropped from the subtraction**, and the resulting jump in market
> value was attributed to performance — precisely what TWR exists to prevent. A probe with a $500
> buy between two flat valuations reported **+50% instead of 0%**. Each flow is now attributed to
> the first valuation on or after its own date. Pinned by
> `AnnualReturnCashFlowAlignmentTests` (deposit, withdrawal, and a flow past the last valuation),
> and the probe was confirmed to fail at 50% before the fix.

---

### ✅ Phase 7 — Angular scaffold + shell · `frontend-angular`

Verified 2026-07-31 against a running API through the dev proxy.

- [x] Angular 22.1.2 workspace at `src/Portfolio.Web` — standalone, **zoneless** (no `zone.js`
      dependency at all), SCSS, Vitest, strict templates
- [x] Angular Material 22 + ngx-echarts (installed and provided; charts are Phase 9) +
      `@microsoft/signalr`
- [x] **Central design system — `src/styles/ui.tokens.scss`** plus `ui.mixins.scss`. Every colour,
      space, radius, elevation, type step, layout dimension, z-layer and motion curve is a CSS
      custom property; light/dark via `prefers-color-scheme` **and** a `[data-theme]` override that
      wins in both directions. Material is **derived from** it — `mat.theme()` supplies only the M3
      structural scaffold, then a `--mat-sys-*` block repoints every rendered colour at the tokens,
      so Material never makes a second, parallel colour decision. Grepped clean: no component
      stylesheet contains a hex colour or a raw `px` dimension, only `1px` hairline borders
- [x] `AppShell` — responsive sidenav (Stocks / Crypto / Transactions) + toolbar, via
      `BreakpointObserver`
- [x] Lazy routes `/stocks`, `/stocks/:symbol`, `/crypto`, `/crypto/:symbol`, `/transactions`;
      `/` redirects to `/stocks`. One shared page component per pair, parameterised by `assetClass`
      from route `data` through `withComponentInputBinding()` — pinned by a `RouterTestingHarness`
      test that drives both routes onto the *same* component
- [x] Distinct accent per section — `data-section` on `<html>` swaps one token alias; no forked
      component styles. Pinned by a test that asserts the attribute flips on navigation
- [x] `PriceStore` — signal-based, SignalR-fed, automatic reconnect, 60s HTTP polling fallback only
      once the hub genuinely fails, with a background retry that recovers the push connection.
      Degraded state surfaced in the UI. State machine tested against a fake hub via a DI seam
- [x] `RefreshIndicator` — ticking relative timestamp, manual button, cooldown countdown driven by
      the `secondsRemaining` field on the `429`, stale/degraded warning, and D5 messaging
- [x] `MoneyPipe` / `QuantityPipe` — sub-cent prices never floor to `$0.00`, quantities carry 10 dp
      without trailing-zero noise. Tested over the real values `0.0005326`, `0.00042369`,
      `1000000.0000000000`, `333.019989`
- [x] Dev proxy (`proxy.conf.json`, `ws: true` on `/hubs`); `ng build` clean, `ng test` **68/68**

**Live-verified through the proxy on port 4200, 16:1x SGT Friday** — `GET /api/assets` returned all
6 seeded assets; a real SignalR client connected over **`WebSocketTransport`** (confirmed by name,
so the `ws: true` upgrade genuinely works rather than silently degrading to long polling); the
on-connect `RefreshStatus` snapshot arrived; a manual refresh pushed 4 `QuoteUpdated` frames with
sub-cent precision intact (AMP `0.00039261`, ANVL `0.00050864`); and `POST /api/prices/refresh`
returned `200` then `429` with `secondsRemaining: 22`. NYSE was closed and TwelveData gated, so
**0 Twelve Data credits** were spent.

> ✅ **Node upgraded to 22.23.2** (2026-07-31), clearing Angular 22's `^22.22.3` CLI gate. The
> `node_modules` patch that had worked around it is deleted and the range is pinned in
> `package.json` `engines`. Stayed on the 22 line rather than the 24 LTS so dev matches the
> `node:22-alpine` image Phase 10 plans to use — **keep that image at 22.22.3 or newer.**

### ✅ Phase 8 — Transactions UI · `frontend-angular`

Verified 2026-08-01 against a running API on `localhost:5100`.

> Everything new reads `ui.tokens.scss` — it does not introduce values. See the rule stated at the
> top of that file. Adding a token is fine; inlining a hex or a raw `px` at the call site is not.

- [x] Transactions list with asset-class filter (`All` / `Stock` / `Crypto`)
- [x] `TransactionFormDialog` — typed `FormGroup<{...}>`, **quantity to 10 decimal places** via
      `type="number" step="any"` plus `decimalPrecisionValidator` — never a `step` assuming
      integers or 2dp
- [x] Create / edit / delete with optimistic UI: delete removes immediately and rolls back via
      `reload()` + an inline banner on failure; create/edit splice the server's own response into
      the sorted list through `WritableResource.update()` rather than refetching
- [x] Loading, error and **two** empty states (no transactions at all vs. filter matches nothing),
      plus a non-blocking warning when `/api/assets` itself fails, with New/Edit disabled until it
      resolves
- [x] All three 400 `ValidationProblemDetails` rejections mapped to their own fields, and a bare
      `PUT` 404 shown as its own "deleted elsewhere" state rather than a generic error
- [x] `api-routes.ts` id parameters corrected from `string` to `number`; `local-date.ts` formats
      local date parts directly so `DateOnly` never round-trips through `toISOString()`
- [x] One new token, `--ui-layout-dialog-width-sm`; tree re-grepped clean of hex and raw `px`
- [x] `ng build` clean, `ng test` **97/97** across 17 files (was 68/68)

**Live-verified, independently of the agent** — create → read-back → delete against the real API,
with `Transactions` confirmed at `[]` before and after. Both D8 probes were re-run from this
terminal rather than taken on report; see the D8 entry for what they measured. It was a Saturday
with both markets gated, so **0 Twelve Data credits** were spent (`TwelveData.symbolsRefreshed: 0`).

> 🐛 **Caught by the agent in its own draft, before it shipped.** Its first
> `decimalPrecisionValidator` rejected any value whose `String(value)` contained `"e"` — which
> would have blocked exactly the `0.0000000001` quantity the D8 probe had *just proved the backend
> accepts*, since `String(0.0000000001)` is `"1e-10"`. Now formats via `toLocaleString('en-US', {
> maximumFractionDigits: 20 })`, which never switches to exponential notation, and the exact case
> is pinned by `decimal-precision.validator.spec.ts`.

### ✅ Phase 9 — Overview + detail pages · `frontend-angular`

Built and wire-verified 2026-08-01. **Not verified at the rendered-pixel level — no browser was
driven this session.** See the explicit list of visual-only claims below.

- [x] Loaded the `dataviz` skill before writing the first chart config
- [x] Shared `src/app/shared/charts/chart-theme.ts` — resolves `--ui-*` custom properties off the
      live DOM (with light-mode fallbacks for environments with no stylesheet loaded), fixed mark
      specs (2px lines, ≤24px bars with 4px rounded caps, 8px end-markers with a 2px surface ring,
      10% area opacity), shared axis/tooltip/legend builders. Pie/bar/line all read from it —
      confirmed by reading each chart's own file, not by rendering
> ⚠️ **The two asset classes get different pages** — a locked decision, not an oversight. Crypto
> keeps no price history at all, so `AnnualReturnChart` and `CostVsMarketChart` are never even
> imported into the crypto path; `PortfolioOverviewPage` renders the annual-return panel behind
> `@if (isStock())`, and `AssetDetailPage` never puts `<app-cost-vs-market-chart>` in its template
> for crypto — not a component that renders empty.

**Stocks:**

- [x] `PortfolioOverviewPage` — summary tiles (cost basis, market value, unrealized/realized
      gain-loss), **allocation pie** (cost ⇄ market value toggle via `mat-button-toggle-group`,
      no extra HTTP request on toggle), **annual return bar chart**, holdings table
- [x] `AssetDetailPage` — live price header (falls back to the last persisted snapshot price
      before any SignalR push arrives, then to "Waiting for a live quote…"), **cost vs market
      value line chart** (cost as an ECharts `step: 'end'` series, market value `smooth: true`)
      with a 1M/3M/1Y/All range selector anchored to the *series' own last date* (not wall-clock
      "today", so a lagging dev dataset still populates every range), gain/loss card, per-asset
      transactions (`GET /api/transactions?assetId=`)

**Crypto:**

- [x] Overview — summary tiles, **allocation pie**, holdings table. **No annual return chart** —
      `annualReturnsResource`'s `httpResource` request function returns `undefined` for
      `assetClass() !== 'Stock'`, so no HTTP call is even made, confirmed live through the proxy
- [x] Detail — live price header, **gain/loss card only** (cost basis, market value, absolute and
      percentage gain via `GainLossCard`), per-asset transactions. **No line chart, no range
      selector** — `performanceResource` likewise never requests for crypto, and
      `<app-cost-vs-market-chart>` is behind `@if (isStock())`

**Both:**

- [x] Gains/losses distinguishable without relying on colour alone — `GainLoss` (shared,
      `src/app/shared/gain-loss/`) renders an explicit `+`/`-` sign, an `arrow_upward` /
      `arrow_downward` / `remove` glyph with an `aria-label`, AND the gain/loss colour token, so a
      grayscale render or a screen reader gets the same answer as a sighted colour-reader. Used in
      the annual-return bar labels, the holdings table, the gain-loss card and the summary tiles.
      Unit-tested (`gain-loss.spec.ts`) but the actual rendered glyph/contrast is unconfirmed
- [x] Sub-cent prices render correctly — reused the existing `MoneyPipe`/`QuantityPipe` everywhere
      rather than writing new formatting; `holdings-table.spec.ts` and `gain-loss-card.spec.ts`
      pin ANVL's `0.00050448` rendering as `$0.00050448`, not `$0.00`
- [x] **A holding with `currentPriceUsd: null` renders "Awaiting price" / "Awaiting first price"**,
      never the naive -100% the raw numbers (`marketValueUsd: 0` minus a real cost basis) would
      otherwise read as — pinned in `holdings-table.spec.ts` and `gain-loss-card.spec.ts` against
      the real AAPL probe row (see the live-verification note below). Left as a known, narrower
      gap: the *portfolio-total* tiles do not carry this same override, so a portfolio where every
      holding is unpriced would show a literal -100% total — undocumented in the tracker's
      original scope, not fixed this session
- [x] `ng build` clean (one acceptable bundle-budget warning, +9.68 kB over the 500 kB initial
      budget — chart libraries), `ng test` **138/138** across **25 files** (was 97/97 / 17 files)
- [x] Tree re-grepped clean of hex colours and raw `px` outside `ui.tokens.scss` /
      `ui.mixins.scss` (the 1px hairline-border exception, and 4 new chart-height / tile-min-width
      tokens added to `ui.tokens.scss` for the values this phase introduced) — no exceptions
      needed inside `chart-theme.ts` either; every colour it emits is read from a token, never
      inlined

**Live-verified against the real API on `localhost:5100` through the dev proxy on `:4200`,
2026-08-01 (Saturday, both markets closed):**

- Every new endpoint's exact response shape was curled and matches the frontend's DTOs verbatim —
  `GET /api/portfolio/{Stock,Crypto}/{summary,allocation}`, `GET /api/portfolio/stock/annual-returns`,
  `GET /api/assets/{id}/performance` (200 for AAPL, 400 `ValidationProblemDetails` for a crypto id),
  `GET /api/transactions?assetId=`.
- Probe rows created to see non-trivial data (2 AAPL buys + 1 partial sell, 1 ETH buy, 1 ANVL buy)
  and read back through every endpoint: AAPL came back with `currentPriceUsd: null` /
  `unrealizedPnlPercent: -100` — the **exact real-data case** the "no price yet" override exists
  for, since AAPL has price *history* (for the chart) but no live *quote* in this dev database.
  ANVL's sub-cent quote (`0.0005044800`) and 10-dp quantity (`1000000.0000000000`) round-tripped
  intact. The performance series rendered cost basis as a flat step across 3201 → 3201 → 4830 →
  4830 → 3864 while market value moved daily — the step/smooth distinction is real in this data,
  not just asserted. All 5 probe rows deleted afterward; `GET /api/transactions` confirmed back to
  `[]`.
- `GET /api/prices/status` showed `TwelveData: { lastAttemptedAt: null, symbolsRefreshed: 0 }` —
  **0 Twelve Data credits spent** (NYSE closed all session); CoinGecko ticked its own 2-minute
  cadence unprompted during the session (harmless, unmetered).

**What was NOT verified — explicit, because it's the part that matters most for this phase.** No
browser was driven, so nothing below is confirmed at the rendered-pixel level; each rests only on
reading the ECharts config or the component logic:

- Whether the pie chart's donut, direct labels (shown only ≥8% share) and leader lines actually
  read cleanly, or collide, at real card widths.
- Whether the annual-return bar's signed labels (`position: 'top'`/`'bottom'` by sign) clear the
  zero baseline `markLine` without overlapping it for a small percentage.
- Whether the line chart's two `endLabel`s (cost vs market value) collide when the two series are
  close together at the right edge — marks-and-anatomy.md flags exactly this as a case needing a
  leader line or a fallback to the legend, which was not implemented; only the legend (always
  present for 2 series) and the tooltip are the fallback today.
- Whether SVG-renderer text sizing/contrast holds up in a real browser — jsdom's canvas stub
  (`Not implemented: HTMLCanvasElement's getContext()`) meant even the passing chart specs never
  exercised real text metrics.
- Dark mode: the app has no live theme-toggle button anywhere yet (only `prefers-color-scheme` /
  a manual `[data-theme]` attribute nobody sets), and `chart-theme.ts` resolves tokens once per
  `computed()` re-evaluation, not on a `matchMedia` change listener — an OS dark-mode flip
  mid-session would not repaint an already-rendered chart's baked-in colours without a data change
  forcing a recompute. Untested either way.
- The reactive "reload summary/allocation/performance after a completed refresh cycle" effect
  (`PortfolioOverviewPage`/`AssetDetailPage` constructors) is unit-tested against a fake hub but
  never observed against a real multi-minute refresh cycle.
- Left both `dotnet run --project src/Portfolio.Api` (port 5100) and `ng serve` (port 4200)
  **running** at the end of this session specifically so the next browser-driving pass can start
  immediately without a cold start; kill them if that's not wanted.

---

### ⬜ Phase 10 — Podman stack · `container-podman`

**Blocked by:** Podman install

- [ ] `Containerfile.api` — sdk:10.0 → aspnet:10.0-noble-chiseled, non-root, port 8080
- [ ] `Containerfile.web` — node:22-alpine → nginx:alpine
- [ ] `nginx.conf` — SPA fallback, `/api` proxy, **WebSocket upgrade headers on `/hubs/`**
- [ ] `compose.yaml` — db / api / web, healthcheck-gated startup, named `mssql-data` volume
- [ ] `.containerignore`
- [ ] Verified: `podman compose up -d` → all healthy, app loads, SignalR shows status 101
- [ ] Verified: `down` then `up` preserves data

### ⬜ Phase 11 — End-to-end verification

- [ ] Enter a fractional crypto buy; confirm it persists and the gain/loss card recalculates
- [ ] `/stocks` and `/crypto` totals are fully independent
- [ ] Timestamp updates across a scheduled refresh with no page reload
- [ ] Z74 quote arrives in SGD and converts at the stored USD/SGD rate
- [ ] Twelve Data credit use over one trading day lands well under 800
- [ ] Same migration set applies cleanly to both SQLEXPRESS and the container DB

---

## Known drawbacks — revisit later

Things that are wrong, incomplete, or unproven, kept here so they are decided about rather than
rediscovered. None of these block the next phase. Anything fixed gets struck through with the date,
so the list stays a record and not just a to-do.

| # | Drawback | Phase | Impact | Cost to fix |
|---|---|---|---|---|
| D1 | ~~Manual cooldown could fail to engage when every source was gated~~ **Fixed 2026-07-31** | 5 | — | — |
| D2 | ~~Background refresh loop had no automated test~~ **Fixed 2026-07-31** | 5 | — | — |
| D3 | ~~Refresh status was in-memory and reset on restart~~ **Fixed 2026-07-31** | 5 | — | — |
| D4 | **SGX lunar holidays are not modelled** | 5 | Calendar reports SGX open on Chinese New Year, Vesak, Hari Raya and Deepavali. Yahoo is called anyway and returns the previous close, which is then stored with a fresh-looking timestamp — Z74 silently looks current on days it isn't. No credit cost (Yahoo is unmetered). NYSE holidays *are* fully rule-based. | Medium — needs a maintained per-year table or a holiday API. Deliberately not faked with an approximation. |
| D5 | ~~Manual refresh is a no-op for stocks outside market hours~~ **Fixed 2026-07-31** (frontend messaging, Phase 7) | 5 | — | — |
| D6 | **The 5/60-minute cadence has never run over a real window** | 5 | Only single cycles and one 2-minute crypto interval have been observed live. The NYSE-open 5-minute cadence, the 60-minute closed cadence and an open→close transition are all unobserved, so "well under 800 credits/day" is still arithmetic rather than measurement. | Low, but needs a real trading day — belongs to Phase 11. |
| D7 | **Two serializer configurations still exist** | 5 | The enum-as-int bug is fixed, but REST and SignalR agree only because their defaults happen to coincide (both camelCase). Changing a naming policy on one side, or adding a MessagePack protocol, reintroduces the same class of bug. The regression test only covers enums on the JSON protocol. | Low — assert the two configurations agree, or build both from one shared options factory. |
| D8 | ~~**`decimal(28,10)` over JSON is unproven in a JS client**~~ **Closed 2026-08-01 — measured, and the original premise was wrong** | 3 | Kept in full below, because what it actually measured is worth not rediscovering. | — |
| D9 | ~~Angular CLI's Node check is patched in `node_modules`~~ **Fixed 2026-07-31** — Node upgraded to 22.23.2, patch and `postinstall` hook deleted, pristine CLI gate confirmed restored | 7 | — | — |
| D10 | **A gated provider vanishes from `PriceRefreshCycleResult.sources`** | 5 | `SourceRefreshOutcome.Attempted` is documented as "false when the source's market was closed", but `RunCycleAsync` records that outcome to the status store and then `continue`s **without adding it to the returned list** — so a closed market yields no entry at all, not an `attempted: false` one. Any client reading the skipped set off `sources` gets nothing; the Phase 7 UI derives it from `nyseOpen`/`sgxOpen` instead, which is authoritative. Not a bug in behaviour, but the DTO's own doc comment describes a shape the API never emits. | Low — either add the gated outcome to `outcomes` or correct the doc comment. A `backend-dotnet` call. |
| D11 | **`AssetClass` route/query binding is case-sensitive** | 3, 6 | `/api/portfolio/stock/summary` returns **400**; only `/api/portfolio/Stock/summary` binds. Pre-existing, not a Phase 6 regression — `/api/assets?assetClass=stock` 400s on unmodified Phase 3 code too. Route *literals* are case-**in**sensitive, so `/api/portfolio/{Stock,stock}/annual-returns` both work; only the enum-bound segment is fussy. **The safe frontend rule is to send `Stock`/`Crypto` capitalised everywhere** — that form works on every route. Deliberately not special-cased on the new routes alone, which would create exactly the two-encodings-of-one-field drift D7 warns about. | Low — a custom binder or a `[FromRoute]` string parsed case-insensitively, applied to *both* the route and the Phase 3 query parameter together, never just one. |
| D12 | **Crypto's exclusion from backfill is unit-tested but never observed live** | 6 | `PriceBackfillService` now filters to `AssetClass.Stock`, proven by a unit test (asset skipped, router never consulted, zero rows written). But **no HTTP endpoint triggers a backfill** — true before this phase too — so it has never been watched against a real provider call. | Low, but needs a trigger to exist. Fold into Phase 11. |
| D13 | **Annual returns have only ever run over 5 days of real data** | 6 | Dev SQLEXPRESS holds `PriceHistory` for 2026-07-20→24 only, so the live TWR figure covered one partial week and the union-of-dates timeline never spanned a weekend gap, a year boundary, or two assets with divergent calendars. The *algorithm* is proven by the hand-computed two-year unit test and the cash-flow-alignment tests; the *assembly* of real multi-year inputs is not. | Low — needs a wider backfill or a real holding period. Belongs to Phase 11. |

---

## Handoff log

| Date | Agent | Phases | Outcome |
|---|---|---|---|
| 2026-07-26 | — | 0 | Repo skeleton, three agent definitions, CLAUDE.md committed (`688b1aa`). Backend blocked on .NET 10 SDK. |
| 2026-07-26 | `backend-dotnet` | 1–3 | **Incomplete.** .NET 10 SDK 10.0.302 confirmed installed, SQLEXPRESS confirmed running — Phase 1 unblocked. Agent launched for Phases 1–3, **stopped by the user part-way**. Last signal: initial migration applied to SQLEXPRESS with seed data; agent was about to rebuild. Working tree state was **never inspected** — no build, no test run, no endpoint check observed. Nothing committed; no boxes ticked. Next session starts with an audit. |
| 2026-07-26 | `backend-dotnet` | 1–3 | **Complete and verified.** Audit first: the previous session's work turned out to be *committed* (`fa94714`), not uncommitted as the log above assumed, and the schema/migration/seed claims all held up. Two real defects found: `Portfolio.IntegrationTests` did not compile (`IAsyncLifetime` written against xUnit v3 `ValueTask` while the project pins xUnit 2.9.3), so the precision test had never once run; and `Portfolio.Application` was entirely empty with Phase 3 not started. Fixed the test signatures, removed a dead `quantity.Multiply(...)` line, and built Phase 3. Verified independently of the agent: solution build clean with 0 warnings, `dotnet test` 21/21, and the API run live with a sub-cent fractional round trip plus all three validation rejections. |
| 2026-07-26 | `backend-dotnet` | 4 | **Complete and verified.** User supplied the Twelve Data key (stored in user-secrets, never written to a file) and pointed at CoinGecko's keyless API, which needs no key — Phase 4 unblocked. Smoke-testing the APIs *before* launching the agent caught that **Twelve Data's free tier cannot serve Z74** at all, invalidating the recorded "only free source covering both US and SGX" decision; user chose Yahoo Finance for Z74, so a fourth provider and an explicit `Asset.QuoteProviderKind` dispatch key were added. Agent reported honestly, including flagging CoinGecko's history endpoint as untested — live-testing that gap myself found the one real defect: keyless CoinGecko caps history at 365 days (HTTP 401, `error_code 10012`) and the provider swallowed it as an empty list, which would have silently backfilled nothing for any crypto held over a year. Fixed via `HistoryFetchResult`. Verified independently of the agent: build clean 0 warnings, 51/51 tests, key absent from the entire tree, `Program.cs` byte-identical to Phase 3 (temporary debug endpoints genuinely removed), migration applied and routing correct in SQLEXPRESS. |
| 2026-07-31 | `backend-dotnet` | 5 | **Complete and verified.** Calendar, refresh service, hub, both endpoints built; agent reported honestly and flagged SignalR wire delivery as unverified. Live-testing that flag found the one real defect: **SignalR does not inherit `ConfigureHttpJsonOptions`**, so `QuoteProviderKind` crossed the hub as `"source":0` while REST sent `"source":"TwelveData"` — the exact payload Phase 7 merges, and invisible to every unit test. Fixed at `AddSignalR()` with a regression test the agent confirmed fails when reverted. Verified independently of the agent: build 0 warnings, 85/85 tests, no pending EF model changes, no SignalR type outside `Portfolio.Api`, and a real SignalR client run against the live API — on-connect snapshot plus `QuoteUpdated`/`RefreshStatus` over the wire, sub-cent precision intact (ANVL `0.00051468`), `200` then `429 secondsRemaining: 22`. NYSE closed and SGX in its lunch break during the run, so **0 Twelve Data credits** were spent, and the background loop was observed ticking unprompted. Left knowingly: SGX lunar holidays unmodelled. |
| 2026-07-31 | — | 5 (follow-up) | **Two Phase 5 drawbacks closed.** (1) The manual-cooldown edge case is fixed — a manual cycle now persists its `RefreshRun` even when every source was gated, so `POST /api/prices/refresh` can no longer be hammered with zero crypto assets and all markets closed; the scheduled path still writes nothing there, so the 30-second poll doesn't flood the audit table. (2) The background loop now has tests: survives a throwing cycle and keeps polling, fresh DI scope per tick, clean shutdown — needing `Microsoft.Extensions.TimeProvider.Testing`'s `FakeTimeProvider`, since the loop waits via `Task.Delay(…, TimeProvider, …)` and `MutableTimeProvider` only overrides `GetUtcNow`. Both new tests were confirmed to **fail when their fix is reverted** (missing `RefreshRun`; loop exits instead of retrying) rather than trusted because they were green. 90/90, build clean. |
| 2026-07-31 | — | 5 (follow-up) | **Refresh status made durable (D3), and the drawback register added.** `PriceRefreshStatusStore` now reads and writes a new `SourceRefreshState` table — one upserted row per provider, three rows forever — instead of a process-lifetime dictionary; it became scoped, and `LastRefreshedAt` is derived from the newest `LastSuccessAt` rather than stored separately. The original "it's only a UI indicator" reasoning was wrong: `NextDueAt` gates the cadence, so every restart made all providers due immediately and re-spent Twelve Data credits. Migration `20260731044754_AddSourceRefreshState` applied to SQLEXPRESS. **Live-proven with a real restart**: status survived (`lastRefreshedAt` 04:50:51 from before the restart), CoinGecko was *not* re-called on startup, and the next cycle fired exactly at the persisted due time 04:52:51 — which also verified the 2-minute crypto cadence over a real interval for the first time. The restart test was confirmed to fail when next-due is not persisted. 95/95, build clean. Remaining drawbacks D4–D8 recorded in the new register rather than left in conversation. |
| 2026-08-01 | `backend-dotnet` | 6 | **Complete and verified.** Cost basis, P&L, performance series, TWR, both summary/allocation endpoints and both stocks-only endpoints built; crypto excluded from `PriceBackfillService`. Agent reported honestly, including flagging that it had not exercised the payloads through a JS client and that live annual returns only covered ~5 days of real data. Verifying its work found **one real defect, and a serious one**: `AnnualReturnCalculator` matched cash flows to valuations by **exact date equality**, but valuations come from stored `PriceHistory` dates while flows come from each transaction's `TradeDate` — two series with no guarantee of alignment. Any buy dated a weekend, a holiday, or any day without a stored close was dropped from the subtraction and its money reported as **performance**, which is the one thing TWR is chosen to prevent; a probe showed **+50% where the answer is 0%**. Fixed by attributing each flow to the first valuation on or after its own date, with the probe confirmed failing at 50% before the fix and three regression tests kept (deposit, withdrawal, flow past the last valuation). Also corrected the agent's report on one point: it claimed `/api/portfolio/stock/annual-returns` *requires* lowercase, but route literals are case-insensitive — only the enum-bound segment is case-sensitive, so capitalised `Stock`/`Crypto` works everywhere (**D11**). Verified independently of the agent: build 0 warnings, **134/134**, and a live run against real backfilled data — per-date historical FX proven to differ from today's rate on the wire (`440 SGD / 1.29109 = 340.7973`, where today's rate would give `340.89`), sub-cent and 10-dp precision intact (`0.0005061500`, `1000000.0000000000`), cost basis rendering as a flat step, SGD basis and the `-0.1871%` TWR both recomputed by hand, crypto → `400`, unknown → `404`. Probe transactions deleted; dev `Transactions` back to 0. It was a Saturday, so **0 Twelve Data credits** were spent. Left knowingly: D12 (backfill exclusion unexercised live), D13 (TWR never assembled from multi-year real data), and D8 still open. |
| 2026-08-01 | `frontend-angular` | 8 | **Complete and verified.** Before launching the agent, the D8 brief in this file was found to be **misaimed**: it instructed the next session to measure `1000000.0000000000`, a value that is exactly 10⁶ and provably lossless, so a faithful agent would have passed the probe and closed D8 without ever testing the real exposures. Corrected first, then handed off — the retargeted probes are what produced the two findings now in Decisions. Agent built all four Phase 8 boxes, reported honestly, and **caught a real defect in its own draft before it shipped**: its first `decimalPrecisionValidator` rejected any value whose `String(value)` contained `"e"`, which would have blocked the very `0.0000000001` quantity the probe had just proved the backend accepts. Verified independently of the agent: `ng build` clean, `ng test` **97/97** across 17 files, no hex or raw `px` outside the token files, and a live run against the API — `1e-10` accepted with **201** and read back as `0.0000000001`, and the >15-digit case isolated to the **client** by sending a raw `12345678.1234567891` literal via `curl` that round-tripped through `System.Text.Json` and `decimal(28,10)` exactly while `JSON.stringify` mangles it. All probe rows deleted, dev `Transactions` confirmed back to `[]`. Saturday, both markets gated, **0 Twelve Data credits** spent. **D8 closed.** Left knowingly: no browser was driven this session, so no click-level confirmation of the datepicker, `mat-select` or dialog focus-trapping — component tests exercise the same methods, and the request bodies those methods build were proven against the real API, but the rendered interaction is unobserved. Also unexercised: a non-USD (SGD/Z74) transaction through the new form's currency-auto-set path, and any accessibility tooling. |
| 2026-08-01 | `frontend-angular` | 9 | **Built and wire-verified; explicitly NOT confirmed at the rendered-pixel level — no browser automation was available this session.** Loaded the `dataviz` skill before the first chart config, per instruction. Built the shared `chart-theme.ts` (resolves `--ui-*` tokens off the live DOM with light-mode fallbacks, fixed mark specs), the allocation pie (donut, cost/market toggle with zero extra HTTP calls, colour assigned by stable asset-id identity rather than current-value rank so a re-render never repaints a holding that changed relative size — the exact anti-pattern the skill calls out), the annual-return bar (diverging around a zero `markLine`, signed per-bar labels positioned by sign so a negative bar's label lands under it rather than on the axis), and the cost-vs-market line (cost as a genuine ECharts `step: 'end'` series, market value `smooth: true`, a 1M/3M/1Y/All range anchored to the series' own last date rather than wall-clock "today" so a lagging dev dataset never empties the chart). Both pages rewired onto the real summary/allocation/annual-returns/performance endpoints in place of the Phase 7 placeholder `GET /api/assets` list. Crypto's exclusion from the stocks-only charts is structural — `httpResource`'s request function returns `undefined` for the crypto asset class, so `performanceResource`/`annualReturnsResource` never fire an HTTP call at all, and the chart components are behind `@if (isStock())` in the templates, not rendered-and-hidden. New shared components: `GainLoss` (sign + arrow glyph + colour, three independent cues so colour is never load-bearing alone) and `StatTile`. **One real defect caught and fixed before shipping, in the test suite itself, not the app**: the first attempt at testing a resource that derives its URL from *another* resource's resolved value (`AssetDetailPage`'s performance/transactions calls, which only fire once the `assets` lookup resolves) used `await fixture.whenStable()` to wait for the derived request to appear — this hangs forever in Angular's zoneless test harness, because `whenStable()` tracks the newly-dispatched-but-unflushed request as a pending task and can never resolve while it's outstanding, a chicken-and-egg wait that isn't documented anywhere obvious. Fixed by polling `httpMock.match()` across a few `TestBed.tick()` cycles instead (see `waitForRequest` in `asset-detail.page.spec.ts`), confirmed to actually resolve rather than coincidentally pass. Verified independently after the agent's own report: `ng build` clean (one acceptable bundle-budget warning, chart libraries), `ng test` **138/138 across 25 files** (was 97/97/17), tree re-grepped clean of hex/raw-`px` outside the token files (4 new chart-height/tile-width tokens added for values this phase introduced, none inlined). **Live-verified through the dev proxy against the real API** — curled every new endpoint and confirmed the exact response shape against the frontend's DTOs, created 5 probe transactions (2 AAPL buys + 1 partial sell, 1 ETH buy, 1 ANVL buy) to see non-trivial data, and specifically hit the "no live quote yet" case for real: AAPL has stored `PriceHistory` (so the chart has data) but no `PriceQuote` in this dev database, so `currentPriceUsd: null` / `unrealizedPnlPercent: -100` came back from the live API exactly as documented, and the holdings-table/gain-loss-card both render "Awaiting price" for that row rather than a -100% loss — not simulated, this is what the real API sent. Cost-vs-market performance series confirmed rendering the true step/smooth distinction over real backfilled AAPL data (cost held flat at 3201 across two days, jumped to 4830, dropped to 3864, while market value moved daily). All 5 probes deleted, `GET /api/transactions` confirmed back to `[]`. `GET /api/prices/status` showed **0 Twelve Data credits spent** (`TwelveData.symbolsRefreshed: 0`, NYSE closed all session, Saturday). Left both the API (`:5100`) and `ng serve` (`:4200`) running at handoff so a browser-driving session can start immediately. **Left knowingly, and this is the important part**: nothing about the actual rendered chart output — label collision, the pie's leader lines, the line chart's two `endLabel`s potentially overlapping when the series converge, real text legibility at real card widths, or a live OS dark-mode flip repainting an already-drawn chart — has been looked at. Also left knowingly: the portfolio-total tiles (as opposed to the per-holding rows) do not carry the "no price yet" override, so an all-unpriced portfolio's total would show a literal -100%; this was outside the tracker's stated scope and not fixed. |
| 2026-07-31 | `frontend-angular` | 7 | **Complete and verified.** Angular 22 workspace scaffolded with the central design system the user asked for: `ui.tokens.scss` + `ui.mixins.scss`, with Material *derived from* the tokens rather than themed alongside them. Agent reported honestly and flagged the proxy and hub as never exercised live — testing that flag found **two real defects**. (1) **Ids were typed `string` across `models.ts`** while the backend sends C# `int` as JSON numbers; since the API sets no `AllowReadingFromString`, the Phase 8 transaction form would have `POST`ed `"assetId": "3"` and got a 400. The specs passed only because their fixtures (`'a1'`, `'t1'`) matched the wrong type. Fixed to `number`, then confirmed against the live API (`"id":1`) and the live hub (`"assetId":3`). (2) **The D5 "why nothing moved" logic was dead code** — it filtered `sources` for `attempted: false`, but `RunCycleAsync` drops a gated provider from that list entirely, so the filter could never match and a click with NYSE closed would have said a cheerful "Refreshed 4 symbols" with no explanation. Rewritten to derive closed markets from `nyseOpen`/`sgxOpen`, and its spec rebuilt around a payload captured verbatim from the live API instead of a fabricated one; recorded as **D10**. Also closed the design-system gaps the agent left: raw `px` layout values inlined in five component stylesheets despite the token file's own rule (now `--ui-layout-*` / `--ui-size-icon-*` tokens, with the toolbar height tracking Material's 64→56px breakpoint so the content `calc()` stays right on mobile), and the dark-mode block duplicated between the media query and `[data-theme]` (now one `ui-dark-tokens` mixin, so a token cannot be added to one and forgotten in the other). Verified independently of the agent: `ng build` clean, `ng test` **68/68**, no hex or raw `px` anywhere outside the token files, and a **live run through the dev proxy** — 6 assets over `/api`, a real SignalR client on `WebSocketTransport` (not long-polling), on-connect snapshot, 4 `QuoteUpdated` frames with sub-cent precision intact, `200` then `429 secondsRemaining: 26`. NYSE closed throughout, so 0 Twelve Data credits spent. Left knowingly: the `@angular/cli` Node-check patch (**D9**), and D8's 10-dp *quantity* round trip still unmeasured. |

---

## Decisions

Locked in during planning — see `C:\Users\akmal\.claude\plans\memoized-roaming-crane.md`.

- ~~**Stock data:** Twelve Data free tier (only free source covering both US and SGX)~~
  **Superseded 2026-07-26 — this was wrong**, see Phase 4 decisions below.
- **Crypto data:** CoinGecko (verified to carry `anvil`/ANVL and `amp-token`)
- **Reporting currency:** USD, with historical FX conversion for Z74
- **Auth:** none, single user
- **Database:** SQL Server — local SQLEXPRESS + Windows Auth for dev, `mssql/server` container
  with SQL auth for the stack. Windows Auth cannot work from a Linux container.
- **Cost basis:** average cost, behind `ICostBasisCalculator` so FIFO can drop in later
- **Annual performance:** time-weighted return, so deposits aren't counted as gains

Added during Phase 3 (2026-07-26):

- **Solution format is `.slnx`**, the .NET 10 XML solution format. `dotnet build` / `dotnet test`
  with no argument will not find it — always pass `portfolio.slnx` explicitly.
- **`IPortfolioDbContext`** in `Portfolio.Application/Abstractions` exposes `IQueryable<T>` plus
  save/find, and `PortfolioDbContext` implements it. This keeps services testable and free of the
  SQL Server provider, at the cost of `Portfolio.Application` taking a package reference on
  provider-agnostic `Microsoft.EntityFrameworkCore`. A deliberate trade — revisit if the
  Application layer should stay strictly persistence-ignorant.
- **`JsonStringEnumConverter` is registered globally.** `AssetClass` and `TransactionType` cross
  the wire as strings (`"Crypto"`, `"Buy"`), not ints. The Angular client in Phase 7+ must type
  them as string unions to match.
- **Unit tests use the EF Core InMemory provider**, which does *not* enforce decimal precision.
  Precision is only genuinely proven by `tests/Portfolio.IntegrationTests` against real SQL
  Server — keep that project green, it is the actual tripwire.
- **FluentAssertions is pinned at 8.10.0.** Version 8 moved to a paid licence for commercial use.
  Fine for a personal project; worth a look before this goes anywhere near work.

Added during Phase 4 (2026-07-26):

- **Twelve Data's free tier cannot serve Z74.** The planning decision that it was "the only free
  source covering both US and SGX" was **wrong**. `symbol=Z74&exchange=SGX` returns
  `"This symbol is available starting with the Pro or Venture plan"`. The symbol *is* in their
  catalogue (SGX, MIC `XSES`, SGD) — it is a plan gate, not a bad symbol string. Twelve Data is
  now **US equities + FX only**.
- **Z74 comes from Yahoo Finance** (`query1.finance.yahoo.com/v8/finance/chart/Z74.SI`), chosen by
  the user over manual entry or a paid plan. Free, no key, serves quote *and* daily history in
  SGD. It is **unofficial and undocumented with no SLA**, so it sits behind the same
  `IQuoteProvider` seam and degrades to the last stored quote rather than failing a whole refresh.
  It requires a browser-like `User-Agent` or it 403s.
- **CoinGecko runs keyless — no API key at all.** Per the keyless docs the `x-cg-demo-api-key`
  header is *ignored* on `api.coingecko.com`, so sending one is pointless. `CoinGecko:ApiKey`
  remains optional: set it and the client switches to `pro-api.coingecko.com` + header.
  Keyless limit is ~10–30 req/min IP-based — **lower than the 30/min CLAUDE.md assumes**, but
  fine at one call per 2 minutes.
- **CoinGecko keyless caps history at 365 days**, returning HTTP **401** with `error_code 10012`.
  This was found by live-testing the one endpoint the agent had flagged as unverified, and it was
  a real defect: the provider swallowed it as an empty list, so any crypto held over a year would
  have silently backfilled *nothing*. `GetHistoryAsync` now returns **`HistoryFetchResult`**
  (`Success` / `Truncated` / `RequestedFrom` / `EffectiveFrom` / `Error`) so "nothing requested",
  "truncated by provider policy" and "actually failed" are three distinct outcomes.
  **Largely moot as of the crypto scope decision below** — nothing consumes crypto history any
  more. The type stays as a correctness guard, and it still matters for any provider that
  truncates in future.
- **A free CoinGecko Demo key probably does _not_ lift the 365-day cap** — an earlier note in this
  file claimed it did, which was wrong. The 401 body says *"Public API users are limited to… the
  past 365 days. Upgrade to a **paid** plan"*, and CoinGecko calls the free tier — keyless *and*
  Demo key — the "Public API", with paid being the "Pro API". Their pricing page lists historical
  depth only for paid plans (2 years on Basic/Analyst, from 2013 on Lite/Pro) and none for Demo.
  **Unverified** — nobody has run a 2-year request with a Demo key. Do not plan around a Demo key
  buying deeper history without testing it first.
- **`Asset.QuoteProviderKind`** is the provider dispatch key, not `AssetClass` — stocks now span
  two providers. Crypto is keyed by `ProviderCoinId` (`ethereum`), stocks by `ProviderSymbol`
  (`AAPL`, `Z74.SI`). Migration `20260726103107_AddAssetQuoteProviderKind`; the seeded Z74 symbol
  moved from the dead `Z74:XSES` to `Z74.SI`.
- **Crypto is gain/loss only — no performance over time.** Decided by the user 2026-07-26, after
  the 365-day cap surfaced. Crypto gets cost basis, market value and overall gain/loss (absolute
  and percentage) plus its share of the allocation pie; it gets **no** cost-vs-market line chart
  and **no** year-on-year return chart. Stocks keep both, and are unaffected — Twelve Data serves
  daily history back to at least 2019 on the free key, and Yahoo covers Z74.

  Consequences, which are the point of the decision: crypto needs **no `PriceHistory` rows at
  all**, so it drops out of the backfill job and out of `PerformanceSeriesBuilder` /
  `AnnualReturnCalculator` entirely, and CoinGecko's 365-day limit stops mattering.

  **No crypto price history is stored, and this is accepted as one-way.** An earlier draft of this
  entry claimed history would accumulate going forward by itself, so the decision stayed
  reversible. That was wrong — `PriceBackfillService` is the *only* writer of `PriceHistory`, and
  the refresh service only upserts the live `PriceQuote`. Persisting a daily close was raised as
  an explicit option and **declined 2026-07-26**: crypto keeps no history at all.

  So this is a one-way door for the past. Days that pass unrecorded cannot be bought back from
  CoinGecko's free tier, and beyond 365 days not from any tier below Lite/Pro. If crypto charts
  are ever wanted they start from the day history-keeping is turned on. That is the accepted
  trade — do not reopen it as though it were an oversight. Stocks are unaffected: their history
  is backfillable on demand whenever it is needed.
- **Twelve Data JSON is internally inconsistent** — `/quote` returns numbers as *strings*
  (`"close":"333.019989"`), `/exchange_rate` as a bare *number* (`"rate":1.29073`), a single-symbol
  `/quote` is flat while a batch is keyed by symbol, and `/time_series` values come back
  **descending** by date. Errors arrive as **HTTP 200** with `{"status":"error"}` bodies, and a
  per-symbol failure nests inside an otherwise-successful batch — `IsSuccessStatusCode` alone will
  hand you garbage. Yahoo's raw JSON also carries float noise (`4.440000057220459`), rounded to
  6dp for Yahoo *only* — never on the crypto path, where sub-cent precision is the whole point.

Added during Phase 5 (2026-07-31):

- **SignalR does NOT inherit `ConfigureHttpJsonOptions`.** It has its own protocol serializer, so
  the globally registered `JsonStringEnumConverter` did not reach the hub: `QuoteProviderKind`
  crossed the wire as `"source":0` while the REST endpoint sent `"source":"TwelveData"` — one
  field, two encodings, in payloads the Phase 7 `PriceStore` has to merge. Found by connecting a
  real SignalR client, *not* by any unit test. Fixed at the source with
  `AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))`
  and guarded by `tests/Portfolio.IntegrationTests/SignalR/PricesHubProtocolTests.cs`, which
  resolves the real `IHubProtocol` from DI and was confirmed to fail when the fix is reverted.
  **Any future hub payload carrying an enum depends on that one line.** The Phase 3 rule now holds
  on both transports: enums are strings everywhere.
- **Manual refresh still respects the market gate.** `POST /api/prices/refresh` bypasses the
  *interval*, not the *calendar* — only crypto is unconditional. Clicking refresh at 3am will not
  spend a Twelve Data credit on a closed NYSE. Deliberate: the button means "don't wait for the
  next tick", not "call every provider regardless".
- ~~**Refresh status is in-memory, `RefreshRun` is durable.** `PriceRefreshStatusStore` resets on
  restart, accepted for a "since I last looked" indicator.~~ **Superseded 2026-07-31** — it was not
  only a UI concern, see below.
- **Refresh status is persisted in `SourceRefreshState`; `RefreshRun` stays the audit trail.**
  `PriceRefreshStatusStore` is now scoped and backed by a table holding **one upserted row per
  provider** — three rows, forever, never growing. It was originally in-memory on the reasoning
  that nothing outside the process needed it, which missed that `NextDueAt` **gates the cadence**:
  losing it on restart made every provider due immediately, so each restart re-called every
  provider and re-spent Twelve Data credits that had been spent moments earlier. A debugging
  session with a dozen restarts could take a real bite out of the 800/day budget. Persisting it
  fixes the credit burn and the blank indicator together. `RecordOutcomeAsync` saves immediately
  rather than relying on the caller, because a cycle where every source is gated writes nothing
  else at all and would otherwise lose the closed-market backoff. `LastRefreshedAt` is **derived**
  as the newest `LastSuccessAt` rather than stored, so it cannot drift from the rows it summarises.
  Migration `20260731044754_AddSourceRefreshState`.
- **`BackgroundService` lives in `Portfolio.Application`**, via a
  `Microsoft.Extensions.Hosting.Abstractions` package reference. Pure hosting lifecycle, no HTTP or
  SignalR types, so the inward-only rule still holds — the same trade already made for
  `Microsoft.EntityFrameworkCore`. It opens a DI scope per tick, since `IPortfolioDbContext` is
  scoped while the hosted service is a singleton.
- ~~**Edge case, known and left:** zero crypto assets *and* every equity market closed would leave
  the manual cooldown unenforced.~~ **Fixed 2026-07-31**, see below.
- **A manual refresh always records a `RefreshRun`, even when it does nothing.** The 30s cooldown
  is derived from persisted manual runs, so the old "write a row only if some source actually ran"
  rule meant that with zero crypto assets and every equity market closed, `POST /api/prices/refresh`
  had **no cooldown at all** and could be hammered. It was unreachable with the seeded data, where
  crypto is always present and never gated, but reachable the moment the coins are deleted or
  deactivated. Now a *manual* cycle persists its run even when the outcome is `NothingDue` — the
  row marks the attempt, not any work done. The **scheduled** path deliberately still writes
  nothing in that case: the loop polls every 30 seconds and would otherwise flood the audit table
  with thousands of no-op rows a day. Both halves are pinned by tests.

Added during Phase 6 (2026-08-01):

- **Cash flows are attributed to the first valuation on or after their own date, never matched by
  exact date.** The two series feeding `AnnualReturnCalculator` come from different places —
  valuations from stored `PriceHistory` dates, flows from each transaction's `TradeDate` — so they
  are **not guaranteed to align**. Exact-date matching silently dropped every flow dated a weekend,
  a market holiday, or any day with no stored close, and a dropped deposit is reported as a gain:
  a $500 buy between two flat valuations came back as **+50% instead of 0%**. Transaction
  validation only forbids *future* trade dates, so an off-grid date is fully reachable. A flow
  dated past the last valuation is correctly ignored — no valuation reflects that purchase either,
  so subtracting it would invent a loss.
- **FX carry-forward:** the most recent stored rate at or before the date, falling back to the
  earliest stored rate for dates preceding any stored rate at all. Chosen over throwing, which
  would blank a whole series because one day is missing.
- **Cost basis:** average cost; fees capitalised into basis on buys and deducted from proceeds on
  sells; a full-close sell snaps quantity and cost basis to **exactly** zero rather than leaving a
  decimal-division remainder that would make a closed position look infinitesimally open.
- **Annual return:** daily-valuation TWR, `r = (V(t) − CF(t)) / V(t−1) − 1`, geometrically linked
  per calendar year. A sub-period starting from a **zero** valuation contributes no return — there
  is no rate to compute from a zero base, so initial funding and fully-closed gaps are skipped
  rather than dividing by zero or inventing a 0% that would understate volatility either side.
  Sub-periods link **across** the year boundary, so a Dec 31 → Jan 2 move counts toward January.
- **`DisplayRounding` rounds only at the DTO boundary** — money 4dp, price 10dp, percent 4dp —
  and never inside a calculator. Added after live testing showed FX-chained divisions emitting 20+
  decimal digits on the wire (`310.59027643309141887862193960`). Internal arithmetic stays
  unrounded so the rounding happens once, at the edge.
- **A held position with no `PriceQuote` yet reports `currentPriceUsd: null` and
  `marketValueUsd: 0`**, not an error — a freshly seeded asset must not fail a whole summary.
- **`AssetClass` in a route is case-sensitive; route literals are not.** Send `Stock`/`Crypto`
  capitalised from the frontend and every route works. See **D11**.

Added during Phase 7 (2026-07-31):

- **`src/styles/ui.tokens.scss` is the single source of design truth.** New UI *reads* tokens; it
  does not introduce values. Colour, spacing, radius, elevation, type scale, layout dimensions,
  z-layers and motion all live there as CSS custom properties. **Angular Material is derived from
  it, not configured beside it** — `mat.theme()` contributes only the M3 structural scaffold
  (elevation/shape/state/typography mechanics, which this Material version exposes no API to seed
  from an arbitrary hex), and a `--mat-sys-*` override block immediately repoints every colour
  Material actually paints at the `--ui-*` tokens. Without that block there would be two
  independent palettes drifting apart.
  The one deliberate exception: **breakpoints are SCSS variables, not custom properties**, because
  `@media` cannot read a custom property. They live in `ui.mixins.scss` and nowhere else.
- **The section accent is a token swap, not a forked stylesheet.** `AppShell` sets
  `data-section="stock" | "crypto"` on `<html>` from the active route's `assetClass`;
  `--ui-color-accent` re-aliases and Material follows, because `--mat-sys-primary` points at it.
- **Ids cross the wire as JSON numbers — model them as `number`, not `string`.** `Asset.Id`,
  `Transaction.Id` and `Transaction.AssetId` are C# `int`. The API registers no
  `JsonNumberHandling.AllowReadingFromString`, so `POST`ing `"assetId": "3"` is **rejected with a
  400** rather than coerced. This is the mirror image of the enum rule and easy to get backwards:
  **enums are strings, ids are numbers.** Confirmed against the live API (`"id":1`) and the live
  hub (`"assetId":3`).
- **Never derive "which market was skipped" from `PriceRefreshCycleResult.sources`.** A gated
  provider is omitted from that list entirely rather than reported with `attempted: false` — see
  **D10**. The closed-market set comes from the status snapshot's `nyseOpen` / `sgxOpen` flags.
  `attempted` remains reliable for exactly one thing: telling a provider that was called and failed
  from one that was never called.
- **The hub is the only source of live prices.** There is no "current quotes" REST endpoint, so the
  60-second polling fallback keeps refresh *status* alive (timestamp, market-open flags, stale
  warning) but **cannot** keep per-asset prices current. `PriceStore.connectionState` exposes
  `'polling-fallback'` so the UI says so rather than going quietly stale. If live prices must
  survive a dropped socket, the backend needs a `GET /api/prices` — a Phase 6 decision, not a
  frontend workaround.

Added during Phase 8 (2026-08-01):

- **D8 is closed, and its original premise was wrong.** The entry claimed `1000000.0000000000` was
  "17 significant digits" and therefore at risk of silent corruption in a JS client. Trailing zeros
  are **not** significant digits: the value is exactly 10⁶, exactly representable in IEEE-754, and
  was never at risk. `1000000.0000000001`, `0.0005326` and `1234567.8901234567` also round-trip
  losslessly, and `…0001` / `…0002` remain **distinct** doubles. The proposed fix — serialising
  decimals as strings — was aimed at a non-problem and is **not** needed. The REST contract stands
  unchanged.
- **Exponent notation on the wire is fine.** JavaScript serialises `0.0000000001` as **`1e-10`**,
  not `"0.0000000001"` — a format change, not precision loss, and the likelier real case since
  crypto dust needs no large magnitude to trigger it. `POST`ing a literal `1e-10` returns **201**
  and reads back as `0.0000000001`: `System.Text.Json` binds exponent notation to `decimal`
  losslessly. Verified twice, by the agent and again independently from the orchestrating terminal.
- **The ~15-significant-digit limit is real, and the loss is entirely client-side.** This was
  isolated rather than assumed: a raw `12345678.1234567891` literal sent by `curl`, bypassing JS,
  round-trips through `System.Text.Json` and `decimal(28,10)` **exactly**. `JSON.stringify` on the
  same value emits `12345678.12345679` — so the digits are gone before the request ever leaves the
  browser. **The fix therefore belongs in the form, not the wire format**, which is why
  `decimalPrecisionValidator` caps at 15 significant digits and fails loudly at the input.
- **That 15-digit cap is deliberately conservative.** Between 15 and ~17 significant digits,
  whether a value survives is magnitude-dependent — `1000000.0000000001` (17) is fine, while
  `999999999.9999999999` collapses to `1000000000`. Rather than encode a magnitude-aware rule
  nobody would be able to reason about at the call site, the validator rejects everything past 15,
  which is the universally safe bound. It will refuse a small number of values that would in fact
  have survived. If a legitimate holding ever trips it, widen the cap deliberately — do not remove
  it.

---

## ▶ Next session

Phases 1–9 are done. **Phase 10 is blocked** (needs Podman). Phase 9 was built and wire-verified
but **never confirmed in a real browser** — no browser automation was available in that session.
That is genuinely the next thing that should happen, and it doesn't need a fresh `frontend-angular`
terminal to do it — it needs whichever terminal has Chrome tooling (the orchestrating terminal, per
Phase 9's own handoff note, already has this and was going to look right after that report).

Both the API (`dotnet run --project src/Portfolio.Api`, port 5100) and the Angular dev server
(`ng serve`, port 4200) were left **running** at the end of the Phase 9 session specifically so a
browser pass can start immediately — check they're still up before starting a new pair. If not:

```bash
dotnet run --project src/Portfolio.Api          # port 5100
cd src/Portfolio.Web && npm start               # port 4200, proxies /api and /hubs to 5100
```

**What to actually look at** — the explicit "What was NOT verified" list under Phase 9 in this
file is the checklist: the allocation pie's donut/labels/leader-lines at a real card width, the
annual-return bar's signed labels against the zero baseline, whether the line chart's two
`endLabel`s collide when cost and market value converge near the right edge, real SVG text
legibility (jsdom's canvas stub means even the passing unit tests never rendered real text), and
whether a light/dark OS toggle actually repaints an already-drawn chart (there is no in-app theme
toggle yet, only `prefers-color-scheme` / a manual `[data-theme]` attribute nobody sets). The dev
database currently has 0 transactions (probes were created and deleted during Phase 9's own
verification) — you will likely want to create a few real ones through the Transactions page
itself to see non-empty charts, then decide whether to leave or remove them.

If real defects turn up, they belong to `frontend-angular` to fix — but literal pixel-level chart
review, screenshotting, and any resulting fixes can happen directly in this terminal if it has
`claude-in-chrome` or equivalent; a fresh terminal is only strictly required if the fix needs deep
context-window space the current session has already spent (unlikely for chart tweaks).

**After that pass, or in parallel:** Phase 10 (`container-podman`) is still blocked on Podman
being installed — see Prerequisites.

<details>
<summary>Phase 9 prompt — completed 2026-08-01, kept for reference</summary>

```
Read tracker.md. Phase 6 is done and verified, so the calculation endpoints the charts
need all exist and were hand-checked against real data. Phase 8 is done and verified —
the transactions UI, TransactionFormDialog, and the shared MoneyPipe/QuantityPipe,
local-date and validator utilities are all in place and tested at 97/97.

Use the frontend-angular agent for Phase 9 — overview and detail pages.

Load the dataviz skill before writing the first chart config.

The two asset classes get DIFFERENT pages, and this is a locked decision, not an
oversight to tidy up. Crypto is gain/loss only: no cost-vs-market line chart, no annual
return chart, no range selector. Do not render an empty or flat chart for crypto — omit
the component entirely. Stocks get both charts.

Endpoints, all verified live:
  GET /api/portfolio/{assetClass}/summary      both classes
  GET /api/portfolio/{assetClass}/allocation   both classes
  GET /api/portfolio/stock/annual-returns      stocks only
  GET /api/assets/{id}/performance             stocks only; crypto id returns 400

Send assetClass CAPITALISED — "Stock"/"Crypto". Enum route binding is case-sensitive and
lowercase returns 400 (D11). Route literals are not case-sensitive, so capitalised works
on every route.

The performance series returns cost basis as a flat STEP and market value as a moving
line — render cost as a step series, not a smoothed line, or it will misrepresent when
money actually went in.

Sub-cent prices must not floor to $0.00 — ANVL sits near $0.0005 and the API sends
currentPriceUsd at 10dp. Reuse MoneyPipe/QuantityPipe. Gains and losses must be
distinguishable without relying on colour alone.

An asset with a position but no quote yet sends currentPriceUsd: null and
marketValueUsd: 0 — render that as "no price yet", not as a 100% loss.

Everything visual reads src/styles/ui.tokens.scss and the shared chart-theme.ts. Add a
token if one is missing; do not inline a hex or a raw px at the call site.

Note what Phase 8 could NOT verify, because it matters more here than it did there: no
browser was driven, so nothing in this app has been confirmed at the rendered-pixel
level. Phase 8 could lean on wire-level probes because its correctness was mostly in
the request bodies. Phase 9 is charts — a config that unit-tests green can still render
an unreadable axis, a step series drawn as a smooth line, or an unlabelled legend. If
browser automation is unavailable this session, say so explicitly and describe exactly
which visual claims rest on reading the ECharts config rather than looking at output.
Do not describe a chart as "verified" on the strength of a passing unit test.

Tick a box only for something you have personally seen pass, and say plainly what you did
not verify — that flag is what caught the real defects in Phases 4, 5, 6 and 7. Then
append to the handoff log, write the next session prompt, and commit.
```

</details>

<details>
<summary>Phase 8 prompt — completed 2026-08-01, kept for reference</summary>

```
Read tracker.md. Phase 7 is done and verified — the Angular shell, routing, design
tokens, PriceStore and RefreshIndicator all work, ng test is 68/68, and the dev proxy
plus the SignalR hub were confirmed live against a running API over a real WebSocket.

Use the frontend-angular agent for Phase 8 — the transactions UI.

Do the D8 probe FIRST, before building the quantity input on top of it — but read the
corrected D8 entry in the drawback register before you start. Its original premise was
measured and found WRONG: 1000000.0000000000 is exactly 10^6, exactly representable,
and round-trips losslessly. Do not spend the probe re-confirming that value, and do not
close D8 on the strength of it passing. String serialisation is not the fix and is not
needed.

Two things are actually unmeasured. Probe exactly these:
  1. POST a quantity of 0.0000000001. JavaScript serialises it as "1e-10", not
     "0.0000000001". Confirm System.Text.Json binds exponent notation to a decimal
     parameter rather than 400-ing. This is the likely-real case — crypto dust needs no
     large magnitude to hit it.
  2. POST a quantity above ~15 significant digits, e.g. 12345678.1234567891. It will
     silently store 12345678.12345679 with no error. Decide whether that is in scope;
     if not, cap significant digits in the form validator so it fails loudly at the
     input rather than quietly at the database.

Contract rules that are easy to get backwards, both confirmed against the live API:
enums are STRINGS ("Stock"/"Crypto", "Buy"/"Sell"), ids are NUMBERS (assetId is a C#
int and the API rejects "3" with a 400 — it sets no AllowReadingFromString). tradeDate
is a DateOnly, serialised "YYYY-MM-DD" — never round-trip it through toISOString(),
which shifts it by the timezone offset.

Everything visual reads src/styles/ui.tokens.scss. Add a token if one is missing; do
not inline a hex or a raw px at the call site.

Use a typed reactive form (FormGroup<{...}>), quantity to 10 decimal places, and handle
the three backend validation rejections explicitly — they return 400 with
ValidationProblemDetails: positive quantity, sell cannot exceed units held, trade date
not in the future. PUT /api/transactions/{id} additionally returns a bare 404, so the
edit flow has to survive the row being deleted underneath it.

Two things to fix on the way past, both in your own directory:
  - api-routes.ts types its id parameters as string (asset: (id: string)), which
    contradicts the ids-are-numbers rule in models.ts that a real Phase 7 defect
    established. Interpolation hides it at runtime; Phase 8 is where transaction ids
    start flowing into PUT/DELETE URLs, so fix the signatures rather than casting at
    every call site.
  - tradeDate is a DateOnly. Material's datepicker hands back a Date, and
    toISOString() shifts it by the timezone offset — at SGT (UTC+8) an evening entry
    lands on the previous day. Format the local date parts directly.

Stop after Phase 8. Tick a box only for something you have personally seen pass, and
say plainly what you did not verify — that flag is what caught the real defects in
Phases 4, 5 and 7. Then append to the handoff log, write the next session prompt, and
commit.
```

</details>

**Optional backend cleanup**, small and independent of the above — `backend-dotnet` could close
**D10** (gated provider missing from `sources`, a doc-vs-behaviour mismatch) and **D11** (make
`AssetClass` binding case-insensitive on *both* the Phase 6 route and the Phase 3 query parameter,
never just one). Neither blocks anything.

**Blocked, for later:**

- **Phase 10** (`container-podman`) needs Podman installed. `Containerfile.web`'s Node base image
  must be **22.22.3 or newer** — Angular 22's CLI hard-refuses anything older, so a plain
  `node:22-alpine` tag is only safe if it currently resolves above that. Pin it explicitly.
