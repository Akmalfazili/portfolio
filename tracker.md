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
| 1 | Backend scaffold | `backend-dotnet` | ⬜ Not started |
| 2 | Domain + EF Core | `backend-dotnet` | ⬜ Not started |
| 3 | Transactions CRUD API | `backend-dotnet` | ⬜ Not started |
| 4 | Market data providers | `backend-dotnet` | ⬜ Not started |
| 5 | Auto-refresh + SignalR | `backend-dotnet` | ⬜ Not started |
| 6 | Portfolio calculations | `backend-dotnet` | ⬜ Not started |
| 7 | Angular scaffold + shell | `frontend-angular` | ⬜ Not started |
| 8 | Transactions UI | `frontend-angular` | ⬜ Not started |
| 9 | Overview + detail pages | `frontend-angular` | ⬜ Not started |
| 10 | Podman stack | `container-podman` | ⬜ Not started |
| 11 | End-to-end verification | — | ⬜ Not started |

**Currently active:** none — awaiting prerequisites.

---

## Prerequisites

Blocking items you need to handle before the relevant phase can start.

| Item | Status | Needed by | How |
|---|---|---|---|
| Node.js 22.13.0 | ✅ Installed | Phase 7 | — |
| `MSSQL$SQLEXPRESS` | ✅ Running | Phase 2 | — |
| **.NET 10 SDK** | ❌ **Missing** (9.0.302 present) | **Phase 1** | `! winget install Microsoft.DotNet.SDK.10` |
| **Twelve Data API key** | ❌ Missing | Phase 4 | Free, no card — https://twelvedata.com/pricing |
| **CoinGecko Demo key** | ❌ Missing | Phase 4 | Free — https://www.coingecko.com/en/api |
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

### ⬜ Phase 1 — Backend scaffold · `backend-dotnet`

**Blocked by:** .NET 10 SDK

- [ ] `global.json` pinning the .NET 10 SDK
- [ ] `Directory.Build.props` — `net10.0`, nullable, implicit usings, warnings-as-errors
- [ ] `portfolio.sln` with `Portfolio.Domain`, `.Application`, `.Infrastructure`, `.Api`
- [ ] `tests/Portfolio.UnitTests`, `tests/Portfolio.IntegrationTests`
- [ ] Project references wired inward-only (Domain depends on nothing)
- [ ] `dotnet build` clean

### ⬜ Phase 2 — Domain + EF Core · `backend-dotnet`

- [ ] Entities: `Asset`, `Transaction`, `PriceQuote`, `PriceHistory`, `FxRate`, `RefreshRun`
- [ ] Enums: `AssetClass`, `TransactionType`, `RefreshTrigger`
- [ ] `PortfolioDbContext` with **explicit decimal precision on every decimal column**
- [ ] Unique indexes on `(AssetId, Date)` and `(Date, Base, Quote)`
- [ ] Initial migration applied to local SQLEXPRESS
- [ ] Seed: US holdings, `Z74:XSES`, `ethereum`, `amp-token`, `anvil`
- [ ] **Precision test** — round-trip `0.000123456` units and a `$0.0005326` price unchanged

### ⬜ Phase 3 — Transactions CRUD API · `backend-dotnet`

- [ ] `GET/POST /api/assets`, `GET /api/assets/{id}`
- [ ] `GET/POST/PUT/DELETE /api/transactions`
- [ ] Validation: positive quantity, sell cannot exceed units held, trade date not in future
- [ ] DTOs as records; no EF entities leak past the endpoint boundary
- [ ] Unit tests + `dotnet test` green — first runnable vertical slice

### ⬜ Phase 4 — Market data providers · `backend-dotnet`

**Blocked by:** Twelve Data + CoinGecko API keys

- [ ] `IQuoteProvider` in Application; implementations in Infrastructure
- [ ] `TwelveDataQuoteProvider` — batched `/quote`, `/time_series` for backfill
- [ ] `CoinGeckoQuoteProvider` — all three coins in one `/simple/price` call
- [ ] `TwelveDataFxProvider` — USD/SGD spot + daily history
- [ ] Resilience via `Microsoft.Extensions.Http.Resilience`; API keys redacted from logs
- [ ] Historical backfill job populating `PriceHistory` + `FxRate` from first trade date

### ⬜ Phase 5 — Auto-refresh + SignalR · `backend-dotnet`

- [ ] `IMarketCalendar` — NYSE and SGX hours, weekends, holidays, DST-safe
- [ ] `PriceRefreshService : BackgroundService` — 5 min open / 60 min closed / 2 min crypto
- [ ] `PricesHub` at `/hubs/prices` broadcasting quote + last-refresh updates
- [ ] `POST /api/prices/refresh` with 30s cooldown returning `429` + seconds remaining
- [ ] `GET /api/prices/status`
- [ ] Calendar tests across DST boundaries and weekends in both time zones

