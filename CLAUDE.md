# Portfolio Tracker

Personal stock & crypto portfolio tracker. Records buy/sell transactions with fractional
units and fees, pulls live market prices, and reports cost vs market value per asset plus
allocation and year-on-year performance per portfolio.

Single user, no authentication. Reporting currency is **USD**.

## Stack

| Layer | Technology |
|---|---|
| Backend | .NET 10 LTS, ASP.NET Core Minimal APIs, EF Core 10 (SQL Server), SignalR |
| Frontend | Angular 22 (standalone, signals, zoneless), Angular Material, ngx-echarts |
| Database | SQL Server — local `SQLEXPRESS` for dev, `mssql/server` container for the stack |
| Containers | Docker (Desktop, WSL2 backend), `compose.yaml` (db / api / web) |

## Layout

```
src/Portfolio.Domain/          entities + enums, no dependencies
src/Portfolio.Application/     services, DTOs, calculators, provider interfaces
src/Portfolio.Infrastructure/  EF Core DbContext + migrations, provider clients
src/Portfolio.Api/             minimal API endpoints, SignalR hub, background services
src/Portfolio.Web/             Angular workspace
tests/Portfolio.UnitTests/
tests/Portfolio.IntegrationTests/
```

Dependencies point inward. `Portfolio.Domain` references nothing.

## Agents and progress

**Read [tracker.md](tracker.md) at the start of every session** — it holds current phase
status, blocking prerequisites, and the handoff log. Update it before ending a session.

Work is split across terminal sessions, one agent per session. Delegate to the specialised
agent that owns the area rather than working across boundaries:

- **`backend-dotnet`** — anything under `src/Portfolio.{Domain,Application,Infrastructure,Api}` or `tests/`
- **`frontend-angular`** — anything under `src/Portfolio.Web`
- **`container-docker`** — `Dockerfile.*`, `compose.yaml`, `nginx.conf`, Docker operations

When a phase completes and the next belongs to a different agent, stop, update `tracker.md`,
and issue a handoff prompt for the next terminal.

## Critical conventions

### Decimal precision

SQL Server's EF Core default is `decimal(18,2)`, which silently destroys this domain's data:
the ANVL token trades near $0.0005326 and would round to `0.00`, and fractional crypto units
would truncate. Every decimal column is configured explicitly in `OnModelCreating`:

| Kind | Precision |
|---|---|
| Quantities, unit prices, closes | `decimal(28,10)` |
| Fees, monetary totals | `decimal(19,4)` |
| FX rates | `decimal(18,8)` |

The same applies in the frontend: no validator, input step, or display pipe may assume
integers or two decimal places.

### Dual connection string

One codebase, one migration set, two environments:

- Local `dotnet run` → `Server=localhost\SQLEXPRESS;...;Trusted_Connection=True` (Windows Auth)
- Docker → SQL auth against the `db` service via the `ConnectionStrings__Portfolio` env var

Windows Authentication cannot work from a Linux container — never assume it's available.

### Asset class segregation

`AssetClass` (`Stock` | `Crypto`) is filtered on in every portfolio-level query. Stocks and
crypto have separate navigation, separate totals, and never aggregate together.

### Currency

Transactions are stored in their native currency (USD for US stocks and crypto, SGD for Z74).
All reporting converts to USD. Historical series must use the FX rate **for that date**, not
today's rate. The frontend does no FX math — the backend has already converted.

### Rate limits

Twelve Data free tier is 800 credits/day and 8 req/min, and each symbol in a batch costs a
credit. CoinGecko Demo is 30/min. Always batch symbols into one request, always check the
market calendar before spending credits, and cache aggressively.

## Market data

| Source | Covers |
|---|---|
| Twelve Data | US equities, SGX `Z74:XSES`, USD/SGD FX |
| CoinGecko | `ethereum`, `amp-token`, `anvil` — all three in one call |

Prices refresh automatically via a market-hours-aware background service (5 min while the
relevant exchange is open, 60 min when closed, 2 min for crypto) and broadcast over SignalR.
`POST /api/prices/refresh` triggers a manual refresh behind a 30-second cooldown.

## Commands

```bash
dotnet build && dotnet test                     # backend
dotnet ef database update -p src/Portfolio.Infrastructure -s src/Portfolio.Api
dotnet run --project src/Portfolio.Api          # API on https://localhost:7xxx

cd src/Portfolio.Web && npm start               # Angular dev server on :4200
cd src/Portfolio.Web && npm run build && npm test

docker compose up -d                            # full stack on http://localhost:8080
```

## Secrets

API keys come from `dotnet user-secrets` locally and `.env` (gitignored) under compose.
Never commit a key, write one into `appsettings.json`, or log one.
