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
