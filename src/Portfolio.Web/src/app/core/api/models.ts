// Mirrors the backend DTOs exactly (src/Portfolio.Application / src/Portfolio.Api).
//
// Enums cross the wire as STRINGS on both REST and SignalR — do not model them as
// numbers. This was a real backend bug (Phase 5) fixed by registering
// JsonStringEnumConverter on both the HTTP and SignalR JSON protocols; the frontend
// contract depends on that fix holding.
//
// Identifiers, by contrast, are C# `int` and cross the wire as JSON NUMBERS — do not
// model them as strings. The API registers no `JsonNumberHandling.AllowReadingFromString`,
// so POSTing `"assetId": "3"` is rejected with a 400 rather than coerced. Ids are
// `number` here for that reason; a select control bound to an asset must send the
// numeric id, not its string form.

export type AssetClass = 'Stock' | 'Crypto';

export type TransactionType = 'Buy' | 'Sell';

export type QuoteProviderKind = 'TwelveData' | 'Yahoo' | 'CoinGecko';

export type RefreshOutcome = 'Completed' | 'NothingDue' | 'CooldownActive';

export interface AssetDto {
  id: number;
  symbol: string;
  name: string;
  assetClass: AssetClass;
  exchange: string | null;
  currency: string;
  quoteProviderKind: QuoteProviderKind;
  providerSymbol: string | null;
  providerCoinId: string | null;
  isActive: boolean;
}

export interface TransactionDto {
  id: number;
  assetId: number;
  assetSymbol: string;
  assetClass: AssetClass;
  type: TransactionType;
  /** A C# `DateOnly`, serialised as a plain "YYYY-MM-DD" string — never an ISO instant. */
  tradeDate: string;
  quantity: number;
  pricePerUnit: number;
  fees: number;
  currency: string;
  notes: string | null;
}

export interface CreateTransactionRequest {
  assetId: number;
  type: TransactionType;
  tradeDate: string;
  quantity: number;
  pricePerUnit: number;
  fees: number;
  currency: string;
  notes: string | null;
}

export type UpdateTransactionRequest = CreateTransactionRequest;

/** SignalR "QuoteUpdated" payload. */
export interface QuoteUpdateNotification {
  assetId: number;
  symbol: string;
  price: number;
  currency: string;
  asOf: string;
}

export interface SourceRefreshStatus {
  source: QuoteProviderKind;
  lastAttemptedAt: string | null;
  lastSuccessAt: string | null;
  lastRunSuccess: boolean;
  lastError: string | null;
  symbolsRefreshed: number;
  nextDueAt: string | null;
}

/** SignalR "RefreshStatus" payload and the body of GET /api/prices/status. */
export interface PriceRefreshStatus {
  lastRefreshedAt: string | null;
  nyseOpen: boolean;
  sgxOpen: boolean;
  nextScheduledRunAt: string | null;
  sources: SourceRefreshStatus[];
}

export interface SourceRefreshOutcome {
  source: QuoteProviderKind;
  attempted: boolean;
  success: boolean;
  symbolsRefreshed: number;
  error: string | null;
}

/** 200 body of POST /api/prices/refresh. */
export interface PriceRefreshCycleResult {
  outcome: RefreshOutcome;
  cooldownSecondsRemaining: number | null;
  sources: SourceRefreshOutcome[];
  totalSymbolsRefreshed: number;
}

/** The `secondsRemaining` extension on the 429 ProblemDetails body. */
export interface RefreshCooldownProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  secondsRemaining: number;
}

/**
 * The 400 body of POST/PUT /api/transactions on any of the three domain
 * rejections — positive quantity, sell cannot exceed units held, trade date
 * not in the future — all keyed under `errors` by request field name
 * (`quantity`, `pricePerUnit`, `fees`, `tradeDate`, `currency`, `assetId`).
 * Standard ASP.NET `ValidationProblemDetails`. PUT additionally returns a
 * bare 404 with no body at all when the row has been deleted underneath the
 * request — that is NOT this shape, and must be handled separately.
 */
export interface ValidationProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  errors: Record<string, string[]>;
  traceId?: string;
}

// -----------------------------------------------------------------------------
// Portfolio calculations (Phase 6 endpoints) — mirrors
// Portfolio.Application/Dtos/PortfolioDtos.cs exactly. All monetary fields are
// USD, already converted server-side — the frontend does no FX math.
// -----------------------------------------------------------------------------

/**
 * One asset's position within a portfolio summary. An asset with a position
 * but no live quote yet reports `currentPriceUsd: null` and
 * `marketValueUsd: 0` — render that as "no price yet", NOT as a 100% loss
 * (the naive `unrealizedPnlPercent` for that row is -100, which is an
 * artefact of costBasis-minus-zero, not a real loss).
 */
export interface HoldingDto {
  assetId: number;
  symbol: string;
  name: string;
  assetClass: AssetClass;
  currency: string;
  quantityHeld: number;
  costBasisUsd: number;
  currentPriceNative: number | null;
  currentPriceUsd: number | null;
  priceAsOf: string | null;
  marketValueUsd: number;
  unrealizedPnlUsd: number;
  unrealizedPnlPercent: number | null;
  realizedPnlUsd: number;
}

/** Portfolio-level totals for one AssetClass — stocks and crypto never aggregate together. */
export interface PortfolioSummaryDto {
  assetClass: AssetClass;
  totalCostBasisUsd: number;
  totalMarketValueUsd: number;
  totalUnrealizedPnlUsd: number;
  totalUnrealizedPnlPercent: number | null;
  totalRealizedPnlUsd: number;
  holdings: HoldingDto[];
}

/** One slice of the allocation pie. Only currently-held (quantity > 0) assets appear. */
export interface AllocationItemDto {
  assetId: number;
  symbol: string;
  name: string;
  marketValueUsd: number;
  percentageOfTotal: number;
}

export interface PortfolioAllocationDto {
  assetClass: AssetClass;
  totalMarketValueUsd: number;
  items: AllocationItemDto[];
}

/**
 * One point on a cost-basis-vs-market-value chart, both USD. `costBasisUsd`
 * is a flat STEP that only moves on transaction dates — render it with a
 * step series, never a smoothed line, or it misrepresents when money
 * actually went in. `marketValueUsd` is a genuine daily line.
 */
export interface PerformancePointDto {
  /** A C# `DateOnly`, serialised as a plain "YYYY-MM-DD" string. */
  date: string;
  costBasisUsd: number;
  marketValueUsd: number;
}

/** Stocks only — requesting this for a crypto asset id is rejected with a 400 before this DTO is built. */
export interface AssetPerformanceDto {
  assetId: number;
  symbol: string;
  name: string;
  currency: string;
  points: PerformancePointDto[];
}

/** Time-weighted return for one calendar year, as a percentage. */
export interface AnnualReturnDto {
  year: number;
  timeWeightedReturnPercent: number;
}

/** Stocks only, across the whole stock portfolio — not per asset. */
export interface AnnualReturnsDto {
  years: AnnualReturnDto[];
}
