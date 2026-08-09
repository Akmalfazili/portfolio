# Portfolio Tracker

A personal stock and crypto portfolio tracker. Record buy/sell transactions with fractional
units and transaction costs, pull live market prices automatically, and see cost versus
market value for every holding.

- **Stocks** and **crypto** are tracked in separate sections of the app
- Per-asset page: cost vs market value over time, plus overall gain/loss
- Portfolio overview: allocation pie (cost and market value) and year-on-year performance
- Live prices with an always-visible "last refreshed" indicator and a manual refresh button

All values report in **USD**; SGD holdings are converted at the FX rate for the relevant date.

## Prerequisites

| Requirement | Notes |
|---|---|
| .NET SDK 10.0 LTS | https://dotnet.microsoft.com/download |
| Node.js 20.19+ | Angular 22 requirement |
| SQL Server Express | Local development. A container is used for the Docker stack. |
| Docker Desktop | Optional — only for the containerized stack. `winget install Docker.DockerDesktop` |

You'll also need two free API keys (neither requires a card):

- **Twelve Data** — https://twelvedata.com/pricing — US equities, SGX, and FX
- **CoinGecko Demo** — https://www.coingecko.com/en/api — crypto

## Running locally

```bash
# Store API keys outside source control
dotnet user-secrets set "TwelveData:ApiKey" "<key>" --project src/Portfolio.Api
dotnet user-secrets set "CoinGecko:ApiKey" "<key>" --project src/Portfolio.Api

# Create the database on your local SQLEXPRESS instance (Windows Auth)
dotnet ef database update -p src/Portfolio.Infrastructure -s src/Portfolio.Api

# API
dotnet run --project src/Portfolio.Api

# Frontend, in a second terminal
cd src/Portfolio.Web
npm install
npm start          # http://localhost:4200
```

## Running the containerized stack

```bash
# Docker Desktop must be running (and not paused) before any of this

cp .env.example .env    # then fill in the API keys and BOTH SQL passwords

docker compose up -d    # http://localhost:8080
```

The stack runs its own SQL Server container (`db`) rather than your local SQLEXPRESS instance —
Windows Authentication cannot reach into a Linux container, so this stack uses SQL authentication
instead. Bringing the stack up runs four services in order:

1. **`db`** — SQL Server, gated by a healthcheck that actually probes readiness with `sqlcmd`,
   not just "container started".
2. **`migrate`** — a one-shot service, built from `Dockerfile.migrate`, that applies the exact
   same EF Core migrations `dotnet ef database update` runs locally, then provisions a
   least-privilege `portfolio_app` SQL login (`db_datareader` + `db_datawriter`, no DDL) and
   exits. This is the only container ever handed `MSSQL_SA_PASSWORD`; see `.env.example` for why
   there are two SQL passwords, not one.
3. **`api`** — starts only once `migrate` has exited `0` (`depends_on: condition:
   service_completed_successfully`), and connects as `portfolio_app`, never `sa`.
4. **`web`** — nginx, serving the built Angular app and reverse-proxying `/api` and `/hubs` to
   `api`.

Data lives in the named volume `mssql-data`, not a bind mount — `docker compose down` followed
by `up` preserves it. **Never run `docker compose down -v`** unless you genuinely want to destroy
the database; `-v` deletes that volume.

Want the container to reach your host's SQLEXPRESS instead of the bundled `db` service? That
needs Mixed Mode authentication, TCP/IP enabled, and a host firewall rule on SQLEXPRESS, and the
container-side address is `host.docker.internal,1433`. Documented here as an option, not built —
the default is the bundled `db` service above.

## Price refresh

Prices update automatically on a market-hours-aware schedule, so the free-tier API quotas
aren't burned polling closed exchanges:

| | While the exchange is open | Closed |
|---|---|---|
| US stocks | every 5 min | every 60 min |
| Z74 (SGX) | every 5 min | every 60 min |
| Crypto | every 2 min | every 2 min (24/7) |

Updates push to the browser over SignalR, so the timestamp in the top-right corner stays
current without a page reload. The refresh button beside it forces an immediate update and
is rate-limited to once every 30 seconds.

## Development

See [CLAUDE.md](CLAUDE.md) for architecture, conventions, and the specialised agents that
own each part of the codebase.
