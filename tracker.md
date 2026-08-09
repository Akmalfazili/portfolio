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
| 10 | Docker stack | `container-docker` | ✅ Done — verified 2026-08-09, real stack run start to finish |
| 12 | Asset management + theme toggle | `backend-dotnet` + `frontend-angular` | ✅ Done — browser-verified 2026-08-07 |
| 11 | End-to-end verification | — | ⬜ Not started — **now unblocked on the container-DB check**; D6 (a real NYSE trading day) is the only remaining blocker |

**Currently active:** none — Phases 1–10 and **12** are closed out. Only Phase 11 remains, and one
of its six checks is still blocked on **D6** (a real NYSE trading day, which no local session can
manufacture). D34's small frontend follow-up is also now closed — see below.

> ✅ **2026-08-09 (later still): D34's frontend half closed.** `AssetDto.providerHasEverSucceeded`
> now mirrored into `models.ts`, and `asset-management.page.ts`'s `unpricedState()` ANDs it into the
> `suspicious` computation alongside the existing per-provider day threshold, so a never-priced asset
> whose provider has never once succeeded stays in the calm "no price yet" state instead of
> escalating. **2 new tests**, one confirmed to fail against the pre-fix (AND-less) logic by
> reproducing the exact live symptom: created 14 days ago, never priced, provider never succeeded.
> `ng build` clean, `ng test` **196/196** (was 194/194, confirmed as the real pre-change baseline by
> stashing and re-running, not assumed). Rebuilt and redeployed the `web` Docker image; `GET
> /api/assets` through the running stack still matches the backend half's own verified values
> exactly. ⚠️ **Not verified this session: the rendered page in a browser** — this agent's context
> did not have the `claude-in-chrome` tools available, so the visual confirmation the task asked for
> rests on the API contract plus the reproducing test, not a screenshot. See the D34 row and the
> handoff log for the full account.

> ✅ **2026-08-09 (later the same day): D32, D33 and D34 closed — three coupled defects found by**
> **running the containerized app for real, not by reading code.** The user's own symptom report —
> the `/assets` page showing the escalated D27 amber warning on assets whose identifiers were
> correct — traced back through three layers: **D32**, CoinGecko routing to the paid Pro API and
> 401ing because a compose `.env`'s blank `CoinGecko__ApiKey=` line binds to `""`, not `null`, and
> `ApiKey is null` treated that as "a key is configured" (fixed with a single `HasApiKey` property,
> applied at every call site the pattern appeared, not just the one line first reported); **D33**,
> found only while verifying D32 — a refresh cycle where every symbol in a batch failed was still
> recording `LastRunSuccess: true` because `Success` was hardcoded true whenever the HTTP call
> itself didn't throw, so CoinGecko's 401 looked like a clean success with 0 symbols refreshed; and
> **D34**, the D27 escalation firing on a fresh database because seeded assets' `CreatedAt` is a
> static constant and D33's false success meant even a provider that was failing looked proven.
> Fixed in the mandated order (D32 → D33 → D34, since D34's gate reads D33's output) and confirmed
> **each fix corrects the live Docker stack**, not only unit tests: `GET /api/prices/status`'s
> CoinGecko entry went from `lastRunSuccess: true` / `lastError: "CoinGecko returned HTTP 401."` /
> `symbolsRefreshed: 0` to `lastRunSuccess: true` / `lastError: null` / `symbolsRefreshed: 3` after
> rebuilding and redeploying `api`, and `GET /api/assets` went from every asset reading
> `providerHasEverSucceeded: false` to the three CoinGecko-backed ones reading `true` while the
> still-untried TwelveData/Yahoo ones correctly stayed `false` (Sunday, both markets gated all
> session — **0 Twelve Data credits spent**). `dotnet build portfolio.slnx` clean, `dotnet test`
> **189/189** (was 173/173 — 16 new tests, several confirmed to fail against each pre-fix
> behaviour). ⚠️ **D34 is backend-only.** The new `AssetDto.ProviderHasEverSucceeded` field exists
> and is verified on the wire, but `asset-management.page.ts`'s escalation logic does not read it
> yet — that consumption is `frontend-angular`'s to do next. See the D34 row and the handoff log
> entry below for the full account.

> ✅ **2026-08-09: Phase 10 (the Docker stack) is done, and the container genuinely applies the
> same EF Core migrations SQLEXPRESS gets.** `docker compose up -d` against a **freshly-created,
> genuinely empty** `mssql-data` volume brought up `db` (healthy), a new one-shot `migrate`
> service (exit `0`), `api` and `web` — all four confirmed running, no crash loop, no restart.
> Schema creation on a first run, `portfolio_app`-not-`sa` (D29), the volume surviving `down`/`up`
> (never `-v`), and a genuine WebSocket `101` for `/hubs/prices` through nginx were each measured
> directly against the live containers, not inferred. One real defect was found and fixed along
> the way that was not in the drawback register going in: the plain `*-noble-chiseled` runtime
> images set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true`, under which `Microsoft.Data.SqlClient`
> cannot open a connection to SQL Server at all — both `Dockerfile.api` and `Dockerfile.migrate`
> now use the `-extra` chiselled variant instead, still non-root. See the Phase 10 section and the
> handoff log for the full account, including one thing found but **deliberately not fixed** here
> because it needs a `backend-dotnet` change — see **D32**.

> ✅ **2026-08-08: D4, D7, D13, D27, D31 and the D16/D17/D18 residuals are all closed.** **Every
> pre-Phase-10 drawback is now resolved except D6**, which needs a real NYSE trading day and which
> no amount of local work manufactures. Only Phases 10 and 11 remain.
>
> Two of those four were cheaper than this file predicted, and for the same reason worth
> remembering: **the prerequisite had already been paid off by earlier work and nobody had
> re-checked the estimate.** D4 was rated "Medium — needs a maintained per-year table"; it turned
> out every provider *already* stored its own timestamp and only the read path was lying, so it
> became a read-time classification with no table at all. D13 was parked in Phase 11 as needing "a
> real holding period"; closing **D12** had already made real multi-year history reachable by
> backdating a probe, which is exactly what closed it. **Re-derive a stale cost estimate before
> trusting it.**
>
> The 2026-08-07 evening session had cleared everything except D4, D6, D7 and D13.

That session closed **D10, D11, D12, D17, D20, D21, D22, D23, D24, D25** and found and fixed one
new defect of its own, **D26** — the backfill reported provider *failures* as budget skips, so an
asset could stop advancing behind a clean-looking report. It was found by calling the endpoint and
reading the payload, not by reading the code: `assetsSkippedForBudget: ["AAPL"]` alongside
`providerCallsUsed: 1` against a budget of 20.

> ⚠️ **D12 was the single most consequential item in the project and it is now closed.** The
> backfill has callers (a bounded `POST /api/prices/backfill` and a calendar-aware daily background
> service), proven live: MSFT went from **0 to 14 `PriceHistory` rows spanning 2026-07-20 → 08-06**,
> genuinely past the frozen 07-24 window, and idempotent on re-run.

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
> **Phase 10 became unblocked on 2026-08-07**, and was **retargeted from Podman to Docker on
> 2026-08-08** — the user uninstalled Podman and installed Docker Desktop. `podman` is gone from
> the machine; Docker 29.6.2 with Compose v5.3.1 is present. No container artifact had been written
> yet, so nothing was ported — only the docs and the agent definition changed.
>
> ✅ **Phase 12 is complete and browser-verified.** Assets can be added, deactivated and reactivated
> from the UI, the create form surfaces the provider routing rules as *server* validation (D23) plus
> the credit-cost ceiling as a visible warning, and the toolbar carries a three-state Light / Dark /
> Follow OS toggle whose "Follow OS" genuinely removes the attribute and returns to the media query —
> measured, not assumed.
>
> ✅ **A stale close is now shown honestly rather than hidden.** A holding priced from the last close
> renders its price with a `Close · Fri 24 Jul` caption carrying the close's *own* date, and the
> overview caveats totals that exclude an unpriced holding instead of reading as a portfolio-wide
> crash. Both confirmed at the rendered-pixel level.
>
> **Only Phases 10 and 11 remain.** Phase 10 (now Docker) is unblocked but was explicitly deferred by
> the user on 2026-08-07. Phase 11 cannot fully close until Phase 10 lands — see its own note.

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
| ~~**Podman**~~ | ❌ **Uninstalled 2026-08-08** | — | Replaced by Docker at the user's choice. `podman` is no longer on PATH. Nothing had been built against it — the swap cost documentation only. |
| **Docker Desktop** | ✅ Installed and used (29.6.2, Compose v5.3.1) | Phase 10 | Confirmed 2026-08-08, **used successfully 2026-08-09** — `docker compose build && up -d` ran a real four-service stack (db/migrate/api/web) end to end. Desktop was manually paused once, earlier; unpause from the Whale menu if `docker info` ever reports that again. **Phase 10 is done.** |

.NET SDKs install side by side, so adding 10 will not disturb existing .NET 9 projects.

---

## Phases

### ✅ Phase 0 — Repo skeleton + agents

- [x] `.claude/agents/backend-dotnet.md`
- [x] `.claude/agents/frontend-angular.md`
- [x] `.claude/agents/container-podman.md` — **replaced by `container-docker.md` on 2026-08-08**
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

Built and wire-verified 2026-08-01 by the agent. **Browser-verified 2026-08-01 from the
orchestrating terminal** — Chrome driven against the live app at 1600×1100, both themes. This is
the first time any part of this app has been confirmed at the rendered-pixel level.

> 🐛 **Four defects found by looking at rendered output, three of them invisible to every test.**
> See **D14–D17**. The most serious, **D14**, makes the live price on *every* detail page —
> stocks and crypto — render at a contrast ratio of **1.01:1** in dark mode: measurably invisible.
> The agent's own "not verified" list below predicted two of the four as open questions; both
> turned out to be real. That list is why they were found.

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

**Browser verification, 2026-08-01 (orchestrating terminal, Chrome at 1600×1100).** Probe rows were
re-created to get non-trivial data — including a deliberate **second Z74 buy mid-window**, because
with a single buy the cost basis is flat across every point and a step series and a smooth line
render *identically*, so the step claim would have been unfalsifiable. All 5 rows deleted
afterward; `GET /api/transactions` confirmed `[]`. `TwelveData.lastAttemptedAt` still `null` —
**0 Twelve Data credits spent.**

Confirmed passing by looking at output:

- **The cost series is a genuine step.** With cost basis stepping `$336.92 → $689.33` at 07-22, the
  orange series renders flat, then a clean vertical jump at the 22nd, then flat — not a diagonal
  ramp. This is the claim the phase brief cared most about.
- **Sub-cent prices survive to the pixel.** ANVL renders `$0.00050465` in the holdings table and
  `$0.0005326` in the transactions table. Nothing floors to `$0.00`.
- **Crypto really has no charts.** `/crypto` goes Allocation → Holdings with no annual-return
  panel; `/crypto/ANVL` goes header → gain/loss → transactions with no line chart and no range
  selector. Not an empty component — no component.
- **Allocation colours are keyed to asset identity, not rank.** Toggling market value → cost basis
  flips the order (Z74 100% → AAPL 82.7% first) and each symbol *keeps* its colour.
- **Numbers match the API exactly**, checked against curl: TWR tooltip `+1.27%` against
  `timeWeightedReturnPercent: 1.2675`, cost-basis split 82.7/17.3 against `3301/3990.33`.
- **The "no price yet" case renders correctly** — AAPL shows *Awaiting price* / *Awaiting first
  price*, never a naive −100%.

**What was NOT verified by the agent — explicit, because it's the part that mattered most for this
phase.** No browser was driven *by the agent*, so nothing below was confirmed at the rendered-pixel
level by it; each rested only on reading the ECharts config or the component logic. **Resolutions
from the browser pass are marked inline.**

- ~~Whether the pie chart's donut, direct labels (≥8% share) and leader lines read cleanly at real
  card widths.~~ **Checked — they read fine.** Two-slice (ETH 65% / ANVL 35%) and single-visible-
  slice (Z74 100%, AAPL 0%) both render legibly. The 0% slice correctly draws nothing while
  staying in the legend.
- ~~Whether the annual-return bar's signed labels clear the zero baseline `markLine`.~~ **Checked
  — clears fine** at `+1.3%`. Note the bar label rounds to 1dp (`+1.3%`) while the tooltip gives
  2dp (`+1.27%`); both are correct, just different precision.
- ~~Whether the line chart's two `endLabel`s collide when the series are close at the right edge.~~
  **They collide, and it is unreadable — see D15.** With cost `$689.33` and market `$680.32` about
  nine dollars apart, the two labels overlap into an illegible blob. The agent flagged this exact
  risk and the fallback it named (legend + tooltip) does not rescue the rendered label.
- ~~Whether SVG-renderer text sizing/contrast holds up in a real browser.~~ **Chart text is fine;
  the surrounding HTML is not.** A scripted contrast sweep over every leaf HTML element (SVG
  excluded — it paints via `fill`, not `color`, and naive sweeps false-positive on it) found
  **4** elements below 3:1, all from one root cause: **D14**.
- ~~Dark mode: `chart-theme.ts` resolves tokens once and would not repaint an already-rendered
  chart on an OS dark-mode flip. Untested either way.~~ **The agent's suspicion was exactly right
  — see D16.** Measured: after flipping to light, `--ui-color-gridline` is `#e1e0d9` but the SVG
  is still stroking the dark-mode `#2c2c2a`, giving heavy black gridlines on a light chart.
- The reactive "reload summary/allocation/performance after a completed refresh cycle" effect
  (`PortfolioOverviewPage`/`AssetDetailPage` constructors) is unit-tested against a fake hub but
  never observed against a real multi-minute refresh cycle.
- Left both `dotnet run --project src/Portfolio.Api` (port 5100) and `ng serve` (port 4200)
  **running** at the end of this session specifically so the next browser-driving pass can start
  immediately without a cold start; kill them if that's not wanted.

---

### ✅ Phase 10 — Docker stack · `container-docker`

**Done and verified 2026-08-09.** Built fresh against Docker 29.6.2 / Compose v5.3.1 — Podman was
never on the machine by the time this phase actually ran, so there was nothing to port, only the
2026-08-08 doc/agent retarget to build on.

- [x] `Dockerfile.api` — `sdk:10.0.302-noble` → `aspnet:10.0.10-noble-chiseled-**extra**`, non-root
      (`APP_UID`, confirmed live via `docker top` = uid 1654), `ASPNETCORE_HTTP_PORTS=8080`
- [x] `Dockerfile.web` — `node:22.23.2-alpine` (pinned patch tag, confirmed **above** the 22.22.3
      Angular CLI gate) → `nginx:1.31-alpine`, made non-root by hand (chowned cache/log/pid dirs,
      `USER nginx`, confirmed live via `docker exec … id` = uid 101, `docker top` shows the master
      process itself owned by that uid, not just the workers)
- [x] `nginx.conf` — SPA `try_files … /index.html` fallback, `/api/` reverse proxy, `/hubs/` with
      the full WebSocket upgrade header set
- [x] `compose.yaml` — `db` / `migrate` / `api` / `web`, healthcheck-gated startup
      (`service_healthy` → `service_completed_successfully` → default), named `mssql-data` volume
- [x] `.dockerignore` — `bin/`, `obj/`, `node_modules/`, `.angular/`, `dist/`, `.git/`, `tests/`,
      `*.md`, secrets
