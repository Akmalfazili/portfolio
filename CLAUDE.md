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
| Containers | Docker (Desktop, WSL2 backend), `compose.yaml` (db / migrate / api / web) |

## Layout

```
src/Portfolio.Domain/          entities + enums, no dependencies
src/Portfolio.Application/     services, DTOs, calculators, provider interfaces
src/Portfolio.Infrastructure/  EF Core DbContext + migrations, provider clients
src/Portfolio.Api/             minimal API endpoints, SignalR hub, background services
src/Portfolio.Web/             Angular workspace
docker/db-init/                Portfolio.DbInit — the one-shot migration runner (not in the .slnx)
tests/Portfolio.UnitTests/
tests/Portfolio.IntegrationTests/
```

Dependencies point inward. `Portfolio.Domain` references nothing.

## Status and reference

**Development is complete.** All phases and every defect (D1–D39) are closed. The one open
item is **D6**, which is a *measurement* on hold, not a defect: Twelve Data credit use has
never been measured across a full NYSE trading day.

**[tracker.md](tracker.md) is the development record** — what shipped, what was decided and
why, what broke and what the breakage taught. **Read the area you are about to change before
changing it.** Most of the sharp edges in this project are invisible in the code, and each one
cost a real debugging session to find. Update it when behaviour changes.

Work is split by area. Delegate to the specialised agent that owns it rather than working
across boundaries:

- **`backend-dotnet`** — anything under `src/Portfolio.{Domain,Application,Infrastructure,Api}` or `tests/`
- **`frontend-angular`** — anything under `src/Portfolio.Web`
- **`container-docker`** — `Dockerfile.*`, `compose.yaml`, `nginx.conf`, Docker operations

Verify claims against running output, not against tests alone. Confident arithmetic has been
disproved by measurement five separate times here; four of the worst defects were invisible to
every passing test and only surfaced by looking at a real browser or a real API response. Say
plainly what you did *not* verify.

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

**The unit tests cannot prove this.** The EF Core InMemory provider does not enforce precision,
so `tests/Portfolio.IntegrationTests` against real SQL Server is the actual tripwire — keep it
green. `DisplayRounding` rounds only at the DTO boundary (money 4 dp, price 10 dp, percent
4 dp) and never inside a calculator, so rounding happens once, at the edge.

### Dual connection string

One codebase, one migration set, two environments:

- Local `dotnet run` → `Server=localhost\SQLEXPRESS;...;Trusted_Connection=True` (Windows Auth)
- Docker → SQL auth against the `db` service via the `ConnectionStrings__Portfolio` env var

Windows Authentication cannot work from a Linux container — never assume it's available.

### Asset class segregation

`AssetClass` (`Stock` | `Crypto`) is filtered on in every portfolio-level query. Stocks and
crypto have separate navigation, separate totals, and never aggregate together.

**Crypto is gain/loss only and keeps no price history at all** — a locked decision, not a gap.
`PriceBackfillService` filters to `AssetClass.Stock`, and the cost-vs-market and annual-return
components are never imported into the crypto path rather than rendering empty. This is a
one-way door for the past: unrecorded days cannot be bought back from CoinGecko's free tier.

### Currency

Transactions are stored in their native currency (USD for US stocks and crypto, SGD for Z74).
All reporting converts to USD. Historical series must use the FX rate **for that date**, not
today's rate. The frontend does no FX math — the backend has already converted.

### Wire contract

**Enums are strings, ids are numbers** — `"Crypto"`/`"Buy"` cross the wire as strings via a
global `JsonStringEnumConverter`, while ids are C# `int` with no `AllowReadingFromString`, so
`"assetId": "3"` is rejected with a `400` rather than coerced. Easy to get exactly backwards.

**SignalR does not inherit `ConfigureHttpJsonOptions`** — it has its own protocol serializer,
so the hub needs `AddJsonProtocol(…JsonStringEnumConverter…)` of its own or an enum crosses as
an int on one transport and a string on the other. Guarded by `PricesHubProtocolTests`.

`tradeDate` is a `DateOnly` serialised `YYYY-MM-DD` — never round-trip it through
`toISOString()`, which shifts it a day at any positive UTC offset.

### Price honesty

A close is **never** written into `PriceQuote`. The last-close fallback happens at read time and
carries `priceSource: "Live" | "Close"` plus the close's **own** `priceAsOf` date, so a stale
price can never masquerade as current. Any consumer must treat `"Close"` as stale data.

A held position with no quote reports `currentPriceUsd: null` and `marketValueUsd: 0`, never an
error, and the UI renders "Awaiting price" — never the naive −100% the raw numbers would read
as. Totals carry an unpriced-holdings caveat rather than being quietly wrong.

Outcome lists must distinguish **"not attempted"** from **"attempted and failed"**. A skip list
whose name asserts a reason is how a silent data-staleness bug hides behind a healthy report —
this is the most repeated defect family in this project (D10, D26, D33, D35, D38).

### Design tokens

