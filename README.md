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
| SQL Server Express | Local development. A container is used for the Podman stack. |
| Podman | Optional — only for the containerized stack. `winget install RedHat.Podman-Desktop` |

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
podman machine init     # first time only
podman machine start

cp .env.example .env    # then fill in the API keys and SA password

podman compose up -d    # http://localhost:8080
```

The stack runs its own SQL Server container rather than your local SQLEXPRESS instance —
Windows Authentication cannot reach into a Linux container. Both use the same EF Core
migrations, so the schema is identical either way.

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