- [x] **Migrations applied in the container (D28) — option 2, the one-shot init service, as
      decided.** New `migrate` service built from `Dockerfile.migrate`, running a small console app
      at `docker/db-init/` (`Portfolio.DbInit`, referencing `Portfolio.Infrastructure` only — **zero
      changes under `src/`**, not part of `portfolio.slnx`) that calls
      `PortfolioDbContext.Database.MigrateAsync()` — the *same* migrations `dotnet ef database
      update` applies to SQLEXPRESS, not a separate schema. `api`'s `depends_on: migrate:
      condition: service_completed_successfully` means it never starts against an unmigrated
      database. `README.md` and the `container-docker` agent's completion checklist both corrected
      in this change — the checklist now points at `docker compose logs migrate`, not `api`, since
      the chiselled `api` image has no code path that could ever emit that line.
- [x] **App connects as a least-privilege login, not `sa` (D29)** — `migrate` is the *only*
      container ever handed `MSSQL_SA_PASSWORD`; it creates the `portfolio_app` SQL login + database
      user (`db_datareader` + `db_datawriter`, no DDL) and `api` connects as that. **Verified from
      the app's own live connection, not inferred from the env file**: `sys.dm_exec_sessions`
      queried directly against `db` while `api` was running showed both of its EF Core sessions
      (`program_name = 'EFCore/10.0.10 …'`) as `login_name = 'portfolio_app'`, `host_name` matching
      the `api` container's own hostname — and attempting `CREATE TABLE` as `portfolio_app` via
      `sqlcmd` was rejected with `Msg 262 … permission denied`, proving the DDL boundary is real,
      not just granted-and-unused. Both credentials documented in `.env.example` with an explicit
      "bootstrap only" vs "the app's own login" comment on each. Container connection string carries
      `Encrypt=True;TrustServerCertificate=True`.
- [x] Verified: `docker compose up -d` → `db` healthy, `migrate` exits `0`, `api`/`web` running, no
      restarts (`RestartCount: 0` on all three long-running services). App reachable at
      `http://localhost:8080` — confirmed via `curl` returning real Angular `index.html`, not an
      nginx default page, and `GET /api/assets` through the nginx proxy returning the seeded rows.
      SignalR: `POST /hubs/prices/negotiate` returns a real connection token, and a raw HTTP
      Upgrade request to `/hubs/prices?id=<token>` through nginx came back **`HTTP/1.1 101
      Switching Protocols`** with a server-computed `Sec-WebSocket-Accept` — proven at the protocol
      level, not inferred from devtools.
- [x] Verified: **a first `up` against a genuinely empty `mssql-data` volume creates the schema.**
      `docker volume rm portfolio_mssql-data` run deliberately before the first `up` (confirmed no
      volume existed beforehand), then after `up`: `sys.tables` in `Portfolio` lists all 8
      application tables plus `__EFMigrationsHistory`, and `__EFMigrationsHistory` lists all **4**
      real migrations (`InitialCreate` → `AddAssetCreatedAt`) — not a subset, not a stale copy.
- [x] Verified: `down` then `up` preserves data. A probe transaction (`POST /api/transactions`)
      was created, read back, the stack brought down with plain `docker compose down` (**no
      `-v`** — confirmed the named volume still existed via `docker volume ls` after `down`), then
      `up` again: the probe row was still there via `GET /api/transactions`, and `migrate` re-ran
      and exited `0` (idempotent no-op past the already-applied migrations, exactly as designed).
      Probe deleted afterward.

**One real defect found and fixed along the way, not in the register going in.** The plain
`aspnet:10.0-noble-chiseled` / `runtime:10.0-noble-chiseled` images set
`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true` by default. Under that setting,
`Microsoft.Data.SqlClient` cannot open a SQL Server connection **at all** — every attempt throws
`System.NotSupportedException: Globalization Invariant Mode is not supported.` before a single
query runs. First hit as `migrate` exiting `139` with that exception in its log on the very first
`up` attempt. Both `Dockerfile.migrate` and `Dockerfile.api` now pull the `-extra` chiselled
variant instead (ships ICU, still non-root by default), and the original `<InvariantGlobalization>
true</InvariantGlobalization>` that had been set in `docker/db-init/Portfolio.DbInit.csproj` —
which would have caused the exact same failure even on a fixed base image — was removed. Recorded
in `.claude/agents/container-docker.md` so the next Dockerfile in this repo doesn't rediscover it
the hard way.

**Found, but deliberately NOT fixed here — belongs to `backend-dotnet` — see D32.** CoinGecko
quote refresh 401'd against `pro-api.coingecko.com` for the whole session. Root cause is a
pre-existing one-line bug in `Portfolio.Infrastructure/DependencyInjection.cs`
(`options.ApiKey is null ? Keyless : Pro` — should be `string.IsNullOrWhiteSpace`), invisible in
every environment tried before now because `dotnet user-secrets` leaves an unset key genuinely
**absent**, while a compose `.env` with a blank `CoinGecko__ApiKey=` line hands the container a
**present-but-empty** string — and there turns out to be no way to make Compose omit a mapped
environment key based on the emptiness of its source value (tested directly: `${VAR:+…}`,
`${VAR:-}`, and the bare-name passthrough form all still emit `VAR=` into the container once `.env`
defines the key with `=` at all). Harmless — crypto quotes simply don't refresh, everything else is
unaffected — but real, and it is not a container-stack defect, so it was left as a drawback row
rather than patched by editing `src/Portfolio.Infrastructure` myself.

**What the platform swap did and did not change, in the end.** Windows Authentication was
confirmed to genuinely stay out of the container path — `ASPNETCORE_ENVIRONMENT=Production` is set
explicitly in `Dockerfile.api` so `appsettings.Development.json`'s `Trusted_Connection=True` string
is never even loaded, and `ConnectionStrings__Portfolio` under compose carries SQL auth only. nginx
forwards the WebSocket upgrade on `/hubs/` — confirmed with a real `101`, not assumed from the
config existing. `docker compose down -v` was never run against the real stack; the one deliberate
volume deletion for the empty-volume test used `docker volume rm portfolio_mssql-data` by name.

### ✅ Phase 12 — Asset management + theme toggle

**Added and completed 2026-08-07.** Browser-verified from the orchestrating terminal at 1568×675,
both themes. The backend item was done first, so the form surfaces the provider rules as server
validation rather than reimplementing them client-side — confirmed on the wire, see below.

**`backend-dotnet` — D23:**

- [x] `AssetService.CreateAsync` requires the provider field matching `QuoteProviderKind`:
      `ProviderSymbol` for `TwelveData`/`Yahoo`, `ProviderCoinId` for `CoinGecko`. `400` naming the
      offending field, never a `201` for an asset that can never be priced
- [ ] **Not done, deliberately:** verify the symbol actually resolves at the provider before
      accepting it. It spends a credit per asset creation and needs its own failure-mode design
      (what happens when the provider is simply down?). A typo like `APPL` for `AAPL` therefore
      still produces a permanently unpriceable asset — D23 closes the *malformed record* case, not
      the *wrong but well-formed symbol* case. See **D27**.
- [x] `PUT`/deactivate for an existing asset. Full-replace `PUT /api/assets/{id}` including
      `IsActive`; there is no separate deactivate route and no `DELETE`

**`frontend-angular` — D24, D25:**

- [x] Asset management page at `/assets` — list, create, deactivate **and reactivate**. The create
      form renders the **D24 design note**'s routing table as an always-visible table, swaps and
      clears the identifier field when the provider changes, and locks Crypto to CoinGecko
- [x] Warns on the credit cost whenever Twelve Data is selected — the ~78-credits-per-session figure
      and the "well under ten US symbols" ceiling are on screen, not in a code comment. The currency
      field states that only USD and SGD have FX coverage, and the identifier field states that a new
      asset has no price history until a backfill runs
- [x] **Theme toggle** in the toolbar — `data-theme` on `<html>`, persisted to `localStorage`, three
      states (Light / Dark / Follow OS), plus an inline `<head>` script so the first paint never
      flashes the wrong theme
- [x] Browser-verified that toggling repaints charts *and* that "Follow OS" genuinely returns to the
      media-query branch — see the measurements in the handoff log

**Live browser verification, 2026-08-07 (orchestrating terminal, Chrome at 1568×675):**

- **The server is genuinely the validator.** Submitting a Twelve Data asset with an empty identifier
  produced a real `POST` on the wire (confirmed via `performance.getEntriesByType('resource')`, not
  inferred) and the backend's own message — *"ProviderSymbol is required when QuoteProviderKind is
  TwelveData."* — rendered against the `providerSymbol` field. The wording is backend casing, so
  this is the 400 surfacing, not a client-side lookalike.
- **The theme toggle repaints charts completely.** Flipping to Light took gridline strokes
  `#2c2c2a`×6 → `#e1e0d9`×6 with **zero** stale dark strokes, and series/marker-ring colours moved
  too — all while the OS still preferred dark, so the explicit override really does win.
- **"Follow OS" is correct.** Selecting it **removed** the `data-theme` attribute *and* the
  `localStorage` key, and every token and stroke returned to the dark media-query values. It does
  not pin the last resolved value, which is the usual way this gets built wrong.
- **A full-page contrast sweep found 0 elements under 3:1** across 53 leaf elements (SVG excluded),
  so D14 has not regressed and the new amber `Close ·` caption is legible in dark mode.

### ⬜ Phase 11 — End-to-end verification

> ⚠️ **One of these six is now closed as a side effect of Phase 10; one is still blocked and
> cannot be helped by more local work.** Phase 10 landing on 2026-08-09 closes the container-DB
> migration-parity check below — it was verified as part of that phase, not deferred to this one.
> The credit-budget check still needs a **real NYSE trading session**, which no amount of local
> work manufactures (**D6**). The other four are reachable now; do not let a session quietly tick
> the D6 box without an actual trading day behind it.

- [ ] Enter a fractional crypto buy; confirm it persists and the gain/loss card recalculates
- [ ] `/stocks` and `/crypto` totals are fully independent
- [ ] Timestamp updates across a scheduled refresh with no page reload
- [ ] Z74 quote arrives in SGD and converts at the stored USD/SGD rate
- [ ] **Blocked (D6)** — Twelve Data credit use over one trading day lands well under 800. Needs a
      real trading day; every session so far has run with NYSE closed and spent 0 credits