### ⬜ Phase 6 — Portfolio calculations · `backend-dotnet`

- [ ] `ICostBasisCalculator` → `AverageCostCalculator` (fees capitalised into basis)
- [ ] Unrealised and realised P&L, including partial sells
- [ ] `PerformanceSeriesBuilder` — cost basis step series vs daily market value, USD-converted
      at each date's historical FX rate
- [ ] `AnnualReturnCalculator` — time-weighted return per calendar year
- [ ] `GET /api/assets/{id}/performance`, `/api/portfolio/{assetClass}/{summary,allocation,annual-returns}`
- [ ] Heavy unit coverage — TWR verified against a hand-computed multi-year example

---

### ⬜ Phase 7 — Angular scaffold + shell · `frontend-angular`

- [ ] `ng new` Angular 22 workspace at `src/Portfolio.Web` (standalone, zoneless, SCSS)
- [ ] Angular Material + ngx-echarts + `@microsoft/signalr`
- [ ] `AppShell` — sidenav (Stocks / Crypto / Transactions) + toolbar
- [ ] Lazy routes: `/stocks`, `/stocks/:symbol`, `/crypto`, `/crypto/:symbol`, `/transactions`,
      parameterised by `assetClass` from route `data`
- [ ] Distinct accent colour per section
- [ ] `PriceStore` — signal-based, SignalR-fed, with polling fallback
- [ ] `RefreshIndicator` in the toolbar top-right — relative timestamp, manual button,
      cooldown countdown on `429`, stale-data warning state
- [ ] Dev proxy to the API; `ng build` clean

### ⬜ Phase 8 — Transactions UI · `frontend-angular`

- [ ] Transactions list with asset-class filter
- [ ] `TransactionFormDialog` — typed reactive form, **quantity to 10 decimal places**
- [ ] Create / edit / delete flows with optimistic UI and error handling
- [ ] Loading, empty, and error states on every data-backed view

### ⬜ Phase 9 — Overview + detail pages · `frontend-angular`

- [ ] Load the `dataviz` skill before writing the first chart config
- [ ] Shared `chart-theme.ts`
- [ ] `PortfolioOverviewPage` — summary tiles, **allocation pie** (cost ⇄ market value toggle),
      **annual return bar chart**, holdings table
- [ ] `AssetDetailPage` — live price header, **cost vs market value line chart** (cost as a
      *step* series) with 1M/3M/1Y/All range selector, gain/loss card, per-asset transactions
- [ ] Gains/losses distinguishable without relying on colour alone

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

- [ ] Enter a fractional crypto buy; confirm it persists and the detail chart redraws
- [ ] `/stocks` and `/crypto` totals are fully independent
- [ ] Timestamp updates across a scheduled refresh with no page reload
- [ ] Z74 quote arrives in SGD and converts at the stored USD/SGD rate
- [ ] Twelve Data credit use over one trading day lands well under 800
- [ ] Same migration set applies cleanly to both SQLEXPRESS and the container DB

---

## Handoff log

| Date | Agent | Phases | Outcome |
|---|---|---|---|
| 2026-07-26 | — | 0 | Repo skeleton, three agent definitions, CLAUDE.md committed (`688b1aa`). Backend blocked on .NET 10 SDK. |

---

## Decisions

Locked in during planning — see `C:\Users\akmal\.claude\plans\memoized-roaming-crane.md`.

- **Stock data:** Twelve Data free tier (only free source covering both US and SGX)
- **Crypto data:** CoinGecko Demo (verified to carry `anvil`/ANVL and `amp-token`)
- **Reporting currency:** USD, with historical FX conversion for Z74
- **Auth:** none, single user
- **Database:** SQL Server — local SQLEXPRESS + Windows Auth for dev, `mssql/server` container
  with SQL auth for the stack. Windows Auth cannot work from a Linux container.
- **Cost basis:** average cost, behind `ICostBasisCalculator` so FIFO can drop in later
- **Annual performance:** time-weighted return, so deposits aren't counted as gains

---

## ▶ Next session

**Terminal 1 — `backend-dotnet`**

First, install the .NET 10 SDK (this session or beforehand):

```
! winget install Microsoft.DotNet.SDK.10
```

Then paste:

```
Read tracker.md. Use the backend-dotnet agent to complete Phases 1 through 3
(backend scaffold, domain + EF Core, transactions CRUD API).

Stop after Phase 3 — do not start Phase 4, it needs API keys I haven't obtained yet.
When done, tick the boxes in tracker.md, append to the handoff log, write the next
session prompt, and commit.
```