`src/Portfolio.Web/src/styles/ui.tokens.scss` is the single source of design truth. New UI
*reads* tokens; it does not introduce values. Angular Material is **derived from** it — a
`--mat-sys-*` block repoints every colour Material paints at the `--ui-*` tokens, so there are
never two palettes drifting apart. No hex colour or raw `px` outside the token files (`1px`
hairline borders excepted); breakpoints are the one deliberate exception, as SCSS variables in
`ui.mixins.scss`, because `@media` cannot read a custom property.

### Rate limits

Twelve Data free tier is 800 credits/day, and the per-minute ceiling is **8 credits/min — not
8 requests/min**. Each symbol in a batch costs one credit, so a single `/quote` (or `/time_series`)
request that carries more than 8 symbols spends more than 8 credits in one shot and 429s
immediately, even though it is the only request in its minute (D38). Never batch every symbol into
one request — chunk to at most 8 symbols per request and pace successive chunks through the shared
credit throttle (`ITwelveDataCreditThrottle`), which both the quote path and the historical
backfill path go through. CoinGecko runs **keyless** here — its public limit is ~10–30 req/min,
IP-based and request-denominated, unaffected by any of the above and fine at one call per two
minutes. Always check the market calendar before spending credits, and cache aggressively.

The credit ledger **reconciles against Twelve Data's own counter** rather than trusting itself:
a new UTC day's row is seeded from `GET /api_usage` instead of zero, and reconciled hourly.
Reconciliation is periodic, never per call — `/api_usage` costs a credit itself. `GET
/api/prices/status` is deliberately outside that path and can **never** cost a credit, which
makes it the safe thing to poll.

## Market data

| Source | Covers |
|---|---|
| Twelve Data | US equities, USD/SGD FX |
| Yahoo Finance | SGX `Z74.SI` only |
| CoinGecko | `ethereum`, `amp-token`, `anvil` — all three in one call |

Routing is an explicit per-asset field (`Asset.QuoteProviderKind`), never inferred from currency,
exchange, or symbol shape. **Z74 does not go to Twelve Data and cannot**: the free tier 404s on it
with *"available starting with the Pro or Venture plan"*, verified live. `Z74:XSES` is Twelve Data's
syntax and is dead for this asset; Yahoo's chart endpoint resolves the SGX listing as `Z74.SI`.
Yahoo has no key, no SLA, and no batch form, so every call is treated as fallible and a failure
falls back to the last stored quote rather than taking down a multi-asset refresh. It is also
**free** — Z74 costs zero Twelve Data credits, which matters whenever credit arithmetic is done.

Prices refresh automatically via a market-hours-aware background service (5 min floor while the
relevant exchange is open — Twelve Data's actual interval is derived at runtime from the live
active-symbol count and the remaining daily credit budget, and widens automatically as the
portfolio grows past ~21 symbols, see `TwelveDataCadenceCalculator` — 60 min when closed, 2 min for
crypto) and broadcast over SignalR. `POST /api/prices/refresh` triggers a manual refresh behind a
30-second cooldown; if that refresh would need to pace a large Twelve Data batch across several
minutes, it is queued onto a background task and the endpoint returns promptly instead of blocking.

## Containers

Migrations run in a one-shot **`migrate`** service, not from the API at startup; `api` gates on
`service_completed_successfully` so it never starts against an unmigrated database. Migration
problems surface in `docker compose logs migrate`, never `logs api`.

Base images must be the **`-noble-chiseled-extra`** variants. The plain chiselled tags set
`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true`, under which `Microsoft.Data.SqlClient` cannot open
a SQL Server connection at all. `Dockerfile.web`'s Node base must be **22.22.3 or newer** —
Angular 22's CLI hard-refuses anything older, so pin the patch.

The app connects as the least-privilege `portfolio_app` login; only `migrate` ever receives
`MSSQL_SA_PASSWORD`. `docker compose down` (never `-v`) preserves the `mssql-data` volume.

A compose `.env` **cannot omit an empty variable** — every form still emits `VAR=` into the
container — so present-but-empty config must be handled in code, not worked around in YAML.

## Commands

```bash
dotnet build portfolio.slnx && dotnet test portfolio.slnx   # always pass the .slnx explicitly
dotnet ef database update -p src/Portfolio.Infrastructure -s src/Portfolio.Api
dotnet run --project src/Portfolio.Api --launch-profile http   # API on http://localhost:5100

cd src/Portfolio.Web && npm start               # Angular dev server on :4200
cd src/Portfolio.Web && npm run build && npm test

docker compose up -d                            # full stack on http://localhost:8080
```

Pass `--launch-profile http` (or plain `dotnet run`). Suppressing the launch profile leaves
`ASPNETCORE_ENVIRONMENT` unset, so `appsettings.Development.json` never loads and the API dies
with *"Connection string 'Portfolio' is not configured"* — which reads like a missing secret
rather than a missing environment.

## Secrets

API keys come from `dotnet user-secrets` locally and `.env` (gitignored) under compose.
CoinGecko needs no key. Never commit a key, write one into `appsettings.json`, or log one —
and **never list user-secrets to read a key back**: a redaction regex once failed to match
PowerShell's `Key = Value` spacing and printed a real key into a transcript. Read the single
value you need and pipe it straight into the call, or let the app make the call for you.