- [x] **Closed 2026-08-09, as part of Phase 10.** Same migration set applies cleanly to both
      SQLEXPRESS and the container DB — not inferred, compared directly: the container's
      `__EFMigrationsHistory` lists all 4 real migrations
      (`InitialCreate`/`AddAssetQuoteProviderKind`/`AddSourceRefreshState`/`AddAssetCreatedAt`),
      an exact match against the `.cs` files under
      `src/Portfolio.Infrastructure/Persistence/Migrations/`, applied via the same
      `PortfolioDbContext.Database.MigrateAsync()` codepath EF Core always uses — there is only
      ever one migration set in this repo, the container now genuinely runs it too.

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
| D4 | ~~**SGX lunar holidays are not modelled**~~ **Closed 2026-08-08 — by making the data honest rather than by modelling the holidays, at the user's direction.** The calendar is *still wrong* on lunar holidays and deliberately always will be: a per-year table nobody refreshes rots into the same wrong answer it was added to fix. What changed is that a stale quote can no longer masquerade as a live price. **The fix was much smaller than this row's "Medium" estimate, because the data needed already existed and nothing read it** — all three providers already store the *provider's* own timestamp (`quote.Timestamp`, `meta.RegularMarketTime`, `entry.LastUpdatedAt`) and `PriceRefreshService` persists it verbatim rather than stamping "now"; `PortfolioSummaryService` simply labelled every stored quote `Live` regardless of its age. New `QuoteFreshness.Classify` reports a quote from an earlier session as `PriceSource.Close` carrying its own date, using **two conditions that cover each other's blind spot**: an exchange-local *date* comparison that never consults the holiday table (so it stays right on precisely the days the table is wrong), plus an `IsOpen` check for the hours after a session ends on a day the calendar does know about. Crypto is exempt by design — no session boundary to be stale relative to. Pinned by `QuoteStalenessTests`, which asserts the calendar's wrongness on **Chinese New Year 2026 (17 Feb)** as an explicit premise, and **4 of its 7 tests were confirmed to fail against the pre-fix behaviour**. Kept in full below. | 5 | Calendar reports SGX open on Chinese New Year, Vesak, Hari Raya and Deepavali. Yahoo is called anyway and returns the previous close, which is then stored with a fresh-looking timestamp — Z74 silently looks current on days it isn't. No credit cost (Yahoo is unmetered). NYSE holidays *are* fully rule-based. | Medium — needs a maintained per-year table or a holiday API. Deliberately not faked with an approximation. |
| D4a | ~~**A pushed SignalR quote bypassed the D4 fix entirely**~~ **Found and fixed 2026-08-08, in the same change.** Fixing the read path alone would have left D4 half-closed on the page where it shows most. `AssetDetailPage.isCloseSourced` read `quote() === undefined && …`, on the stated reasoning — written in the code — that *"a genuine SignalR push is always live"*. That is false in exactly the D4 case: on an unmodelled lunar holiday the refresh service polls anyway, Yahoo answers with the previous session's close, and `RefreshGroupAsync` broadcasts it like any other tick. So a push **overrode** the honest label rather than confirming it, leaving the 24px live price — the most prominent number on the page — as the one place the stale price still read as live. Fixed on the backend, not by teaching the browser exchange-session arithmetic: `QuoteUpdateNotification` gained `Source`, classified by the same `QuoteFreshness` rule as the read path. **A second, structural half was found while testing it**: the `Close ·` caption lived *inside* the snapshot-fallback branch of the template, so the push path had nowhere to render it even once the flag was right — the caption is now hoisted out of both branches. | 5, 9 | — | — |
| D5 | ~~Manual refresh is a no-op for stocks outside market hours~~ **Fixed 2026-07-31** (frontend messaging, Phase 7) | 5 | — | — |
| D6 | **The 5/60-minute cadence has never run over a real window** | 5 | Only single cycles and one 2-minute crypto interval have been observed live. The NYSE-open 5-minute cadence, the 60-minute closed cadence and an open→close transition are all unobserved, so "well under 800 credits/day" is still arithmetic rather than measurement. | Low, but needs a real trading day — belongs to Phase 11. |
| D7 | ~~**Two serializer configurations still exist**~~ **Fixed 2026-08-08 — both remedies this row offered, not just one.** New `PortfolioJsonSerialization.Apply` is the single definition, applied to both pipelines in `Program.cs`; it now sets `PropertyNamingPolicy` **explicitly** rather than inheriting it from two independent `JsonSerializerDefaults.Web` defaults, so the agreement is *stated* instead of coincidental. New `JsonSerializationParityTests` resolves the **actually-registered** options out of the running host's DI (not a hand-rolled copy of `Program.cs`) and serializes the same `PriceRefreshStatus` through both, asserting the JSON is byte-identical — a string comparison precisely so it catches divergences nobody thought to enumerate, plus a separate test pinning the encoding itself so both sides cannot drift to raw ints *together*. **All 3 confirmed to fail when the original Phase 5 bug is reintroduced on the hub side**, reproducing `"source":0` vs `"source":"TwelveData"` on the wire. Kept in full below. | 5 | The enum-as-int bug is fixed, but REST and SignalR agree only because their defaults happen to coincide (both camelCase). Changing a naming policy on one side, or adding a MessagePack protocol, reintroduces the same class of bug. The regression test only covers enums on the JSON protocol. | Low — assert the two configurations agree, or build both from one shared options factory. |
| D8 | ~~**`decimal(28,10)` over JSON is unproven in a JS client**~~ **Closed 2026-08-01 — measured, and the original premise was wrong** | 3 | Kept in full below, because what it actually measured is worth not rediscovering. | — |
| D9 | ~~Angular CLI's Node check is patched in `node_modules`~~ **Fixed 2026-07-31** — Node upgraded to 22.23.2, patch and `postinstall` hook deleted, pristine CLI gate confirmed restored | 7 | — | — |
| D10 | ~~**A gated provider vanishes from `PriceRefreshCycleResult.sources`**~~ **Closed 2026-08-07 — resolved as a doc correction, not a payload change, and the reasoning matters.** Emitting gated outcomes into `Sources` would make `outcomes.Count == 0` stop meaning "nothing happened", which is exactly what prevents a `RefreshRun` write and a SignalR broadcast on every closed-market poll tick — rewriting that heuristic safely was not the "Low" cost this row estimated. The DTO comments now describe what the API actually sends. The frontend's `nyseOpen`/`sgxOpen` derivation is untouched and remains authoritative. Kept in full below. | 5 | `SourceRefreshOutcome.Attempted` is documented as "false when the source's market was closed", but `RunCycleAsync` records that outcome to the status store and then `continue`s **without adding it to the returned list** — so a closed market yields no entry at all, not an `attempted: false` one. Any client reading the skipped set off `sources` gets nothing; the Phase 7 UI derives it from `nyseOpen`/`sgxOpen` instead, which is authoritative. Not a bug in behaviour, but the DTO's own doc comment describes a shape the API never emits. | Low — either add the gated outcome to `outcomes` or correct the doc comment. A `backend-dotnet` call. |
| D11 | ~~**`AssetClass` route/query binding is case-sensitive**~~ **Fixed 2026-08-07.** New `AssetClassRouteValue` (`IParsable`, case-insensitive `Enum.TryParse`) applied to the route segment *and* the query parameter **together**, per this row's own warning against fixing one alone. Verified live: `/api/portfolio/stock/summary`, `/StOcK/allocation`, `?assetClass=crypto` and `?assetClass=stock` all `200`; a garbage value still `400`s cleanly. Kept in full below. | 3, 6 | `/api/portfolio/stock/summary` returns **400**; only `/api/portfolio/Stock/summary` binds. Pre-existing, not a Phase 6 regression — `/api/assets?assetClass=stock` 400s on unmodified Phase 3 code too. Route *literals* are case-**in**sensitive, so `/api/portfolio/{Stock,stock}/annual-returns` both work; only the enum-bound segment is fussy. **The safe frontend rule is to send `Stock`/`Crypto` capitalised everywhere** — that form works on every route. Deliberately not special-cased on the new routes alone, which would create exactly the two-encodings-of-one-field drift D7 warns about. | Low — a custom binder or a `[FromRoute]` string parsed case-insensitively, applied to *both* the route and the Phase 3 query parameter together, never just one. |
| D12 | ~~**`PriceHistory` is never refreshed — the backfill service has no caller at all**~~ **FIXED 2026-08-07, and proven by watching the table grow rather than by a passing test.** `RunAsync` now takes a `RefreshTrigger` and writes a `RefreshRun` audit row; new `RunIfDueAsync` gates on `IMarketCalendar` (skips while NYSE is open) plus a once-per-day throttle; new `PriceBackfillBackgroundService` polls every 15 min; `POST /api/prices/backfill` is the manual trigger. **Live proof: MSFT went from 0 to 14 `PriceHistory` rows spanning 2026-07-20 → 2026-08-06**, genuinely past the frozen 07-24 window, and a second run inserted 0 — idempotency confirmed against real SQL Server. The background service also fired unprompted at API startup. ⚠️ Note what this does *not* do: an asset with **no transactions** is still skipped by design ("nothing to backfill against"), so Z74's history stays at 07-24 while nothing holds it — that is correct behaviour for a portfolio view, not a residual bug. Kept in full below. | 4, 6 | **Re-examined 2026-08-07 and it is materially worse than this row previously said.** The original text ("crypto's exclusion is unit-tested but never observed live") described a testing gap. The actual defect is structural: `IPriceBackfillService.RunAsync` is implemented and registered in DI, but **grepping `src/` finds no call site whatsoever** — no endpoint, no background service, no startup hook. `PriceBackfillService` is the *only* writer of `PriceHistory`, so in normal operation **that table never grows**. Confirmed against dev SQLEXPRESS: `PriceHistories` holds 5 rows each for AAPL and Z74 spanning `2026-07-20 → 2026-07-24`, written by a manual Phase 4 test, and unchanged **14 days later**. Consequences, all currently live: the cost-vs-market chart and **all annual returns** read only `PriceHistory`, so they are frozen at that 5-day window forever and silently look "current"; and **D20's recommended last-close fallback would read from this same frozen table**, so it would surface a two-week-old close. Not a rate-limit or market-hours issue — Twelve Data and Yahoo would both serve this data on request; nothing asks. | Low to fix, high value: add a `POST /api/prices/backfill` endpoint (bounded by the existing `MaxProviderCallsPerRun`), and/or run it on a daily schedule after each market's close. **This is now the real prerequisite for D20**, and it independently unblocks D13. A `backend-dotnet` call. |
| D13 | ~~**Annual returns have only ever run over 5 days of real data**~~ **Closed 2026-08-08 against genuinely multi-year real market data.** This row (and Phase 11) assumed it needed "a real holding period"; it did not — **closing D12 had quietly made it reachable**, since the backfill runs from an asset's own earliest trade date, so backdating a probe transaction manufactures real history. Probes at 2024-03-15 (AAPL) and 2024-05-20 (Z74) produced **601 + 560 `PriceHistory` rows and 704 `FxRate` rows spanning 2024-03-15 → 2026-08-07**, from **3 provider calls / ~2 Twelve Data credits** — Z74 rides Yahoo, which is unmetered, which is why the two-calendar case was affordable at all. Every gap this row names is now exercised: weekend gaps, two year boundaries (2024→2025→2026), and **genuinely divergent calendars** — the union is 619 dates, of which **59 are NYSE-only and 18 SGX-only**, so neither series is a subset of the other. **All three years were recomputed by hand from the raw per-asset series and reconcile to 0.0000 difference** (2024 `43.8333%`, 2025 `13.7391%`, 2026 `13.6369%`). The check that matters: 2025 contains a $1,001 mid-period deposit, and the naive simple return for that year is **+62.3340%** against TWR's **+13.7391%** — a 49-percentage-point gap, so "deposits are not reported as gains" is now measured on real data rather than argued from a unit test. Reconciling also **identified the implementation's exact cash-flow convention**: the flow is subtracted from the valuation on its attributed date (the two alternative conventions give 13.3352% and 24.9346%). Probe transactions deleted; the history and FX rows were **kept deliberately as genuine market data**, per the MSFT precedent. | 6 | Dev SQLEXPRESS holds `PriceHistory` for 2026-07-20→24 only, so the live TWR figure covered one partial week and the union-of-dates timeline never spanned a weekend gap, a year boundary, or two assets with divergent calendars. The *algorithm* is proven by the hand-computed two-year unit test and the cash-flow-alignment tests; the *assembly* of real multi-year inputs is not. | Low — needs a wider backfill or a real holding period. Belongs to Phase 11. |
| D14 | ~~**The live price is invisible in dark mode on every detail page**~~ **Fixed 2026-08-07 — browser-confirmed at 17.42:1** (was 1.01:1); sidenav/toolbar icons 13.49:1 (were 1.87:1); the full-page contrast sweep now finds **0** elements under 3:1 (was 4). Both root causes addressed: the two missing `--mat-sys-background`/`-on-background` overrides added, and `body { color-scheme: light }` deleted rather than left with a false comment. Kept in full below. | 7, 9 | `.detail__price` renders `rgb(26,27,31)` on `rgb(26,26,25)` — a contrast ratio of **1.01:1**. The 24px live price, the most prominent number on both `/stocks/:symbol` and `/crypto/:symbol`, cannot be read at all in dark mode; it is legible in light mode, which is why no static review caught it. **Root cause is two compounding bugs, and it is not in the Phase 9 component.** (1) `styles.scss`'s `:root` block repoints `--mat-sys-surface`/`-on-surface` but **not `--mat-sys-background`/`-on-background`**, which keep `mat.theme()`'s own `light-dark(#faf9fd, #121316)` / `light-dark(#1a1b1f, #e3e2e6)`. (2) `styles.scss` sets `body { color-scheme: light; }`, which forces every `light-dark()` inside `<body>` to the **light** branch regardless of OS preference — its own comment claims this "is overridden by ui.tokens.scss's own color-scheme rules", and that is **false**: ui.tokens sets `color-scheme` on `:root`/`html`, and `body`'s own declaration wins for body's subtree. `mat-sidenav-container` then paints `color: var(--mat-sys-on-background)` = near-black and that colour **inherits down the entire content tree**. Every element that sets its own colour is unaffected, which is why only the one element that doesn't — `.detail__price` — is visibly broken. Same mechanism dims the sidenav Crypto/Transactions icons and the toolbar refresh icon to **1.87:1** (Phase 7, pre-existing). | Low, but get the diagnosis right: adding a colour to `.detail__price` treats the symptom and leaves the inherited near-black waiting for the next element that omits one. Fix is to add the two missing `--mat-sys-*` overrides and delete or correct the `body { color-scheme: light }` rule. |
| D15 | ~~**The cost-vs-market chart's two end labels overlap illegibly**~~ **Fixed 2026-08-07 — browser-confirmed** against a deliberately harder case than the original: cost `$4,995.00` vs market `$4,995.30`, **30 cents apart**, where the original finding was nine dollars apart. Collision is detected relative to the visible value range (so it works for a $5 ANVL position and a $50k stock alike) and the labels are nudged 14px apart, greater value on top. Kept in full below. | 9 | When cost basis and market value are close at the right edge — `$689.33` vs `$680.32`, about nine dollars apart on a $700 axis — the two `endLabel`s render on top of each other as an unreadable blob. Predicted by the agent as an open risk; the fallback it named (legend + tooltip) does not rescue the rendered label, and near-equal cost and market value is the *normal* case for a recently-opened position, not an edge case. | Low — offset colliding labels, add leader lines, or drop `endLabel` and rely on the legend. |
| D16 | ~~**Charts never repaint when the theme changes**~~ **Fixed 2026-08-07 — browser-confirmed by the same measurement that found it.** `chart-theme.ts` now exports a `themeVersion` signal bumped by a `matchMedia` listener and a `MutationObserver` on `[data-theme]`; `readChartTokens()` reads it, so every chart's `computed()` re-runs. Measured on a live flip: gridline strokes went `#2c2c2a`×6 → `#e1e0d9`×6 with **no** stale dark stroke left, and series/marker-ring colours repainted too. ✅ **Both halves are now confirmed. The `matchMedia` (real OS flip) half was verified on 2026-08-08 by the user, in a visible focused window** — an agent-driven tab is always `visibilityState: "hidden"`, which suppresses the event and made this look broken. See **D31** for the full trap. Kept in full below. | 9 | `chart-theme.ts` resolves `--ui-*` off the live DOM once at construction and never re-resolves. Measured: after switching to light, `--ui-color-gridline` is `#e1e0d9` but the SVG still strokes the dark-mode `#2c2c2a` (×8) and `#1a1a19` (×2), leaving heavy dark gridlines on a light chart. Text fill happens to agree only because `--ui-color-on-surface-muted` is `#898781` in *both* themes — so the text proves nothing either way. Real-world trigger is an OS auto-dark flip with the app open; there is no in-app theme toggle yet, so it is not reachable by clicking today. | Low — a `matchMedia('(prefers-color-scheme: dark)')` listener (plus a `MutationObserver` on `[data-theme]`) forcing the chart-option `computed()` to re-evaluate. Worth doing before any theme toggle ships, or the toggle will look broken. |
| D17 | ~~**Portfolio totals treat a missing quote as $0 market value**~~ **Closed 2026-08-07 — and the "D20 largely closes it" assumption was checked rather than trusted, which was right, because it does not.** D20's close fallback removes the *common* case, but a holding with neither a quote nor any history (a newly added asset) still contributes 0. So `PortfolioSummaryDto` gained **`UnpricedHoldingsCount`**, and the overview renders an explicit caveat above the tiles rather than silently dropping holdings from a total. **Browser-verified against a deliberately constructed case** — a holding with no quote and no history gave `unpricedHoldingsCount: 1` and the banner read *"1 holding has no price yet — the totals below don't include its market value, not a loss."* ~~⚠️ **Residual:** `AllocationItemDto` carries no equivalent field, so a genuinely unpriced holding still shows as 0% in the allocation pie with no caveat.~~ **Residual closed 2026-08-08 — see D30.** Kept in full below. | 6, 9 | With AAPL holding a real $3,301 cost basis but no live quote, the stocks overview headline reads **−$3,302.35 · −82.76%**. The holdings *row* correctly says "Awaiting price", but the summary tiles above it silently count that holding's market value as zero, so the portfolio appears to have lost 82% when nothing happened. The agent recorded the narrow version of this (a portfolio where *every* holding is unpriced shows −100%); the realistic case is worse, because a *partly* priced portfolio looks precisely like a real crash rather than an obvious glitch. Originates in the backend: `totalMarketValueUsd` sums `marketValueUsd: 0`. | Medium — needs a decision, not just code: either exclude unpriced holdings from the totals and label the tile as partial, or surface an explicit "N holdings unpriced" caveat. A `backend-dotnet` + `frontend-angular` pair. |
| D18 | ~~**Material icon ligature names leak into the accessible text**~~ **Fixed 2026-08-07 — browser-confirmed.** The `<mat-icon>` now carries `aria-hidden="true"` inside a `role="img"` wrapper holding the `aria-label`. Measured accessible text went `arrow_upward+$218.09·+40.95%` → **`Up +$218.09+40.95%`** and `remove$0.00` → **`Unchanged $0.00`**. ~~⚠️ **Residual:** `aria-hidden` removes the element from the accessibility tree only — raw `textContent` still contains the ligature.~~ **Residual closed 2026-08-08.** The decorative `<mat-icon>` in `shared/gain-loss/` is now an inline SVG carrying Material's own 24dp arrow paths, so there is no icon-name text left to leak: `arrow_upward+$218.09·+40.95%` → `+$218.09·+40.95%`. The accessible name is unchanged (`role="img"` + `aria-label` on the wrapper, glyph `aria-hidden`), and `fill="currentColor"` keeps the arrow on the same gain/loss token with no second colour rule. **The spec had to be rewritten, not extended — its previous assertions (`expect(textContent).toContain('arrow_upward')`) were pinning the defect in place.** Kept in full below. | 7, 9 | The rendered text layer reads `arrow_downward -$1.35·-0.20%` and `remove $0.00` — the raw `<mat-icon>` ligature strings are exposed to assistive technology and to any text extraction. The *visual* colour-independence requirement is genuinely met (a real ↓/↑/— glyph plus a signed number), so this is an accessibility polish issue, not a failure of the "not colour alone" rule. | Low — `aria-hidden="true"` on the decorative `<mat-icon>`, with the direction carried by the existing `aria-label`. |
| D19 | ~~**The line chart's x-axis shows day-of-month only**~~ **Fixed 2026-08-07, but the first fix shipped a worse bug — see the D19a row below.** Ticks now format by range span (`≤45d` → "Jul 22", `≤370d` → "Jan 2026", `>370d` → "2024"). Browser-confirmed reading `Jul 20 … Jul 24` against API dates `2026-07-20 … 2026-07-24`. Kept in full below. | 9 |
| D19a | ~~**The D19 fix rendered the entire x-axis one day early at any positive UTC offset**~~ **Found and fixed 2026-08-07 in the same browser pass.** The agent formatted ticks with `timeZone: 'UTC'`, reasoning that the native `Date` parser reads `"YYYY-MM-DD"` as UTC midnight. True of `new Date("2026-07-20")` — **but the series passes those raw strings to a `type: 'time'` axis, so it is ECharts, not the native parser, that produces the timestamps the formatter receives, and ECharts parses that shape as _local_ midnight.** Formatting local-midnight in UTC subtracts the offset: in Asia/Singapore (UTC+8) a Jul 20–24 series labelled itself **Jul 19 … Jul 23**, while the tooltip — which reads the raw string — correctly said Jul 20. The axis and tooltip disagreed by a day on the same point. Fixed by formatting locally, so both ends of the pipeline agree. **Why it shipped green:** the spec called the formatter with `new Date('2026-07-22T00:00:00Z')` — an input shape no code path produces. The test now builds local-midnight inputs and was **confirmed to fail when the fix is reverted** (`expected 'Jul 19' to be 'Jul 20'`). ⚠️ **Invisible at zero or negative UTC offsets**, so a UTC CI container would never catch it. | 9 | Labels read `20 21 22 23 24` with no month or year anywhere on the chart. Unambiguous over a 5-day window; meaningless over the 1Y/All ranges the selector offers, where the same axis would repeat day numbers across months. | Low — format ticks by range span. Not observable until there is more than a week of history, which is also why D13 still matters. |
| D20 | ~~**No last-close fallback when a market is closed — a stored price is on disk and the UI says "Awaiting price"**~~ **Fixed 2026-08-07 via Option 1 (read-time fallback), the option the design note recommended.** No `PriceQuote` is ever written from a close, so D4's mistake is not repeated. `HoldingDto` gained `PriceSource` (`"Live"｜"Close"｜null`) and `priceAsOf` now carries **the close's own date**, never "now". Verified live: AAPL returns `priceSource: "Close"`, `priceAsOf: "2026-07-24T00:00:00+00:00"`, `currentPriceUsd: 333.019989` where it previously sent `null` / `marketValueUsd: 0`. **Browser-verified:** the detail header and holdings table both render an amber `Close · Fri 24 Jul` caption, visually distinct from a live price, and the genuinely-unpriced row still reads *Awaiting price*. Kept in full below. | 5, 6, 9 | **Requested 2026-08-01.** A US stock shows no price at all outside NYSE hours. AAPL's close of `333.019989` for 2026-07-24 is sitting in `PriceHistories` right now, while `/api/portfolio/Stock/summary` sends `currentPriceUsd: null`, `marketValueUsd: 0` and the UI renders *Awaiting price*. Over a weekend that means the whole US side of the portfolio reads as valueless for ~64 hours, every week. Cause is structural, not a bug: `PriceQuote` (live, written by the refresh service) and `PriceHistory` (daily closes, written only by `PriceBackfillService`) are separate tables, and **nothing reads the second when the first is empty**. Twelve Data is correctly never called on a closed market — that gate is deliberate and should stay, so the fix must not be "call the provider anyway". **This also largely resolves D17**: the −82.76% headline exists precisely because an unpriced holding contributes `0` to the total, and a last-close fallback gives it a real number. | Medium, and needs a decision first — see the design note below the table. Backend (`Portfolio.Application` summary/allocation assembly + DTO) plus a small frontend labelling change. |

| D21 | ~~**Stat-tile values break mid-number on the detail page**~~ **Fixed 2026-08-07 — browser-confirmed.** The real defect was `overflow-wrap: break-word` on a numeric value, so that is what was removed (`white-space: nowrap` now guarantees one line); the size question was then solved properly with a container query — `clamp(var(--ui-font-size-lg), 9cqi, var(--ui-font-size-2xl))` reads the **tile's own** width, so one rule serves both the wide overview grid and the narrow detail grid. The `--ui-layout-tile-min-width-sm` token was **deleted** rather than left as a second number to keep in sync (tree re-grepped: no orphaned references). Verified at the exact failing case: `$4,664.00`, `$4,995.30` and `$331.30` all render on one line in the detail page's 4-up grid. Ellipsis plus a `title` binding is the last-resort net for a seven-figure value. Kept in full below. | 9 | **Found 2026-08-07 by looking at rendered output.** The gain/loss card's tiles render `$4,995.00` across **two lines**, breaking between the `0` and the final `0` — a monetary figure split mid-digit. Measured: the value box is **128.9px** wide with a **32px** font and `overflow-wrap: break-word`, so a 9-character amount cannot fit on one line. The **overview** page's tiles are wider and unaffected — this is the detail page's narrower 4-up grid only. Not caught by the 2026-08-01 pass because its probe amounts were shorter. Real-world trigger is any holding at or above $1,000, i.e. most of them. | Low, but it is a token decision, not a one-liner: either widen `--ui-layout-tile-min-width`, clamp the value font size, or set `overflow-wrap: normal` and let the tile scroll/ellipsize. Breaking a number mid-digit is never right, so `break-word` on a numeric value is the actual defect. A `frontend-angular` call. |
| D22 | ~~**`transaction-form.dialog.spec.ts` is intermittently failing**~~ **Fixed 2026-08-07 — root-caused, not retried, and the cause was nowhere near the failing test.** ECharts calls `HTMLCanvasElement.getContext('2d')` on an *offscreen* canvas to measure text width **even in SVG mode** (`zrender/core/platform.js`). Without the native `canvas` package, jsdom returns `null` on every call *and* constructs a fresh `Error` through the virtual console each time — so every axis tick, legend row and bar label in every chart spec paid a stack-trace-plus-console-write tax. Spread across three chart-heavy specs running concurrently, that starved the runner enough to push the transaction dialog's first test (which pays one-time JIT compilation for dialog + datepicker + select) past vitest's 5s timeout. **The timeout was a resource-starvation symptom, not a bug in that test or component.** Fixed with a cheap real `measureText` stub in `test-setup.ts`, which zrender caches module-level so one stub covers the whole run. **Proven by 5 consecutive full-suite runs, all 157/157, all exit 0** — against a baseline that failed roughly one run in two. Kept in full below. | 8 | **Observed 2026-08-07 across four consecutive full runs: 147/147, then 1 failed, then 147/147, then 1 failed** — roughly one run in two. The failing case is *"blocks submit and marks controls touched when required fields are empty"*. Pre-existing and unrelated to the D14–D19 work (nothing in that change set touches transactions); the `frontend-angular` agent independently observed the same flakiness and checked it against a stashed baseline. A suite that fails half the time is a suite nobody will trust, and it will mask a real regression the first time one lands here. | Low–medium — needs diagnosis rather than a retry wrapper. Likely a zoneless-harness timing assumption (the same class of issue as the `whenStable()` deadlock recorded in the Phase 9 handoff), not a genuine app defect. |

| D23 | ~~**`POST /api/assets` will happily create an asset that can never be priced**~~ **Fixed 2026-08-07.** `CreateAsync` and a new `UpdateAsync` share a `ValidateCore` requiring `ProviderSymbol` for `TwelveData`/`Yahoo` and `ProviderCoinId` for `CoinGecko`, rejecting with a `400` keyed on **`providerSymbol`** / **`providerCoinId`** and a message that teaches the rule (*"the coin id, e.g. `ethereum` — not the ticker"*). `PUT /api/assets/{id}` added for deactivate/reactivate. All verified live. ⚠️ **The typo case is explicitly NOT closed** — `APPL` for `AAPL` is well-formed and still accepted, producing the same permanent silence. See **D27**. Kept in full below. | 3 | **Found 2026-08-07, answering "what if I want to add my own stocks?"** The endpoint exists and works, but `AssetService.CreateAsync` validates only that `Symbol`, `Name` and a 3-letter `Currency` are present and the symbol is unique. It does **not** check that the provider routing fields are coherent: a `QuoteProviderKind.TwelveData` or `.Yahoo` asset with a null `ProviderSymbol`, or a `.CoinGecko` asset with a null `ProviderCoinId`, is accepted with a `201`. Since `QuoteProviderKind` is the dispatch key, such an asset is **silently unpriceable forever** — it shows *Awaiting price* with no indication that the cause is a malformed record rather than a closed market. Nor is the symbol verified against the provider, so a typo (`APPL` for `AAPL`) produces the same permanent silence. | Low — require the provider field matching the chosen `QuoteProviderKind`, and reject the mismatch with a `400` naming the field. A live symbol-resolution check is a bigger, optional step. `backend-dotnet`. |
| D24 | ~~**There is no UI for adding an asset — the six seeded ones are all you get**~~ **Fixed 2026-08-07 — browser-verified.** New `/assets` page: list with active/inactive badges, create dialog, deactivate **and** reactivate via full-replace `PUT`, optimistic with rollback. The routing table renders as an always-visible table in the dialog; the identifier field swaps and clears when the provider changes; Crypto locks to CoinGecko. The Twelve Data credit ceiling, the USD/SGD-only FX limit and the "no history until a backfill runs" caveat are all on screen. **Server validation genuinely round-trips** — a real `POST` was observed on the wire returning the backend's own message onto the named field, so the rules are not reimplemented client-side. Kept in full below. | 7, 8, 9 | **Found 2026-08-07.** `api-routes.ts` exposes `assets` but the frontend only ever `GET`s it, to populate the transaction form's dropdown; there is no create/edit/deactivate screen anywhere. Adding a holding therefore means hand-crafting a `POST /api/assets` with the right `QuoteProviderKind` + provider id — which also means knowing the routing rules that are currently documented only in this file (Twelve Data cannot serve SGX, so SGX symbols need Yahoo with a `.SI` suffix; CoinGecko wants the coin **id** `ethereum`, not the ticker `ETH`). For a single-user personal tracker that is a real ceiling on usefulness, not a cosmetic gap. | Medium — a form plus an asset-management page. Worth doing **after** D23, so the UI can surface the provider rules as validation rather than reimplementing them client-side. See the design note below. |
| D25 | ~~**No in-app light/dark toggle — the app follows the OS only**~~ **Fixed 2026-08-07 — browser-verified by measurement, not by eye.** Toolbar toggle with three states; `ThemeStore` writes `data-theme` and persists to `localStorage`. **"Follow OS" removes the attribute AND the storage key**, returning to the media-query branch — confirmed live, and it is the failure mode this row predicted. Flipping to Light took gridline strokes `#2c2c2a`×6 → `#e1e0d9`×6 with zero stale strokes while the OS still preferred dark, so the D16 repaint listener does its job and the override beats the OS. Kept in full below. | 7 | **Found 2026-08-07.** All the plumbing exists and is complete: `ui.tokens.scss` defines `:root[data-theme='dark']` / `[data-theme='light']` overrides that beat the OS preference **in both directions**, and as of the D16 fix `chart-theme.ts` actively listens for `[data-theme]` changes via `MutationObserver` and repaints. But **nothing in the app ever sets the attribute** — grepping the whole `src/app` tree finds `data-theme` only in stylesheets and in the listener. It was only reachable during verification by setting it from devtools by hand. So the design system's most visible affordance is built, tested, and unreachable. | Low — a toolbar toggle writing `data-theme` on `<html>` and persisting to `localStorage`, with "follow OS" as a third state. The D16 fix was the hard prerequisite and it has landed, so the toggle will not look broken. `frontend-angular`. |

| D26 | ~~**The backfill reported provider failures as budget skips**~~ **Found and fixed 2026-08-07, in the same session that introduced the endpoint.** `PriceBackfillService.RunAsync` appended three distinct outcomes — budget exhausted, provider threw, provider returned `Success == false` — to one list, surfaced through the DTO as **`AssetsSkippedForBudget`**. Found by *calling* the new endpoint rather than reading it: the payload said `assetsSkippedForBudget: ["AAPL"]` with `providerCallsUsed: 1` against a budget of **20**. AAPL had actually failed a Twelve Data request. **Why this mattered more than it looks:** that endpoint is now the thing an operator hits to ask why history is not advancing, and it answered "budget" — whose obvious remedy, raising `MaxProviderCallsPerRun`, changes nothing. That is D12's own failure mode (history silently not growing behind a clean report) reproduced at the reporting layer, and it is the same family as D10 except the field *name* was the lie, so a comment fix would not have been enough. Split into `AssetsSkippedForBudget` (budget guard only), `AssetsFailed` (`Symbol` + provider error text) and `AssetsSkippedTodayNotClosed`. **Also removed a guaranteed-wasted credit:** an asset whose earliest trade date is today can never return a range, so it is now short-circuited *before* the call — verified live, `providerCallsUsed` went 1 → **0**. | 4 | Fixed on the day it was introduced; the pre-fix payload is quoted in full above. | — |
| D27 | ~~**A well-formed but wrong provider symbol is still permanently silent**~~ **Mitigated 2026-08-08 via the cheap path this row itself named — no provider call, no credit.** The symbol is still not verified against the provider (that decision stands unchanged, for the reasons below), but the *symptom* — no way to tell a wrong record from a closed market — is addressed. `Asset` gained `CreatedAt` (migration `20260808150505_AddAssetCreatedAt`, applied and verified against real SQL Server); `AssetDto` gained `CreatedAt` + `HasEverBeenPriced`, the latter true if **either** a `PriceQuote` or any `PriceHistory` row exists. The `/assets` page renders the two states differently: *"No price yet — waiting for the next refresh"* for a new asset, escalating to an amber *"No price in N days — check this identifier"* past a per-provider threshold. **The thresholds are the interesting part and are not arbitrary: 3 days for TwelveData/Yahoo, 1 day for CoinGecko.** A US stock added on a Friday evening cannot be priced until Monday — the refresh service correctly never polls a closed market — so ~65 hours of silence is *right*, and warning at 1 day would fire on every stock added over a weekend, which is exactly how a warning gets trained into being ignored. Crypto has no such excuse (unmetered, ungated, polled every 2 min). Pinned by 6 frontend tests including the weekend case, and 5 backend ones. Two deliberate refinements: a **deactivated** asset says nothing (it is excluded from refresh cycles, so its silence is meaningless), and `UpdateAsync` does not reset `CreatedAt` (a rename must not erase the clock the hint measures). ⚠️ **Still not closed, by design:** the hint points at the record, it does not prove it wrong — a delisted symbol looks identical. Kept in full below. | 3, 12 | **Split out of D23 on 2026-08-07 rather than left implied.** D23 now rejects a *malformed* record (a Twelve Data asset with no `ProviderSymbol`). It does not, and cannot cheaply, reject a **well-formed but wrong** one: `APPL` for `AAPL`, or a CoinGecko id that does not exist. Such an asset is accepted with a `201`, renders *Awaiting price* forever, and gives no hint that the record rather than the market is the cause — the exact user-visible symptom D23 was written to eliminate, just reached by a different route. Deliberately not fixed: a live symbol-resolution check spends a credit per asset creation and needs its own failure-mode design (what should happen when the provider is simply down — reject a legitimate asset, or accept an illegitimate one?). | Medium — a provider `resolve`/`quote` probe at create time, plus a decision about the provider-unavailable path. Cheapest partial mitigation is a "no price seen yet since creation" hint on the asset row, which needs no provider call at all. `backend-dotnet`. |
| D30 | ~~**The allocation pie had no unpriced caveat**~~ **Closed 2026-08-08.** The D17 residual, split out and fixed. `AllocationItemDto` gained `HasPrice` and `PortfolioAllocationDto` gained `UnpricedHoldingsCount` (carried on the allocation payload itself, not only the summary's, so a caller fetching just this endpoint can still tell the pie is partial). The legend now renders **"No price yet"** in place of the `0.0% / $0.00` pair, because a `0%` that means *ignorance* must not look identical to a `0%` that means a genuinely tiny position. **Scope worth recording: this only ever affected the market-value pie.** Cost basis comes from the transactions and is known for every holding whether or not a quote arrived, so the cost-basis pie had nothing to caveat — the `unpriced` flag is hardcoded false on that path rather than computed. `backend-dotnet` + `frontend-angular`. | 6, 9 | — | — |
| D31 | ~~**A real OS dark-mode flip still cannot be confirmed**~~ **Closed 2026-08-08 by the user, in the visible window the agent could not obtain — and the confound was the whole story.** With the theme toggle on "Follow OS" and the app in a normal focused window, a real Windows theme change repaints the chart gridlines correctly. **So D16 is genuinely fixed on both paths: the `matchMedia` half works, and every symptom the agent measured was an artefact of the harness.** ⚠️ **Method note worth keeping, because it will bite again:** a tab driven through the Chrome extension is `document.visibilityState === "hidden"` for its whole life — screenshots do not activate it — and Chrome withholds `prefers-color-scheme` `change` events from hidden tabs while *still* re-evaluating `matchMedia(...).matches` and repainting CSS on read. That combination is actively misleading: it looks exactly like a listener that is wired up wrong, and it produced a false positive here that took a registry flip, a `WM_SETTINGCHANGE` broadcast and two listener APIs to *fail* to explain. **Before concluding anything about visibility-gated browser behaviour from an extension-driven tab, check `document.visibilityState` first.** Distinction kept honest for the record: the user confirmed this at the rendered level (the gridlines look right), not with the scripted stroke census the agent used — which is the correct level of evidence for the claim, since the claim is about what a person sees. Kept in full below. | 9 | **Attempted on 2026-08-08 and the result was inconclusive, which is why this was a row rather than a tick.** Method: put the app in genuine "Follow OS" state (`data-theme` absent, `localStorage` key removed — verified, not assumed), census every chart SVG stroke, then flip the **actual Windows theme** via `HKCU:\…\Themes\Personalize` — twice, the second time with a proper `WM_SETTINGCHANGE`/`ImmersiveColorSet` broadcast, since a bare registry write does not notify running apps. **What was measured: the OS change genuinely reaches the page** — `matchMedia('(prefers-color-scheme: dark)').matches` flips, `--ui-color-gridline` flips `#2c2c2a` ↔ `#e1e0d9`, and the body background inverts. **But the chart's SVG strokes did not move in step**, sitting one flip behind at the previous theme's values (e.g. 8 gridline strokes still `#2c2c2a` on a fully light page — the exact D16 symptom, visually confirmed as dark gridlines on a light card). **And the `change` event fired ZERO times** across both flips, measured with a listener registered directly on a fresh `MediaQueryList`, which no application code can suppress. ⚠️ **The confound, stated plainly rather than buried:** the tab was `document.visibilityState === "hidden"` throughout, because the Chrome extension drives tabs without activating them and a screenshot does not activate them either. Chrome defers work in hidden tabs, so the missing events may be an artefact of the harness rather than a defect. **Do not record D16 as verified, and do not record it as broken** — neither is established. | Low, and it needs a human for ~30 seconds, not an agent: open the app in a **focused, visible** window with the theme toggle on "Follow OS", change the Windows app theme, and look at whether the chart gridlines repaint immediately. If they lag, the `matchMedia` listener needs replacing with something that also re-checks on `visibilitychange`/focus. `frontend-angular`. |
| D28 | ~~**Nothing applies EF Core migrations — the container stack would start against an empty database**~~ **Closed 2026-08-09 via option 2, the one-shot init service, exactly as this row's own D29 coupling note recommended.** New `migrate` compose service (`Dockerfile.migrate`, a tiny console app at `docker/db-init/Portfolio.DbInit` referencing `Portfolio.Infrastructure` only — **zero changes under `src/`**) runs `PortfolioDbContext.Database.MigrateAsync()` and exits; `api`'s `depends_on: migrate: condition: service_completed_successfully` means it never starts against an unmigrated database. **Proven against a genuinely empty `mssql-data` volume, not merely a pre-populated one**: `docker volume rm` run first, confirmed absent, then `docker compose up -d` — `__EFMigrationsHistory` came back with all 4 real migrations, an exact match to the files under `src/Portfolio.Infrastructure/Persistence/Migrations/`. `README.md` and the `container-docker` agent's own completion checklist — both named in this row as currently lying — are fixed in the same change; the checklist now points at `docker compose logs migrate`, since the chiselled `api` image has no code path that could ever emit a migrations-applied line. Kept in full below. | 10 | **Found 2026-08-08 while answering "will the container update when I change code?"** Grepped the whole of `src/` for `Migrate`, `MigrateAsync` and `EnsureCreated`: **zero hits**. `Program.cs` is 46 lines and never touches `Database`. The only `EnsureCreatedAsync` anywhere is `PrecisionRoundTripTests.cs:38`, in the integration tests. Locally this is invisible and harmless — the schema exists because `dotnet ef database update` is run **by hand** against SQLEXPRESS, and it has been. **In a container there is no hand to run it.** The API image is `aspnet:10.0-noble-chiseled`, which ships no SDK and therefore no `dotnet ef`; the `db` service is reachable only from inside the compose network; and the `mssql-data` volume starts empty on a first `up`. So `docker compose up -d` brings up an API talking to a database with no tables, which surfaces as a runtime exception on the first request rather than a clear startup failure. **Two things already written down assume this is solved and it is not:** `README.md` claims "Both use the same EF Core migrations, so the schema is identical either way", and `.claude/agents/container-docker.md`'s completion checklist says to confirm `docker compose logs api` shows migrations applied — no code path can produce that log line. Note this is *not* the same as the rebuild question it was found next to: rebuilding an image never touches the database, because the volume deliberately survives. A schema change therefore needs its own step whatever the image does. | Medium, and it is a **decision before it is code** — the three options differ in blast radius, so Phase 10 must pick one deliberately rather than reach for the first. (1) `await db.Database.MigrateAsync()` in a startup scope: simplest, keeps image and schema in step automatically, but every API instance races to migrate on boot and a bad migration takes the app down with it. (2) A one-shot init service in `compose.yaml` that runs migrations and exits, with `api` gated on its completion: keeps the concern out of the app, costs an extra service and an SDK image. (3) `dotnet ef migrations bundle` — a self-contained executable copied into the image and run before the app: no SDK in the runtime image, but a build step to remember. **Whichever is chosen, fix `README.md` and the agent file's checklist in the same change**, since both currently describe behaviour that does not exist. ⚠️ **Decide this together with D29 — the two are coupled, and picking (1) forecloses D29.** Migrations need DDL rights the running app has no business holding permanently; options (2) and (3) let the migration step hold them while the app does not, so the privilege separation falls out for free. `backend-dotnet` + `container-docker`. |
| D29 | ~~**The container app would connect to SQL Server as `sa`**~~ **Closed 2026-08-09, in the same change as D28 — cheap exactly because D28 landed as an init service.** `migrate` is the only container ever handed `MSSQL_SA_PASSWORD`; it creates a `portfolio_app` SQL login + database user with `db_datareader`/`db_datawriter` only, and `api` connects as that via `ConnectionStrings__Portfolio` (`Encrypt=True;TrustServerCertificate=True`, as this row specified). **Verified from the app's own live connection, not the env file**: `sys.dm_exec_sessions` queried directly against `db` while `api` was running showed both of its EF Core sessions (`program_name = 'EFCore/10.0.10 …'`, `host_name` matching the `api` container's own hostname) as `login_name = 'portfolio_app'`; a `CREATE TABLE` attempted as `portfolio_app` via `sqlcmd` was rejected with `Msg 262 … permission denied`, proving the DDL boundary holds rather than merely being granted-and-never-tested. Both credentials documented in `.env.example`, each labelled which is bootstrap-only and which the app actually uses. Kept in full below. | 10 | **Found 2026-08-08, answering "is Windows Auth still the right choice under Docker?"** The answer for *local* dev is yes and nothing should change — `appsettings.Development.json:9` uses `Trusted_Connection=True`, which stores no password anywhere and is the more secure option wherever it works. The container has no such choice: a Linux container has no Windows identity or Kerberos ticket, so it must use SQL auth. That much is the existing, correct dual-connection-string design. **The gap is which SQL login it uses.** `.env.example` defines exactly one credential — `MSSQL_SA_PASSWORD` — so the implied `ConnectionStrings__Portfolio` connects the API as **`sa`**, a sysadmin over the whole instance. For a single-user local tracker that is not an emergency, but it is gratuitous: a compromised or merely buggy API gets `DROP DATABASE` and every other database on the instance along with it, when all it needs is to read and write six tables. Worth fixing *while the stack is being written* rather than retrofitted, because it costs almost nothing at authoring time. ⚠️ **Coupled to D28 and the coupling runs one way**: this is only cheaply achievable if migrations run as a *separate* step (D28 options 2 or 3), since a startup `MigrateAsync` (option 1) forces the app's own login to hold DDL rights permanently and there is then no meaningful privilege to separate. Choose D28 first, with this row in view. | Low **if D28 lands as an init service or a bundle**, otherwise mostly moot. Keep `MSSQL_SA_PASSWORD` for container **bootstrap only**; add a `portfolio_app` login + user created during db init, grant it `db_datareader` + `db_datawriter` (plus DDL to the *migration* identity only), and point `ConnectionStrings__Portfolio` at it. Add the new credential to `.env.example` with a comment saying which of the two is for bootstrap and which is for the app, or the next person will reuse the wrong one. Also note the container string needs `Encrypt=True;TrustServerCertificate=True` — SqlClient 6.x defaults `Encrypt=true` and the `mssql/server` image serves a self-signed certificate, so omitting it fails with a certificate-chain error that reads like a networking fault. `container-docker`. |
| D32 | ~~**CoinGecko routes to the paid Pro API and 401s when `CoinGecko__ApiKey` is present-but-empty, which is exactly the shape a compose `.env` produces for an unset key**~~ **Closed 2026-08-09.** New `CoinGeckoOptions.HasApiKey` (`!string.IsNullOrWhiteSpace(ApiKey)`, `[MemberNotNullWhen(true, nameof(ApiKey))]`) is now the single source of truth for configured-vs-absent, replacing every bare `ApiKey is null`/`is not null` check found in the file: the base-URL routing and the `x-cg-pro-api-key` header in `DependencyInjection.cs`, and the keyless-365-day clamp in `CoinGeckoQuoteProvider.ClampToProviderWindow`. The clamp site was not named in the original row and would have stayed broken (an empty key would have wrongly lifted the 365-day window) had the sweep only fixed the one line quoted in the finding — grepped the whole tree for the pattern rather than trusting the one call site. **10 new tests** (6 pinning `HasApiKey` directly against null/empty/whitespace-only/real-value, 1 pinning the clamp specifically, plus 3 more below for the coupled false-success fix), all confirmed to fail against the pre-fix `ApiKey is not null` check. **Verified live against the real Docker stack, not just unit tests**: rebuilt and redeployed the `api` image (`docker compose build api && docker compose up -d api`, `.env`'s blank `CoinGecko__ApiKey=` line untouched), and `GET /api/prices/status` went from `{"source":"CoinGecko","lastRunSuccess":true,"lastError":"CoinGecko returned HTTP 401.","symbolsRefreshed":0}` before the rebuild to `{"source":"CoinGecko","lastRunSuccess":true,"lastError":null,"symbolsRefreshed":3}` after it — a genuine 200 from the free `api.coingecko.com`, all three coins priced, confirmed via a resilience-pipeline log line (`Result: '200'`) rather than assumed. **0 Twelve Data credits spent** confirming this: it was a Sunday, NYSE/SGX gated all session, `TwelveData.lastAttemptedAt` stayed `null` throughout. `backend-dotnet`. | 4, 10 | Fixed on the day this row was found; the pre-fix payload and root cause are quoted in full above. | — |
| D33 | ~~**A refresh cycle where every symbol in a batch failed still reported `LastRunSuccess: true`**~~ **Closed 2026-08-09, and it is the same family as D10/D26 — the reporting layer telling a cleaner story than reality, this time one field deep in the status payload rather than a whole outcome list.** Root cause in `PriceRefreshService.RefreshGroupAsync`: the returned `SourceRefreshOutcome.Success` was hardcoded `true` whenever the *HTTP call itself* didn't throw, on reasoning that was half right — Twelve Data nests a per-symbol error inside an otherwise-successful batch, so a **single** bad symbol must not fail the whole provider. But the same code path also covers the case where the batch call returns 200/401 with **every** symbol individually failed (CoinGecko's D32 401, before that was fixed, produced exactly this), and `Success: true` there is simply wrong — the cycle fetched nothing. **Deliberate rule, written into the code as a comment, not just this row**: `Success = succeeded > 0`. Zero successes out of a non-empty batch is a failure; one or more successes is not, even with `Error` populated from a sibling's failure — partial success stays success, all-fail becomes failure. `PriceRefreshStatusStore.RecordOutcomeAsync` already had the right shape for this (only advances `LastSuccessAt` when `outcome.Success`, only touches `LastRunSuccess`/`LastSuccessAt` at all when `outcome.Attempted`) — the defect was entirely in what `Success` was set to, not in how it was persisted. A gated (market-closed) cycle was already correctly kept out of this by D10's own outcome, unaffected by this change. **2 new tests**, both confirmed to fail against the pre-fix hardcoded `true`: one reproducing the live CoinGecko-401-for-every-coin shape end to end through `PriceRefreshService` and `PriceRefreshStatusStore.GetSnapshotAsync` (asserts `LastRunSuccess: false` and `LastSuccessAt` does **not** advance), one for the genuine partial case (2 Yahoo symbols in one batch, one succeeds one fails, asserts `LastRunSuccess: true` — pinning that this fix does not overcorrect into flagging every partial batch as a failure). **Verified live**: the same Docker-stack rebuild that closed D32 also flipped `GET /api/prices/status`'s CoinGecko entry from the self-contradictory `lastRunSuccess: true` / `lastError: "CoinGecko returned HTTP 401." ` / `symbolsRefreshed: 0` to an honest `lastRunSuccess: true` / `lastError: null` / `symbolsRefreshed: 3` once D32 made the underlying call actually succeed — the false-success shape is gone, not merely retested. `backend-dotnet`. | 5, 11 | **Found 2026-08-09 while verifying D32 against the live Docker stack, before either fix landed.** `GET /api/prices/status` returned, for the cycle that produced D32's 401: `{"source":"CoinGecko","lastAttemptedAt":"...","lastSuccessAt":"...","lastRunSuccess":true,"lastError":"CoinGecko returned HTTP 401.","symbolsRefreshed":0}` — a run that fetched zero symbols and recorded an error, reporting itself successful and advancing `lastSuccessAt` as if it had worked. The `/assets` page's D27 escalation (see D34) reads exactly this signal, so the false success also fed directly into that defect: a provider that had never actually worked still looked, from the status endpoint, like it had just succeeded. | Low — the fix was one field's derivation; the actual work was writing down the partial-vs-total-failure rule deliberately and pinning both halves with tests. `backend-dotnet`. |
| D34 | ~~**The D27 escalated "check this identifier" warning fires on a fresh database with no evidence the identifier is wrong**~~ **Closed 2026-08-09, decision made by the requester rather than re-litigated here: gate the escalation on the *provider*, not the individual asset.** Measured live on the container DB: all six seeded assets carry `CreatedAt = 2026-07-26` because `HasData` seeds a static value, and `PriceQuotes`/`PriceHistories` were both empty on a database created the day before — so `HasEverBeenPriced` read false and the age read as 14 days past the 3-day/1-day thresholds for every asset, TwelveData/Yahoo/CoinGecko alike, regardless of whether their identifiers were right. New `AssetDto.ProviderHasEverSucceeded` — true when `SourceRefreshStates.LastSuccessAt` is non-null for that asset's `QuoteProviderKind`, **queried across every asset sharing the provider, not only the one in question** — is the gate: an asset's own silence carries no evidence about its identifier if the provider backing it has never once succeeded for *any* asset in this database, so there is nothing yet to escalate on. **This row was explicitly sequenced after D33, per the task's own instruction, because CoinGecko's `LastSuccessAt` was being stamped on the false-success 401 cycle before that fix landed — the gate would have read "has succeeded" and escalated anyway, closing this defect on paper while leaving it open in practice.** Existing `HasEverBeenPriced` semantics, its 5 backend + 6 frontend pinning tests, the deactivated-asset silence, and `UpdateAsync` not resetting `CreatedAt` are all unchanged and unregressed — this is a new field alongside the old one, not a replacement, and none of D27's existing tests encoded the old (wrong) behaviour, so none needed rewriting. **7 new backend tests**, 4 of them confirmed to fail with `ProviderHasEverSucceeded` hardcoded false at the two read paths (`ListAsync`/`GetByIdAsync`): a fresh database reports it false; a *sibling* asset succeeding on the same provider flips a still-never-priced asset to true (the core fix — the gate is per-provider, not per-asset); two different providers stay isolated from each other; a provider whose *most recent* attempt failed but which succeeded earlier still reads true (`LastSuccessAt` is "ever", not "most recent" — deliberately not undone by a later transient outage). `CreateAsync`/`UpdateAsync` also thread the field through, confirmed by 2 more tests. **Verified live**: the Docker-stack rebuild that closed D32/D33 also flipped `GET /api/assets` from every asset reading `providerHasEverSucceeded: false` to `ETH`/`AMP`/`ANVL` (CoinGecko, now genuinely succeeding) reading `true` while `AAPL`/`MSFT`/`Z74` (TwelveData/Yahoo, correctly never attempted — Sunday, both markets gated all session) stayed `false`, which is the exactly-correct state: no false escalation on the stocks, and the crypto assets both have real prices *and* would no longer need the gate anyway. ~~⚠️ This closes the backend half only.~~ **Frontend half closed 2026-08-09, same day.** `AssetDto` in `models.ts` gained `providerHasEverSucceeded: boolean` (non-optional, mirroring the backend field exactly), and `asset-management.page.ts`'s `unpricedState()` now computes `suspicious` as `asset.providerHasEverSucceeded && days >= STALE_AFTER_DAYS[...]` — a third suppression term alongside the existing `hasEverBeenPriced`/`!isActive` checks, ANDed in rather than a fourth early return, so the asset still gets the calm "no price yet" state (not silence) while its provider has never once worked. **2 new frontend tests**, one confirmed to FAIL against the pre-fix logic by temporarily reverting the AND to a bare day-threshold check: the exact D34 reproduction case (created 14 days ago, never priced, `providerHasEverSucceeded: false`) rendered the amber "check this identifier" text before the fix and the calm "waiting for the next refresh" text after; a paired test confirms the escalation still fires once a sibling proves the provider works (`providerHasEverSucceeded: true`), so this is a gate, not a blanket suppression. All 6 existing D27 frontend tests needed no rewriting — their fixtures already set `providerHasEverSucceeded: true` via the shared `AAPL` fixture, so the weekend-threshold and identifier-flag cases are unaffected, exactly as predicted going in. `ng build` clean, `ng test` **196/196 across 29 files** (was 194/194 before this change — +2 tests, and 194/194 was itself confirmed as the pre-existing baseline by stashing this change and re-running, not assumed from the last log line). No stylesheet touched — grepped clean of hex/raw-`px` outside the token files, as before. **Verified live**: rebuilt and redeployed the `web` image (`docker compose build web && docker compose up -d web`; `db`'s volume untouched, no `-v`), and `GET /api/assets` through the running stack still reads exactly as D34's backend half left it (AAPL/MSFT/Z74 `providerHasEverSucceeded: false`, ETH/AMP/ANVL `true`) — the DTO contract did not drift. ⚠️ **Not personally verified this session: the rendered `/assets` page in a browser.** This agent's tool context did not include the `claude-in-chrome` browser tools (the skill loaded but the underlying tools were unavailable), so the visual confirmation the task asked for — AAPL/MSFT/Z74 showing the gentle state and not the amber one, ETH/AMP/ANVL showing no warning — rests on the API payload plus the component-level test that reproduces the exact case, not on a screenshot. `backend-dotnet` (DTO contract, closed 2026-08-09 earlier the same day) + `frontend-angular` (consumption, closed 2026-08-09). | 3, 12 | **Found 2026-08-09 on the live container DB, reported by the user as the escalated amber warning appearing for assets whose identifiers were correct.** Root cause: `HasData` seeds `CreatedAt` as a static constant (`2026-07-26`), so any database examined more than a provider's threshold-in-days after that constant reads every never-yet-priced asset as suspiciously old, independent of whether a single refresh cycle had actually had the chance to run against it. Compounded by D33 on the crypto side specifically: CoinGecko's 401 was recording a false success, so even a provider that was *trying and failing* looked, from the old `HasEverBeenPriced`-only signal, no different from one that had never been given the chance. | Low once D33 was in — a new DTO field sourced from an existing table (`SourceRefreshStates`), no new migration. The frontend consumption is the remaining, not-yet-done half. `backend-dotnet` + `frontend-angular`. |

### D24 design note — the provider rules a "add your own asset" form has to encode

These are already established elsewhere in this file; collected here because an asset form is exactly
where getting them wrong becomes a permanently unpriceable holding (**D23**).

| Asset | `QuoteProviderKind` | Identifier field | Format |
|---|---|---|---|
| US equity | `TwelveData` | `ProviderSymbol` | Plain ticker — `AAPL` |
| SGX equity | `Yahoo` | `ProviderSymbol` | **`.SI` suffix** — `Z74.SI`. Twelve Data's free tier **cannot** serve SGX at all |
| Crypto | `CoinGecko` | `ProviderCoinId` | CoinGecko **id**, not ticker — `ethereum`, not `ETH` |

Three further constraints worth stating before someone adds twenty symbols:

- **Every Twelve Data symbol costs a credit per refresh cycle**, against 800/day. At the 5-minute
  open-market cadence a single US symbol is ~78 credits over a 6.5-hour session, so the practical
  ceiling is well under ten US stocks before the budget needs rethinking. Yahoo and CoinGecko are
  unmetered.
- **A new asset has no `PriceHistory`**, so its chart and its contribution to annual returns are
  empty until a backfill runs — which, per **D12**, currently never happens.
- **Currency matters**: transactions are stored natively and converted at each date's stored FX
  rate. A new non-USD asset needs its pair covered by `TwelveDataFxProvider`, which today handles
  USD/SGD only.

### D20 design note — decide this before writing code

The tempting fix is to write a `PriceQuote` row from the last close so everything downstream "just
works". **Do not do that without keeping the as-of date honest.** D4 is the same mistake already
made once: Z74 gets a stale Yahoo close stored with a fresh-looking timestamp on unmodelled lunar
holidays, so it *silently looks current on days it isn't*. A portfolio that quietly presents
Friday's close as a live Monday-morning price is worse than one that admits it has no price.

The three options, with the trade-off that actually separates them:

1. **Read-time fallback in the summary/allocation assembly** — when no `PriceQuote` exists, fall
   back to the newest `PriceHistory` close for that asset, and carry *its* date into the existing
   `priceAsOf` field alongside a new discriminator (e.g. `priceSource: "Live" | "Close"`). Nothing
   is written, no provider is called, the two tables keep their distinct meanings, and the API
   stays honest about what it just handed you. **Recommended.**
2. **Write-time seeding** — have the backfill or refresh service upsert a `PriceQuote` from the
   last close. Simplest downstream, but it collapses "live" and "stale close" into one field and
   walks straight into D4's failure mode. Only viable if `AsOf` is set to the *close date* and
   every consumer already treats a stale `AsOf` as stale — which today's UI does not.
3. **Call the provider anyway when closed** — rejected. It spends Twelve Data credits to fetch a
   number already sitting in the database, and it breaks the deliberate market gate that keeps the
   800/day budget safe.

Whichever is chosen, the UI must **distinguish the two states** rather than showing a bare number:
"Close · Fri 24 Jul" reads differently from a live price, and the detail page already has
`priceAsOf` available to label it. Note the interaction with **D14** — `.detail__price--pending`
sets a colour while `.detail__price` does not, so the fallback state is currently *legible* and the
priced state is not; fix D14 first or the new close price will render invisible in dark mode.

**Does a last-close fallback change the yearly returns? No — and the reason matters.** Asked
2026-08-07, and checked in the code rather than assumed: `PortfolioPerformanceService` reads
**`PriceHistories` only**, for both the cost-vs-market series and `annual-returns`. It never touches
`PriceQuotes`. So a read-time fallback in the summary/allocation assembly is invisible to TWR by
construction — it changes what the *tiles and holdings rows* say, not what the charts compute.

**A closed market is also not, in itself, a problem for TWR.** The calculator links sub-periods
geometrically across gaps and attributes each cash flow to the first valuation on or after its date
(Phase 6), so a weekend or holiday with no stored close is handled correctly — you simply have one
fewer valuation.

What *does* damage yearly returns is **D12**: because nothing ever calls the backfill,
`PriceHistory` stops at whatever was last written, so annual returns are computed over a frozen
window and keep reporting a stale figure that looks current. Confirmed live — dev history ends
`2026-07-24` while the date is `2026-08-07`. **Fix D12 and the yearly-return staleness goes away on
its own; fix D20 alone and it does not.**

Two limits worth knowing before estimating this:

- **`PriceHistory` only exists for assets that have been backfilled**, which runs from an asset's
  first trade date. Held stocks have it (AAPL and Z74 have 5 rows each); MSFT, which has no
  transactions, has **zero**. That is the right shape for a portfolio view — but see **D12**, whose
  severity was corrected on 2026-08-07: the backfill has **no caller at all**, so history is not
  merely "arbitrarily stale", it is frozen at `2026-07-24` and will never advance. A last-close
  fallback built on it today would surface a two-week-old close. **Closing D12 is no longer
  "arguably" a prerequisite — it is one.**
- **Crypto has no `PriceHistory` at all, by decision, and needs none** — CoinGecko is unmetered and
  never gated, so a crypto quote is always live. This is a stocks-only concern.

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
| 2026-08-01 | — | 9 (browser verification) | **First rendered-pixel verification this app has ever had — six findings, four of them defects no test could have caught.** Chrome driven against the live app at 1600×1100 in both themes. Method note worth keeping: the agent's own probe data had a **flat** cost basis across every point, and a flat step and a flat smooth line render *identically* — so the step claim was unfalsifiable as set up. Adding a second Z74 buy mid-window (`336.92 → 689.33` at 07-22) made it testable, and it **passed**: a clean vertical jump, not a diagonal ramp. Also confirmed by eye: ANVL renders `$0.00050465` and `$0.0005326` with nothing flooring to `$0.00`; crypto genuinely has no chart components on either page; allocation colours stay keyed to asset identity when the cost/market toggle reorders the slices; the TWR tooltip `+1.27%` matches the API's `1.2675` exactly; and the "Awaiting price" row renders instead of a naive −100%. **Found: D14** — `.detail__price` renders at a **1.01:1** contrast ratio in dark mode, i.e. the live price is invisible on every detail page, stocks and crypto. Diagnosed rather than patched: it is not the Phase 9 component's fault, it inherits near-black from `mat-sidenav-container`, because `styles.scss` repoints `--mat-sys-surface`/`-on-surface` but **not** `--mat-sys-background`/`-on-background`, *and* its `body { color-scheme: light }` forces every remaining `light-dark()` to the light branch — a rule whose own comment claims it is overridden by `ui.tokens.scss`, which is false, since that sets `color-scheme` on `html` and `body`'s own declaration wins for body's subtree. Proven by flipping to light mode, where the colour is unchanged but the background inverts and the price becomes perfectly legible. A scripted contrast sweep (excluding SVG, which paints via `fill` and false-positives on a naive sweep) found exactly 4 sub-3:1 elements, all from that one cause. **D15** — the line chart's two `endLabel`s overlap illegibly when cost and market value are close, which is the normal case for a new position. **D16** — charts never repaint on a theme change: after switching to light, `--ui-color-gridline` is `#e1e0d9` while the SVG still strokes the dark `#2c2c2a`. Both D15 and D16 were on the agent's own "not verified" list as open risks; **its honesty is what made them findable**, and both turned out real. **D17** — the overview headline reads −82.76% purely because one holding lacks a quote, a worse case than the all-unpriced −100% the agent recorded, since a partly-priced portfolio looks like a genuine crash rather than an obvious glitch. **D18** — icon ligature names (`arrow_downward`, `remove`) leak into the accessible text layer. **D19** — the x-axis shows day-of-month only, fine over 5 days and meaningless over the 1Y/All ranges offered. Nothing was fixed this session; all six are recorded with root causes and fix costs. Probe rows deleted, `GET /api/transactions` confirmed `[]`, and `TwelveData.lastAttemptedAt` still `null` — **0 Twelve Data credits spent**. |
| 2026-08-07 | `frontend-angular` | 9 (D14–D19 fixes) | **Five of the six browser findings fixed and confirmed at the rendered-pixel level; three new findings, one of which the fix pass itself introduced.** Agent did the work, orchestrating terminal drove Chrome at 1600×1100 in both themes. **D14 fixed at the cause**, not the symptom: the two missing `--mat-sys-background`/`-on-background` overrides added *and* `body { color-scheme: light }` deleted (rather than left in place with its false comment). Measured `.detail__price` at **17.42:1**, was 1.01:1; sidenav and toolbar icons at **13.49:1**, were 1.87:1; the full-page sweep now finds **0** elements under 3:1, was 4. Notably the agent *predicted* 17.4 and 13.5 from the token values before any browser ran, and both landed — a useful pattern, since it turns a vague "should be better" into a falsifiable number. **D15** confirmed against a deliberately harder case than the original finding (30 cents apart, not nine dollars). **D16** confirmed by the exact measurement that found it — gridlines `#2c2c2a`×6 → `#e1e0d9`×6 on a live flip with no stale stroke; only the `[data-theme]` path was exercised, a real OS flip is still unobserved. **D18** confirmed via the accessibility tree: `arrow_upward+$218.09·+40.95%` → `Up +$218.09+40.95%`. **D17 left alone deliberately** — product decision, backend origin. 🐛 **The D19 fix shipped a worse bug than D19, caught only by looking: D19a.** The agent formatted ticks in UTC on the reasoning that `new Date("YYYY-MM-DD")` parses as UTC midnight — correct in isolation, but the data goes to a `type: 'time'` axis, so **ECharts** does the parsing and reads that shape as *local* midnight. In SGT (UTC+8) the whole axis rendered one day early (Jul 19–23 for a Jul 20–24 series) while the tooltip, reading the raw string, said Jul 20 — axis and tooltip disagreeing by a day on the same point. It unit-tested green because the spec fed the formatter `new Date('2026-07-22T00:00:00Z')`, an input shape no code path produces. Fixed to local formatting, spec rebuilt on local-midnight inputs, and **confirmed to fail when reverted** (`expected 'Jul 19' to be 'Jul 20'`). It is invisible at zero/negative UTC offsets, so a UTC CI container would never have caught it. **Also found: D21** — the detail page's gain/loss tiles break `$4,995.00` across two lines mid-digit (128.9px box, 32px font, `overflow-wrap: break-word`); the overview tiles are wider and fine. **D22** — `transaction-form.dialog.spec.ts` fails roughly one run in two (observed 147/147, fail, 147/147, fail), pre-existing and unrelated to this change set. Final state: `ng test` **147/147 across 25 files** (was 138/138 — +8 from the agent, +1 regression guard from the browser pass), `ng build` clean bar the known chart-library budget warning. Probe rows (2 stepped AAPL buys + 1 ANVL) deleted, `GET /api/transactions` confirmed `[]`, and `TwelveData.symbolsRefreshed: 0` — **0 Twelve Data credits spent.** Also corrected in passing: Podman is installed (5.8.5), so Phase 10 is no longer blocked. |
| 2026-08-07 | `backend-dotnet` + `frontend-angular` | 12, plus D10–D12, D17, D20–D25 | **Ten open drawbacks closed in one session, one new defect found and fixed, and one of the orchestrating terminal's own findings turned out to be wrong.** Two agents ran in parallel on disjoint directories; the orchestrating terminal owned `tracker.md`, the commit, and all verification. **D12, the project's most consequential item, is closed and proven by watching the table grow** — MSFT 0 → 14 `PriceHistory` rows spanning 2026-07-20 → 08-06, idempotent on re-run, with the background service observed firing unprompted at startup. **D20** landed as the recommended read-time fallback (no `PriceQuote` written from a close, `priceAsOf` carrying the close's own date). **D17 was checked rather than assumed** — the "D20 largely closes it" claim in this file proved *false* for an asset with neither quote nor history, so `UnpricedHoldingsCount` was added and the overview now caveats the total. 🐛 **One real defect found by the orchestrating terminal, by calling the new endpoint instead of reading it (D26):** the backfill reported provider *failures* as `assetsSkippedForBudget` — live payload showed AAPL there with `providerCallsUsed: 1` against a budget of 20. The operator-facing consequence is that the one endpoint you would hit to ask "why isn't history advancing?" answers "budget", whose obvious remedy does nothing — D12's failure mode reproduced at the reporting layer. Fixed by splitting the outcomes, and the same pass removed a guaranteed-wasted credit (`providerCallsUsed` 1 → 0 for a same-day range that can never succeed). ✅ **Corrected in the other direction, and worth recording because it cuts against the orchestrator:** the terminal reported a gap where deactivated assets would remain selectable in the transaction dropdown. The agent checked and pushed back — `transaction-form.dialog.ts:97` already filtered on `isActive`, and the file was unchanged from HEAD. The finding was wrong; it had been inferred from the API returning inactive assets without reading the client. The agent added a pinning test instead, which was the right call. **D22 was root-caused, not retried** — ECharts measures text via `getContext('2d')` even in SVG mode, jsdom returns `null` *and* throws through the virtual console every call, and that starvation pushed an unrelated dialog spec past its timeout; proven by 5 consecutive 157/157 runs against a 1-in-2 failure baseline. **Phase 12 shipped complete** (asset management page, three-state theme toggle) and the frontend agent caught something unpredictable on the way: Angular's production `inlineCritical` extraction drops both the dark `@media` block and the `[data-theme]` override, so the anti-flash script alone would still have flashed light — `inlineCritical` is now off, deliberately. **Verified independently of both agents:** backend build 0 warnings and **157/157** (was 134/134); frontend `ng build` clean and **182/182 across 29 files** (was 147/147 across 25), green on repeated runs; no hex or raw `px` outside the token files; and a Chrome pass at 1568×675 confirming the `Close · Fri 24 Jul` caption, the unpriced-holdings banner, tiles no longer breaking mid-digit, a full chart repaint on theme flip with zero stale strokes, "Follow OS" genuinely removing the attribute, a real server-validated `POST` on the wire, and **0 elements under 3:1** in a full-page contrast sweep. All probe rows deleted — `GET /api/transactions` is `[]` and `Assets` is back to the 6 seeded rows; MSFT's 14 `PriceHistory` rows were kept deliberately as genuine market data. **0 Twelve Data credits** spent on live quotes (NYSE closed all session); ~5 spent on backfill testing, against 800/day. Left knowingly: D27 (a well-formed but wrong symbol is still silent), the allocation DTO's missing unpriced caveat, and the `menuitemradio` fix unverified by real assistive tech. |
| 2026-07-31 | `frontend-angular` | 7 | **Complete and verified.** Angular 22 workspace scaffolded with the central design system the user asked for: `ui.tokens.scss` + `ui.mixins.scss`, with Material *derived from* the tokens rather than themed alongside them. Agent reported honestly and flagged the proxy and hub as never exercised live — testing that flag found **two real defects**. (1) **Ids were typed `string` across `models.ts`** while the backend sends C# `int` as JSON numbers; since the API sets no `AllowReadingFromString`, the Phase 8 transaction form would have `POST`ed `"assetId": "3"` and got a 400. The specs passed only because their fixtures (`'a1'`, `'t1'`) matched the wrong type. Fixed to `number`, then confirmed against the live API (`"id":1`) and the live hub (`"assetId":3`). (2) **The D5 "why nothing moved" logic was dead code** — it filtered `sources` for `attempted: false`, but `RunCycleAsync` drops a gated provider from that list entirely, so the filter could never match and a click with NYSE closed would have said a cheerful "Refreshed 4 symbols" with no explanation. Rewritten to derive closed markets from `nyseOpen`/`sgxOpen`, and its spec rebuilt around a payload captured verbatim from the live API instead of a fabricated one; recorded as **D10**. Also closed the design-system gaps the agent left: raw `px` layout values inlined in five component stylesheets despite the token file's own rule (now `--ui-layout-*` / `--ui-size-icon-*` tokens, with the toolbar height tracking Material's 64→56px breakpoint so the content `calc()` stays right on mobile), and the dark-mode block duplicated between the media query and `[data-theme]` (now one `ui-dark-tokens` mixin, so a token cannot be added to one and forgotten in the other). Verified independently of the agent: `ng build` clean, `ng test` **68/68**, no hex or raw `px` anywhere outside the token files, and a **live run through the dev proxy** — 6 assets over `/api`, a real SignalR client on `WebSocketTransport` (not long-polling), on-connect snapshot, 4 `QuoteUpdated` frames with sub-cent precision intact, `200` then `429 secondsRemaining: 26`. NYSE closed throughout, so 0 Twelve Data credits spent. Left knowingly: the `@angular/cli` Node-check patch (**D9**), and D8's 10-dp *quantity* round trip still unmeasured. |
| 2026-08-08 | — | pre-Phase-10 drawbacks (D4, D7, D13, D27, D17/D18 residuals) | **Six items closed, one new defect found while fixing another, and one verification that honestly failed.** Worked directly rather than via the sub-agents, since the changes were small and spanned both stacks. Final state: backend **build clean, 0 warnings, 173/173** (was 157/157); frontend `ng build` clean, **194/194 across 29 files** (was 182/182); tree re-grepped clean of hex and raw `px` outside the token files (one new token, `--ui-size-icon-xs`). **D7** — both serializers now built from one `PortfolioJsonSerialization.Apply`, with naming policy set *explicitly* so agreement is stated rather than coincidental; all 3 new parity tests confirmed to fail when the original Phase 5 bug is reintroduced. **D4** — closed without modelling a single lunar holiday, at the user's direction: the calendar stays wrong, the reported price stops lying. Cheaper than estimated because the providers already stored honest timestamps and only `PortfolioSummaryService` was ignoring them. 🐛 **D4a, found while testing D4 and worse than D4 in isolation:** the fix was half-useless, because `AssetDetailPage` explicitly assumed *"a genuine SignalR push is always live"* — false in exactly the lunar-holiday case — so a push **overrode** the honest label on the most prominent number on the page. Fixed on the backend by putting the verdict on the wire (`QuoteUpdateNotification.Source`), not by reimplementing session arithmetic in the browser; a second, *structural* half turned up on top of it — the `Close ·` caption lived inside the snapshot branch of the template, so the push path had nowhere to render it even with the flag correct. **D13** — the standout: 601 + 560 `PriceHistory` rows over 2024-03-15 → 2026-08-07 from ~2 Twelve Data credits, with 59 NYSE-only and 18 SGX-only dates proving the calendars genuinely diverge. All three years hand-recomputed from the raw series to **0.0000 difference**, and the mid-period $1,001 deposit shown excluded — **+13.7391% TWR against a naive +62.3340%**. **D27** — `CreatedAt` + `HasEverBeenPriced`, with per-provider thresholds (3 days stocks / 1 day crypto) chosen so a stock added on a Friday evening does not trip the warning over a weekend when no poll could legally have happened. **D30 (D17 residual)** — the pie's `0%` now distinguishes ignorance from a tiny position; only ever affected the market-value pie, since cost basis is known regardless. **D18 residual** — inline SVG; the old spec had been *pinning the defect* by asserting `textContent` contained `arrow_upward`. ⚠️ **D31 — the one that did not work.** A real Windows theme flip (registry + `WM_SETTINGCHANGE` broadcast) was measured to reach the page (media query flips, CSS tokens repaint) while the chart strokes lagged one flip behind, and the `change` event fired **zero** times — but the tab was `visibilityState: "hidden"` throughout, because the extension never activates tabs, so the result is confounded and D16 is recorded as **neither verified nor broken**. ✅ **D31 was closed by the user minutes later, and the agent's finding was a false positive**: with a visible, focused window on "Follow OS", a real Windows theme change repaints the gridlines correctly. The hidden-tab confound was the entire explanation, so **D16 is fixed on both paths**. The lesson is now recorded in D31 and is the durable takeaway from this session: an extension-driven tab is permanently `visibilityState: "hidden"`, and Chrome withholds `prefers-color-scheme` events from hidden tabs *while still* updating `matches` and repainting CSS — which mimics a broken listener almost perfectly. Housekeeping: found and removed a **leftover MSFT probe transaction (id 34)** that the 2026-08-07 log claimed had been deleted — `GET /api/transactions` was not `[]` at session start. All probes deleted; transactions confirmed `[]`. Multi-year `PriceHistory`/`FxRate` rows kept deliberately as genuine market data. **0 Twelve Data quote credits** (NYSE closed all session); ~2 spent on the D13 backfill, against 800/day. |
| 2026-08-08 | — | 10 (retarget only) | **Podman → Docker conversion, documentation and agent definition only. No Phase 10 work was started, at the user's explicit instruction.** The user uninstalled Podman and installed Docker Desktop; verified on this machine — `podman` returns *command not found*, Docker reports **29.6.2** with **Compose v5.3.1**. **The swap was cheap for exactly one reason worth recording: Phase 10 had never produced a single artifact.** A glob for `Containerfile*`, `compose*.yaml`, `nginx.conf` and `.containerignore` returned nothing but `.env.example`, so there was no stack to port — only prose describing one that did not exist yet. Had Phase 10 been done first, this would have been a real migration instead of a rename. Changed: `.claude/agents/container-podman.md` → **`container-docker.md`** (rewritten, not renamed — rootless-Podman rules replaced with non-root-container rules, `podman machine` gotchas replaced with Docker Desktop ones, `host.containers.internal` → `host.docker.internal`); `CLAUDE.md` ×4; `README.md` ×2 blocks; `tracker.md` throughout; and two XML doc comments that named "the Podman container" (`MarketCalendar.cs:8`, `DependencyInjection.cs:22`) — grepped for rather than assumed, and they are the reason a swap that looks like a docs edit also touched `src/`. Backend build re-run after the comment edits: **clean, 0 warnings** under `TreatWarningsAsErrors`. **Two new Docker-specific hazards recorded** that Podman did not have: `docker compose down -v` destroys the named volume the persistence check exists to prove, and Docker Desktop can be **manually paused**, in which case every command fails with *"Docker Desktop is manually paused"* — which reads like a misconfiguration and is not one. It was paused during this session, which is how it got found. **Deliberately unverified:** nothing was built or run. Docker's daemon was never exercised, so "Docker works on this machine" rests on `docker --version` and `docker compose version` alone — the paused daemon means even `docker info` did not succeed. The historical handoff-log rows above still say Podman; they are dated records of what was true then and were left intact rather than rewritten. |
| 2026-08-09 | `container-docker` | 10 | **Complete and verified — the real stack, built and run, not just written.** Built `Dockerfile.api`, `Dockerfile.web`, `Dockerfile.migrate` (new — the D28 one-shot init service), `docker/db-init/Portfolio.DbInit` (new tiny console app, **zero changes under `src/`**), `nginx.conf`, `compose.yaml`, `.dockerignore`, and rewrote `.env.example` with both SQL credentials clearly labelled bootstrap-vs-app. 🐛 **One real defect found and fixed, not anticipated by the drawback register**: the plain `*-noble-chiseled` runtime images set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true`, under which `Microsoft.Data.SqlClient` cannot open a connection to SQL Server at all (`migrate` exited `139` on the very first `up` attempt with that exact exception). Both `Dockerfile.api` and `Dockerfile.migrate` switched to the `-extra` chiselled variant, still non-root by default — recorded in the agent file so it isn't rediscovered. **D28 and D29 closed together, as the register itself insisted they must be**: `migrate` is the only container ever handed `MSSQL_SA_PASSWORD`, applies the exact same 4 EF Core migrations SQLEXPRESS has, provisions `portfolio_app` (`db_datareader`+`db_datawriter`, no DDL), and exits; `api` gates on its success and connects only as `portfolio_app`. **Verified from the app's own live connection, not the env file** — queried `sys.dm_exec_sessions` directly against `db` while `api` was running: both EF Core sessions were `login_name = 'portfolio_app'`, `host_name` matching the `api` container's own hostname, and a `CREATE TABLE` attempted as `portfolio_app` via `sqlcmd` was rejected with `Msg 262 … permission denied` — the DDL boundary is real, not just granted-and-unused. **The empty-volume test was genuine**: `docker volume rm portfolio_mssql-data` run first (confirmed absent via `docker volume ls`), then `docker compose up -d` — `__EFMigrationsHistory` came back with all 4 migrations, an exact match to the `.cs` files under `src/Portfolio.Infrastructure/Persistence/Migrations/`. **SignalR verified at the protocol level**: a raw HTTP `Upgrade` request to `/hubs/prices?id=<real negotiate token>` through nginx returned `HTTP/1.1 101 Switching Protocols` with a server-computed `Sec-WebSocket-Accept`, not inferred from config existing. **Persistence verified with a real probe**: created a transaction via `POST`, ran `docker compose down` (confirmed **no** `-v`, volume still listed afterward), `up` again, confirmed the row survived and `migrate` re-ran idempotently (exit `0`, no-op past already-applied migrations); probe deleted after. All four containers confirmed non-root by direct inspection (`api` uid 1654, `web`/nginx uid 101 including the master process not just workers, `db`'s vendor image uid 10001, `migrate` uid 1654) rather than assumed from the Dockerfile text. 🐛 **Found but deliberately NOT fixed — recorded as D32, not patched**: CoinGecko 401s against the Pro API because `CoinGeckoOptions.ApiKey is null ? Keyless : Pro` treats a compose `.env`'s blank-but-present `CoinGecko__ApiKey=` as "has a key" — confirmed there is no compose-side fix (`${VAR:+…}`, `${VAR:-}`, and bare passthrough all still emit an empty string once `.env` defines the key at all), so the one-line fix belongs in `Portfolio.Infrastructure/DependencyInjection.cs`, out of `container-docker`'s boundary. `README.md` and the agent's own completion checklist corrected in the same change — the checklist no longer claims `docker compose logs api` can show migrations applied, since the chiselled `api` image structurally cannot emit that line; it now points at `docker compose logs migrate`. Real API keys used for the live run (the already-configured Twelve Data key, CoinGecko left keyless per its own now-documented caveat) — **0 Twelve Data credits spent**, confirmed via the app's own `/api/prices/status` showing NYSE/SGX gated all session (Sunday). Left knowingly: no browser was driven — every check above is `curl`/`sqlcmd`/protocol-level, so nothing about the *rendered* app (does the UI actually show live-updating prices on screen, does the manual refresh button work when clicked) was confirmed visually inside the container the way Phase 9's browser pass did for the dev server. Also left knowingly: D6 (real trading-day credit budget) is untouched by this phase and remains Phase 11's only real blocker. |
| 2026-08-09 | `backend-dotnet` | D32, D33 (new), D34 (new) | **Three coupled defects, fixed in the order the task mandated, and confirmed against the live Docker stack, not only unit tests.** All triggered by the user's own symptom report — the `/assets` page showing the escalated D27 amber warning on assets with correct identifiers. **D32**: `CoinGeckoOptions.HasApiKey` (`!string.IsNullOrWhiteSpace`, `[MemberNotNullWhen(true, nameof(ApiKey))]`) replaces every bare `ApiKey is null`/`is not null` check in the file — not just the one line the finding quoted. Grepping the tree turned up a **second, unreported call site**: `CoinGeckoQuoteProvider.ClampToProviderWindow`'s keyless-365-day-window check used the same wrong pattern and would have stayed broken (an empty key wrongly lifting the clamp) had only the DI line been touched. 🐛 **D33, found while verifying D32, not anticipated going in**: `GET /api/prices/status` was returning `lastRunSuccess: true` for the exact CoinGecko cycle that 401'd for all 3 coins — `PriceRefreshService.RefreshGroupAsync` hardcoded `Success: true` on its returned outcome whenever the HTTP call itself didn't throw, which is right for Twelve Data's single-bad-symbol-in-an-otherwise-good-batch case but wrong when **every** symbol in the batch failed. Fixed with a deliberate, commented rule — `Success = succeeded > 0` — and the genuine partial case (some succeed, some fail) was written down as explicitly NOT a failure, per the task's own instruction to decide that rule rather than let it fall out by accident. A gated (market-closed) cycle was already correctly excluded from this by the pre-existing D10 outcome shape, unaffected by this change. **D34**: new `AssetDto.ProviderHasEverSucceeded` — true when `SourceRefreshStates.LastSuccessAt` is non-null for the asset's `QuoteProviderKind`, queried across every asset sharing that provider — is the gate the task specified: an asset's own silence carries no evidence about its identifier if the provider behind it has never once succeeded for anyone in this database. Confirmed D27's existing tests encoded no wrong behaviour and needed no rewriting — this is an additive field, `HasEverBeenPriced`'s own meaning is untouched. **16 new tests total** (6 pinning `CoinGeckoOptions.HasApiKey` directly, 1 pinning the clamp call site, 2 for D33's success rule, 7 for D34's provider-level gate), and the ones that matter were **confirmed to fail against each pre-fix behaviour** by temporarily reverting the fix line, not just written and trusted: D32's `HasApiKey => ApiKey is not null` failed 5/6 of the new CoinGecko tests; D33's `Success: true` hardcoded back in failed the all-fail reproduction; D34's `ProviderHasEverSucceeded` hardcoded `false` failed 4/7 of the new AssetService tests. Backend **build clean, 0 warnings, dotnet test 189/189** (was 173/173). **Verified live against the running Docker stack** — `docker compose build api && docker compose up -d api` (never `-v`; `db`'s data volume untouched throughout), then `GET /api/prices/status`'s CoinGecko entry measured **before**: `{"lastRunSuccess":true,"lastError":"CoinGecko returned HTTP 401.","symbolsRefreshed":0}`, **after**: `{"lastRunSuccess":true,"lastError":null,"symbolsRefreshed":3}` — a genuine `200` from the free `api.coingecko.com`, confirmed via a resilience-pipeline log line (`Result: '200'`), not merely absence-of-error. `GET /api/assets` confirmed the D34 gate directly: `ETH`/`AMP`/`ANVL` (CoinGecko, now genuinely succeeding) read `providerHasEverSucceeded: true`, while `AAPL`/`MSFT`/`Z74` (TwelveData/Yahoo) correctly stayed `false` — not because anything is wrong with them, but because it was a Sunday and both markets were gated all session, so neither provider had been given a real chance yet; that is the exactly-intended non-escalating state. **0 Twelve Data credits spent** — confirmed via `TwelveData.lastAttemptedAt` staying `null` throughout. ⚠️ **D34 is backend-only.** `AssetDto.ProviderHasEverSucceeded` exists and is proven correct on the wire, but `asset-management.page.ts`'s `unpricedState()` does not read it yet — the escalated warning will keep firing on a fresh database until a `frontend-angular` session adds it to the suppression check. Left knowingly: no browser was driven this session — the `/assets` page's rendered warning was never looked at, only the JSON contract feeding it; that visual confirmation is explicitly for the next session. |
| 2026-08-09 | `frontend-angular` | D34 (frontend half) | **Small, scoped follow-up closed the same day it was left open.** `AssetDto` gained `providerHasEverSucceeded: boolean` in `models.ts`, mirroring the backend field exactly (non-optional — the backend always sends it). `asset-management.page.ts`'s `unpricedState()` now computes `suspicious` as `asset.providerHasEverSucceeded && days >= STALE_AFTER_DAYS[quoteProviderKind]`, a third suppression term ANDed into the existing calculation rather than a fourth early return — so a never-priced asset whose provider has never succeeded still renders the calm "no price yet" state, not silence, matching the DTO's own doc comment ("keep only the calm no price yet state") rather than the next-session prompt's looser "no warning at all" phrasing, which would have suppressed the row entirely and was checked against the task's own explicit instruction before implementing. **2 new tests**, the reproduction case confirmed to FAIL against the pre-fix (AND-less) logic before being fixed: created 14 days ago, never priced, `providerHasEverSucceeded: false` — rendered "check this identifier" pre-fix, "waiting for the next refresh" post-fix; a paired test confirms escalation still fires once `providerHasEverSucceeded: true`. All 6 pre-existing D27 tests needed no changes — their shared `AAPL` fixture already carried `providerHasEverSucceeded: true`, so the weekend-threshold case stayed intact exactly as the prior session predicted. Other `AssetDto` fixtures across the frontend test tree (`asset-detail.page.spec.ts`, `asset-form.dialog.spec.ts`, `transaction-form.dialog.spec.ts`) updated to carry the new required field so the build stays clean. `ng build` clean, `ng test` **196/196 across 29 files** (was **194/194**, confirmed as the genuine pre-change baseline by stashing this diff and re-running before trusting the delta). No stylesheet touched; re-grepped the tree — still 0 hex colours and 0 raw `px` outside `ui.tokens.scss` (the 1px hairline exception untouched). Rebuilt and redeployed the `web` Docker image (`docker compose build web && docker compose up -d web`; `db`'s volume never touched, no `-v`), then curled `GET /api/assets` through the running stack and confirmed the payload is unchanged from the backend session's own verified values (AAPL/MSFT/Z74 `false`, ETH/AMP/ANVL `true`) — the contract did not drift between sessions. ⚠️ **Left knowingly: the rendered `/assets` page was never looked at in a browser.** This agent's tool context loaded the `claude-in-chrome` skill but did not have the underlying `mcp__claude-in-chrome__*` tools available to actually drive Chrome, so the task's own requested visual check — confirming AAPL/MSFT/Z74 show the gentle state and ETH/AMP/ANVL show nothing, at the pixel level, in the live container — was not performed. What stands in its place: the API contract (verified via `curl`) and a component test that reproduces the exact reported scenario end-to-end through the real template. That is real evidence but it is not the same claim as "looked at the page and saw it," and the gap should be closed by a session that has browser tools before anyone treats D34 as fully closed at the visual level the way D31/D32/D33 were. |

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

Added during Phase 12 and the D10–D25 sweep (2026-08-07):

- **`PriceHistory` grows only for assets that have transactions.** The backfill runs from an asset's
  earliest trade date, so an asset nobody holds is skipped entirely — by design, not omission. A
  consequence worth knowing before it looks like a bug: Z74's history stays at 2026-07-24 for as long
  as no transaction references it, even though the daily backfill is now running.
- **A close is never written into `PriceQuote`.** The two tables keep their distinct meanings and the
  fallback happens at read time, carrying `priceSource` and the close's own `priceAsOf`. This was a
  deliberate rejection of the simpler write-time seeding, which would repeat D4's mistake of making a
  stale price look current. **Any future consumer must treat `priceSource: "Close"` as stale data.**
- **A backfill outcome list must distinguish "not attempted" from "attempted and failed."** See
  **D26**. A single skip list whose name asserts a reason is how a silent data-staleness bug hides
  behind a healthy-looking report.
- **Angular's production `inlineCritical` is OFF, deliberately.** Its critical-CSS extraction has no
  knowledge of `data-theme` and inlines only the plain `:root` (light) rules, dropping both the dark
  `@media` block and the `[data-theme='dark']` override — confirmed by grepping the built
  `index.html`. With it on, first paint is always light regardless of preference, defeating the
  anti-flash script in `index.html`. **Do not turn it back on to shave first-paint bytes without
  re-testing the dark-mode first paint.**
- **jsdom needs a stubbed 2D canvas context or the whole vitest suite gets slower and flakier.**
  ECharts measures text through `getContext('2d')` even with `renderer: 'svg'`. See **D22** — the
  symptom appeared in a completely unrelated spec, so do not delete the stub in `test-setup.ts`
  because "no test uses canvas."
- **"Follow OS" means removing the `data-theme` attribute**, not writing the currently-resolved
  value. Pinning the resolved value silently stops following the OS at the next system theme change
  while still claiming to.

---

## ▶ Next session

Phases 1–10 and **12** are done and verified. **Only Phase 11 remains, and D34 (both halves) is now
fully closed, including at the rendered-pixel level** — see its row and the three 2026-08-09 handoff
entries. The drawback register is down to **one genuinely open row — D6**. D6 needs a real NYSE
trading day, which no local session can manufacture — Phase 11 is otherwise the only thing left, and
four of its six checks are already reachable today.

> ✅ **The D34 visual gap is closed (2026-08-09, orchestrating terminal).** The `frontend-angular`
> session that wrote the fix had no browser tools, so it correctly left the check open rather than
> claiming it. Done afterwards from the orchestrating terminal against the live Docker stack at
> `http://localhost:8080/assets`: a scripted scan of the rendered page returned
> `escalatedTextPresent: false` and `fourteenDaysPresent: false` — the strings `"check this
> identifier"` and `"No price in N days"` appear **nowhere** on the page — with AAPL, MSFT and Z74
> each rendering *"No price yet — waiting for the next refresh"* and AMP, ANVL and ETH rendering no
> hint at all, being genuinely priced now that D32 is fixed. `document.visibilityState` was
> **`"visible"`**, checked first per D31, so this is not another hidden-tab false positive.
> **This is also the first time any part of the containerized app has been confirmed visually** —
> Phase 10's verification was entirely `curl`/`sqlcmd`/protocol-level, by its own admission.

```
Read tracker.md, then the Phase 11 checklist and its blocking note. Phase 10 (Docker)
closed on 2026-08-09, so the container-DB migration-parity check is ALREADY ticked —
do not re-verify it, it was done as part of Phase 10, not deferred to this session.

Four checks are reachable now with a running dotnet API + ng serve (or the Docker
stack — either proves the same application behaviour):
  - Enter a fractional crypto buy; confirm it persists and the gain/loss card
    recalculates.
  - /stocks and /crypto totals are fully independent.
  - Timestamp updates across a scheduled refresh with no page reload.
  - Z74 quote arrives in SGD and converts at the stored USD/SGD rate.

One check is BLOCKED and cannot be helped by more local work — D6, Twelve Data
credit use over a real NYSE trading day. Every session so far has run with NYSE
closed and spent 0 credits, so "well under 800/day" is still arithmetic, not
measurement. Only tick this box if you are actually running during real NYSE
hours on a real trading day — check the market calendar first, and report exactly
how many credits were spent, against the 800/day budget.

D34 needs nothing further — its visual check was completed from the orchestrating
terminal on 2026-08-09 and the row is fully closed. Do not redo it.

The containerized app has now been LOOKED AT exactly once, on one page
(/assets). Phase 10's own verification was entirely curl/sqlcmd/protocol-level,
so if you have browser tools, the highest-value thing beyond the four checks
above is driving the rest of the app inside the container the way Phase 9 did
for the dev server — in particular whether prices actually update on screen
across a refresh cycle, and whether the manual refresh button works when
genuinely clicked rather than POSTed. Check document.visibilityState before
drawing any conclusion about anything visibility-sensitive — see D31.

Tick a box only for something you have personally seen pass, say plainly what you
did not verify, then append to the handoff log, write the next session prompt, and
commit.
```

**If Phase 11 closes cleanly, the project has no phases left except D6 sitting in the register
until a real NYSE trading day happens to be running.** Worth deciding explicitly at that point
whether the project is "done" with D6 as a permanent asterisk, or whether someone deliberately
runs a session during market hours to close it.

**One thing to watch on a session that touches the theme:** the toggle's menu items were
changed to `role="menuitemradio"` + `aria-checked`, but that was verified only by DOM assertion —
no real screen reader has been near it.

**One thing to watch on a session that touches the Docker stack again:** if you ever need to
rebuild `Dockerfile.api` or `Dockerfile.migrate` from a base-image change, keep them on the
`*-noble-chiseled-extra` tags, not the plain `*-noble-chiseled` ones — the plain tags set
`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true`, under which `Microsoft.Data.SqlClient` cannot open a
connection to SQL Server at all. Found live during the Phase 10 session; see D28's closure note and
`.claude/agents/container-docker.md`.

### Q&A note — four questions asked 2026-08-07, and what investigating them turned up

Recorded because two of the four changed the project's priorities, and one corrected a claim in this
file.

1. **"Can we show the last close when the market is closed?"** Yes — that is **D20**, already
   designed, and AAPL's `333.019989` for 2026-07-24 is on disk right now. But investigating it
   promoted **D12** from a testing gap to a hard prerequisite: the table D20 would read from is
   never updated.
2. **"How does that affect yearly returns?"** It does not — checked in the code, not assumed.
   Performance and annual returns read `PriceHistories` only and never `PriceQuotes`, so a
   summary-level fallback is invisible to TWR. A closed market is likewise fine for TWR, which links
   sub-periods across gaps by design. **The thing that actually damages yearly returns is D12**, and
   fixing D12 fixes it without touching the calculator.
3. **"What if I want stocks/crypto outside the seeded list?"** The backend endpoint exists, but the
   frontend has no UI for it (**D24**) and the endpoint will accept a permanently unpriceable asset
   (**D23**). Became Phase 12.
4. **"Where is the light/dark toggle?"** There is none (**D25**). Every token and, since the D16
   fix, the chart repaint listener are in place — nothing ever sets the attribute. During
   verification it was only reachable from devtools by hand.

<details>
<summary>D14–D19 fix prompt — completed 2026-08-07, kept for reference</summary>

```
Read tracker.md, then the D14-D19 rows in the drawback register. Phase 9 was
browser-verified from the orchestrating terminal and these six findings came out of
looking at rendered output, not from tests. Use the frontend-angular agent.

Fix D14 FIRST and fix the CAUSE, not the symptom. The live price on every detail page
renders at a 1.01:1 contrast ratio in dark mode — measurably invisible. It is NOT a bug
in the Phase 9 component: .detail__price sets no colour and inherits near-black from
mat-sidenav-container. Two things cause that, and both need fixing:
  1. styles.scss repoints --mat-sys-surface / --mat-sys-on-surface but NOT
     --mat-sys-background / --mat-sys-on-background, so those keep mat.theme()'s own
     light-dark(#faf9fd,#121316) / light-dark(#1a1b1f,#e3e2e6).
  2. styles.scss sets `body { color-scheme: light }`, which pins every remaining
     light-dark() inside body to the LIGHT branch whatever the OS says. That rule's own
     comment claims ui.tokens.scss overrides it. That comment is wrong — ui.tokens sets
     color-scheme on :root/html, and body's own declaration wins for body's subtree.
     Fix the comment or the rule; do not leave both.
Just adding a colour to .detail__price is NOT the fix — it leaves the inherited
near-black lying in wait for the next element that omits one. The same root cause dims
the sidenav and toolbar icons to 1.87:1, so a correct fix clears those too; verify it
does.

Then D16 (charts bake theme colours at construction and never re-resolve — needs a
matchMedia listener plus a [data-theme] MutationObserver forcing the option computed()
to re-evaluate), D15 (the two endLabels overlap illegibly when cost and market value are
close, which is the NORMAL case for a new position), D18 (aria-hidden on the decorative
mat-icons), D19 (x-axis shows day-of-month only — meaningless over the 1Y/All ranges the
selector already offers).

Leave D17 alone unless you want to raise it — it needs a product decision about whether
an unpriced holding should be excluded from portfolio totals or merely caveated, and it
originates in the backend's summary DTO, not the frontend.

Verify in a real browser this time if you have Chrome tooling; if you do not, say so
plainly and hand the visual check back rather than calling a contrast fix "verified" off
a unit test. A contrast bug that unit-tests green is exactly what produced D14.

The dev database has 0 transactions. To see non-empty charts you will need probe rows —
and note the trap the verification pass hit: with a single buy the cost basis is FLAT at
every point, and a flat step renders identically to a flat line, so the step series is
unfalsifiable. Use at least two buys on different dates. Delete probes afterward and
confirm GET /api/transactions is [].
```

</details>

**Servers:** both the API (port 5100) and `ng serve` (port 4200) were left **running** at the end of
the 2026-08-07 session. Check they're still up before starting a new pair. If not:

```bash
dotnet run --project src/Portfolio.Api --launch-profile http   # port 5100
cd src/Portfolio.Web && npm start                              # port 4200, proxies /api and /hubs
```

> ⚠️ Pass `--launch-profile http` (or just `dotnet run`). Suppressing the launch profile leaves
> `ASPNETCORE_ENVIRONMENT` unset, so `appsettings.Development.json` never loads and the API dies at
> startup with *"Connection string 'Portfolio' is not configured"* — which looks like a missing
> secret rather than a missing environment.

**The optional backend cleanup that used to sit here (D10, D11) was completed on 2026-08-07.**

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

~~**Optional backend cleanup** — D10 and D11.~~ **Both closed 2026-08-07.**

**Remaining, for later:**

- ~~**Phase 10** (`container-podman`) needs Podman installed.~~ **Unblocked 2026-08-07, and
  retargeted to Docker 2026-08-08** — Podman was uninstalled; Docker 29.6.2 / Compose v5.3.1 is
  present, though Docker Desktop was **paused** when checked. **Deferred by the user on 2026-08-07**,
  so it is skipped by choice rather than blocked. Still true and easy to get wrong:
  `Dockerfile.web`'s Node base image must be **22.22.3 or newer** — Angular 22's CLI hard-refuses
  anything older, so a plain `node:22-alpine` tag is only safe if it currently resolves above that.
  Pin it explicitly.
- **Phase 11** cannot fully close until Phase 10 lands (the container-DB migration check) and until a
  real trading day has been observed (**D6**). Four of its six checks are reachable before either.
