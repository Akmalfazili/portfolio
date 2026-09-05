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

/**
 * D20 — distinguishes a genuinely live quote from a read-time fallback to the
 * last stored daily close. `null` means neither exists yet (a brand-new
 * position with no quote and no history at all — "Awaiting price"). A `null`
 * `priceAsOf`/`currentPriceUsd` always pairs with a `null` `priceSource`; a
 * `"Close"` source's `priceAsOf` is the CLOSE's own date, not "now" — never
 * render it as if it were fresh.
 */
export type PriceSource = 'Live' | 'Close' | null;

/**
 * D38 residual — `Queued` (new) is returned when a manual refresh's Twelve
 * Data group needs more than one per-minute credit chunk: the fetch is
 * detached onto a background task and the endpoint returns immediately with
 * `totalSymbolsRefreshed: 0` and an empty `sources` array. That shape is
 * otherwise indistinguishable from "nothing was due", so any consumer that
 * derives its message from `totalSymbolsRefreshed`/`sources` alone (as
 * `describeRefreshOutcome` once did) must switch on `outcome` first — see
 * that file's header comment.
 */
export type RefreshOutcome = 'Completed' | 'NothingDue' | 'CooldownActive' | 'Queued';

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

  /** When the asset was added. Pairs with `hasEverBeenPriced` — see below. */
  createdAt: string;

  /**
   * D27 — false when no source has ever produced a price for this asset:
   * neither a live quote nor a single backfilled close.
   *
   * The gap it covers: `POST /api/assets` rejects a *malformed* record (D23),
   * but cannot cheaply reject a **well-formed but wrong** one — `APPL` for
   * `AAPL`, or a CoinGecko id that does not exist — because checking means
   * calling the provider, which costs a credit per creation. Such an asset is
   * accepted and then shows "Awaiting price" forever, indistinguishable from a
   * closed market.
   *
   * Read it together with `createdAt`: minutes of silence is normal (no refresh
   * cycle has run), days of it means the identifier is probably wrong. It is a
   * hint pointing at the record, not proof the record is invalid — a delisted
   * symbol looks identical.
   */
  hasEverBeenPriced: boolean;

  /**
   * D34 — true when this asset's `quoteProviderKind` has recorded at least one
   * genuinely successful refresh cycle ANYWHERE in this database (across every
   * asset that shares the provider, not only this one) — sourced from
   * `SourceRefreshStates.LastSuccessAt`.
   *
   * This is the gate on `hasEverBeenPriced`'s escalation: a fresh database
   * (or one whose seeded `createdAt` is a static value — see D34) makes every
   * never-priced asset look days old on day one, regardless of whether its
   * identifier is right. If the provider has never once succeeded here, this
   * asset's silence carries no evidence about its identifier, so there is
   * nothing yet to escalate on — suppress the amber "check this identifier"
   * state and keep only the calm "no price yet" one until this flips true.
   */
  providerHasEverSucceeded: boolean;

  /**
   * Recurring calendar month/day of this company's financial year end — see
   * `zakat.md` §3.1. `null` means "not configured", a distinct reported
   * state (`ZakatAssetStatus.FiscalYearEndNotConfigured`) never treated as
   * "assume 31 Dec". ALWAYS `null` for `Crypto` — crypto has no financial
   * year. Either both this and `fiscalYearEndDay` are set, or both are
   * `null`; the server rejects one-of-two with a 400 keyed on
   * `fiscalYearEndMonth`.
   */
  fiscalYearEndMonth: number | null;
  /** Day of month, 1-31 — see `fiscalYearEndMonth`. 29 February is allowed
   *  (clamped to 28 Feb in non-leap years at resolution time); `2/30`,
   *  `4/31` and similar are rejected server-side. */
  fiscalYearEndDay: number | null;
}

/**
 * D23/D24 — `quoteProviderKind` dictates which identifier field is required:
 * `providerSymbol` for `TwelveData`/`Yahoo`, `providerCoinId` for
 * `CoinGecko`. The server is the authority on this rule (`400`
 * `ValidationProblemDetails` keyed by field name on a mismatch) — the
 * frontend only guides the user toward the right shape, never blocks on its
 * own copy of the rule. See tracker.md's D24 design note for the full
 * asset-class -> provider -> identifier-field routing table.
 */
export interface CreateAssetRequest {
  symbol: string;
  name: string;
  assetClass: AssetClass;
  exchange: string | null;
  currency: string;
  quoteProviderKind: QuoteProviderKind;
  providerSymbol: string | null;
  providerCoinId: string | null;
  /** See `AssetDto.fiscalYearEndMonth`. Both-or-neither; always `null` for `Crypto`. */
  fiscalYearEndMonth: number | null;
  fiscalYearEndDay: number | null;
}

/** PUT /api/assets/{id} is a FULL REPLACE — the only way to flip `isActive`;
 *  there is no separate deactivate/reactivate route. */
export interface UpdateAssetRequest extends CreateAssetRequest {
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

/**
 * SignalR "QuoteUpdated" payload.
 *
 * D4 — `source` is not decoration. A pushed quote is *freshly fetched*, which
 * is not the same as *fresh*: the refresh service polls whenever its calendar
 * believes a market is open, and on an unmodelled SGX lunar holiday that belief
 * is wrong, so Yahoo answers with the previous session's close and it arrives
 * here looking exactly like a live tick. The backend classifies it (same rule,
 * same code path as `HoldingDto.priceSource`) precisely so the browser does not
 * have to reimplement exchange-session arithmetic to tell the difference.
 * Never assume a push is live because it arrived.
 */
export interface QuoteUpdateNotification {
  assetId: number;
  symbol: string;
  price: number;
  currency: string;
  asOf: string;
  source: Exclude<PriceSource, null>;
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
  /**
   * Blended average cost per unit of `quantityHeld`, USD, rounded to 10 dp
   * server-side (`costBasisUsd / quantityHeld`). `null` when `quantityHeld`
   * is 0 — a fully sold-down position has no average cost — NOT because of
   * a missing price, so this must never be gated behind `priceSource`.
   */
  averageCostUsd: number | null;
  currentPriceNative: number | null;
  currentPriceUsd: number | null;
  priceAsOf: string | null;
  priceSource: PriceSource;
  marketValueUsd: number;
  unrealizedPnlUsd: number;
  unrealizedPnlPercent: number | null;
  realizedPnlUsd: number;

  /**
   * Dividend income tracking (2026-08-28) — `null` for every `Crypto` holding
   * (crypto pays no dividends and keeps no such data at all), and the two USD
   * amounts are `null` (never `0`) for a `Stock` holding whose
   * `dividendCoverageStatus` is `NotYetFetched` — a real `0` means "fetched
   * and genuinely pays nothing," which is a different fact from "not fetched
   * yet." See `AssetDividendHistoryDto` for the per-payment detail behind
   * these totals.
   */
  dividendsTrailing12MonthUsd: number | null;
  dividendsAllTimeUsd: number | null;
  dividendCoverageStatus: DividendCoverageStatus | null;
}

/**
 * Portfolio-level totals for one AssetClass — stocks and crypto never
 * aggregate together. `unpricedHoldingsCount` (D17) is how many holdings
 * have neither a live quote nor a stored close (`priceSource: null`) and so
 * contribute `0` to every total above — when it is greater than zero, the
 * totals are partial and must be captioned as such rather than presented as
 * a complete, accurate portfolio value.
 */
export interface PortfolioSummaryDto {
  assetClass: AssetClass;
  totalCostBasisUsd: number;
  totalMarketValueUsd: number;
  totalUnrealizedPnlUsd: number;
  totalUnrealizedPnlPercent: number | null;
  totalRealizedPnlUsd: number;
  unpricedHoldingsCount: number;
  holdings: HoldingDto[];

  /**
   * Portfolio-level dividend totals — `null` for a `Crypto` summary (crypto
   * pays no dividends). `dividendsUncoveredCount` is how many `Stock`
   * holdings are NOT `Covered` (`NotYetFetched` or `FetchFailed`); when it is
   * greater than zero the two totals above are partial and must be captioned
   * as such — the same "zero-contribution total plus a separate caveat
   * count" shape as `unpricedHoldingsCount` (D17), applied to dividend
   * income instead of market value.
   */
  totalDividendsTrailing12MonthUsd: number | null;
  totalDividendsAllTimeUsd: number | null;
  dividendsUncoveredCount: number;
}

/** One slice of the allocation pie. Only currently-held (quantity > 0) assets appear. */
export interface AllocationItemDto {
  assetId: number;
  symbol: string;
  name: string;
  marketValueUsd: number;
  percentageOfTotal: number;

  /**
   * D17 residual — false when this holding has no price from any source, so
   * `marketValueUsd` is 0 because the value is *unknown*, not because the
   * position is worthless. Render those two cases differently: a `0.0%` that
   * means ignorance must not look like a `0.0%` that means a tiny position.
   *
   * Only meaningful for the market-value pie. Cost basis is known for every
   * holding, so a cost-basis allocation has nothing to caveat.
   */
  hasPrice: boolean;
}

export interface PortfolioAllocationDto {
  assetClass: AssetClass;
  totalMarketValueUsd: number;
  items: AllocationItemDto[];

  /**
   * D17 residual — how many `items` have `hasPrice: false`. Carried on the
   * allocation payload itself, not just the summary's own
   * `unpricedHoldingsCount`, so a caller that fetches only this endpoint can
   * still tell that the pie is partial without a second request.
   */
  unpricedHoldingsCount: number;
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

// -----------------------------------------------------------------------------
// Dividend income tracking (2026-08-28). Stocks only — `GET
// /api/assets/{id}/dividends` rejects a crypto asset id with a 400 before any
// DTO is built, and every dividend field on `HoldingDto`/`PortfolioSummaryDto`
// is `null` for `Crypto`. These figures are COMPUTED from units held on each
// ex-date, never recorded cash actually received — they exclude withholding
// tax, DRIP and scrip handling, so the UI must caption them as an estimate,
// never present them as a broker statement.
// -----------------------------------------------------------------------------

/**
 * `NotYetFetched` and a real `Covered` zero (a stock that pays no dividend)
 * must never look the same — `NotYetFetched`'s two USD totals are `null`,
 * `Covered`'s are real numbers (including a legitimate `0`). `FetchFailed`
 * means the last attempt errored, distinct from both.
 */
export type DividendCoverageStatus = 'Covered' | 'NotYetFetched' | 'FetchFailed';

/**
 * One dividend payment, newest ex-date first in `AssetDividendHistoryDto.payments`.
 * `amountPerShareNative` is in the asset's OWN `currency` (Z74 pays in SGD) —
 * never render it with `MoneyPipe`'s USD default. `incomeUsd` is the already-
 * converted USD figure (`amountPerShareNative * unitsHeldAtExDate`, FX-converted
 * at the ex-date's own rate) — the frontend does no FX math itself.
 */
export interface DividendPaymentDto {
  /** A C# `DateOnly`, serialised as a plain "YYYY-MM-DD" string — never round-tripped through `toISOString()`. */
  exDate: string;
  amountPerShareNative: number;
  currency: string;
  unitsHeldAtExDate: number;
  incomeUsd: number;
}

/** Stocks only — requesting this for a crypto asset id is rejected with a 400 before this DTO is built; an unknown id is a 404. */
export interface AssetDividendHistoryDto {
  assetId: number;
  symbol: string;
  name: string;
  currency: string;
  trailing12MonthIncomeUsd: number | null;
  allTimeIncomeUsd: number | null;
  coverageStatus: DividendCoverageStatus;
  /** Newest ex-date first. */
  payments: DividendPaymentDto[];
}

// -----------------------------------------------------------------------------
// Zakat on shares (zakat.md) — mirrors Portfolio.Application/Dtos/ZakatDtos.cs.
// GET /api/zakat is the ONE sanctioned exception to asset-class segregation:
// it returns stocks AND crypto in a single response because MUIS requires a
// single grand total, but they stay in separate sub-objects with separate
// subtotals so nothing aggregates implicitly. This report is denominated in
// SGD — the app's usual USD reporting currency does not apply here, and
// MoneyPipe must be called with an explicit 'SGD' currency, never its
// default. Nisab is deliberately NOT encoded anywhere on this page (zakat.md
// §2.4) — only the zakatable total and 2.5% of it are shown; the nisab
// comparison against the current zakat.sg figure is the user's own to make.
// -----------------------------------------------------------------------------

/**
 * Six outcomes, never one "skipped" bucket (zakat.md §6 — the D10/D26/D33/
 * D35/D38/D45 defect family). `NotHeldAtFiscalYearEnd` (a correct zero,
 * counted) and `FiscalYearEndNotConfigured` (a missing input, excluded) must
 * never be presented the same way in the UI.
 */
export type ZakatAssetStatus =
  | 'Included'
  | 'NotHeldAtFiscalYearEnd'
  | 'FiscalYearEndNotConfigured'
  | 'NoCloseOnOrBeforeFiscalYearEnd'
  | 'NoFxRateForCloseDate'
  | 'NoQuote';

/**
 * One stock asset's zakat line, valued at ITS OWN last fiscal year end
 * (zakat.md §2.2/§2.7 — the total is a sum across different dates by
 * design). EVERY stock asset appears here, including one holding zero units
 * today (zakat.md §2.8) — never filtered to current holdings. Nullable
 * fields are genuinely `null` (never `0`) unless `status` is `Included`,
 * except `quantityHeld`/`valueSgd`, which are legitimately `0` for
 * `NotHeldAtFiscalYearEnd`.
 */
export interface ZakatStockLineDto {
  assetId: number;
  symbol: string;
  name: string;
  currency: string;
  status: ZakatAssetStatus;
  /** Null only when `status` is `FiscalYearEndNotConfigured`. */
  fiscalYearEndDate: string | null;
  /** Units held on `fiscalYearEndDate`. Null only when that date itself is null. */
  quantityHeld: number | null;
  /** Closing price in `currency`. Non-null only when `status` is `Included`. */
  closeNative: number | null;
  /** The `PriceHistory` date actually used — the greatest date at or before
   *  `fiscalYearEndDate`. May differ from it; see `closeDateExact`. */
  closeDateUsed: string | null;
  /** `false` means `closeDateUsed` was carried forward from an earlier
   *  trading day (weekend/holiday, or a year end landing on today, which has
   *  no close of its own yet) — must be surfaced, never presented as exact. */
  closeDateExact: boolean | null;
  /** The USD/SGD `FxRate` date actually used. Null for an SGD-native asset
   *  (no FX conversion at all) and null unless `status` is `Included`. */
  fxDateUsed: string | null;
  /** `true` means no FX rate existed at or before the close date at all, and
   *  the resolver fell back to the EARLIEST rate on file — a real data gap,
   *  not the ordinary weekend carry-forward. Must be surfaced. */
  fxCarriedBack: boolean | null;
  /** The USD/SGD rate actually applied to convert `closeNative` — SGD per
   *  USD, `FxRate.Rate`'s own convention (zakat.md §4.3: this report
   *  MULTIPLIES by it, the opposite of every other USD-to-native converter
   *  in the codebase). `null` for an SGD-native asset (Z74) — that path runs
   *  no FX conversion at all, so there is no rate to show; this is NOT the
   *  same thing as a rate of `1.0` and must never render as one. Also
   *  `null` for every non-`Included` status. */
  fxRateUsed: number | null;
  /** This line's contribution to the total, in SGD. `0` for
   *  `NotHeldAtFiscalYearEnd` (a real, counted zero); `null` for every other
   *  non-`Included` status — excluded entirely, never presented as zero. */
  valueSgd: number | null;
}

/**
 * Provenance of a crypto line's USD/SGD rate (zakat.md §12). Three-way, not
 * two, for the same reason as `ZakatAssetStatus` (§6, the D10/D26/D33/D35/D45
 * defect family): "not attempted" and "attempted and failed" must never share
 * a bucket, because collapsing them is how a silent staleness bug hides
 * behind a report that otherwise looks healthy.
 */
export type ZakatFxSource =
  | 'Spot' // Live Twelve Data /exchange_rate rate, cached 15 minutes.
  // `?asOf=` names a past date, so a live spot was never the right thing to
  // fetch in the first place — a daily close is correct here, not a fallback.
  | 'DailyCloseHistoricalAsOf'
  // The live spot WAS attempted, for today's date, and failed — this is the
  // stored daily close standing in for it. A warning: the rate may be stale.
  | 'DailyCloseSpotUnavailable';

/**
 * One crypto asset's zakat line. Crypto follows NO MUIS ruling (zakat.md
 * §2.3 — a user convention, not a ruling, and must be labelled as such) and
 * is valued at TODAY's price, never a fiscal year end — crypto has none and
 * keeps no price history to look one up in.
 */
export interface ZakatCryptoLineDto {
  assetId: number;
  symbol: string;
  name: string;
  currency: string;
  status: Extract<ZakatAssetStatus, 'Included' | 'NoQuote'>;
  /** Always computed regardless of `status` — nothing about resolving this can fail. */
  quantityHeld: number;
  /** Null only when `status` is `NoQuote`. */
  priceUsd: number | null;
  priceSource: PriceSource;
  priceAsOf: string | null;
  fxDateUsed: string | null;
  fxCarriedBack: boolean | null;
  /** The USD/SGD rate actually applied — SGD per USD (zakat.md §4.3).
   *  Crypto has no SGD-native case, so unlike the stock line's
   *  `fxRateUsed` this is only ever `null` for a non-`Included` status. */
  fxRateUsed: number | null;
  /** The provider's own timestamp for a live `/exchange_rate` spot — a real
   *  `DateTimeOffset`, e.g. `"2026-09-05T11:31:00+00:00"`. `null` when the
   *  rate came from a daily close instead: a close has no time of day, so
   *  this null means "daily close", never "unknown" or "not attempted" —
   *  see `fxSource` for which of those a null `fxRateUsed` actually is. */
  fxAsOf: string | null;
  /** Which of the three ways this line's rate was resolved — see
   *  `ZakatFxSource`. `null` only alongside a `null` `fxRateUsed` (a
   *  non-`Included` status has no rate to attribute a source to). */
  fxSource: ZakatFxSource | null;
  valueSgd: number | null;
}

/**
 * GET /api/zakat?asOf=YYYY-MM-DD (asOf optional — defaults to today
 * server-side). Nothing here is persisted; it is computed fresh on every
 * read. `excludedAssetCount` is a caveat, same shape as
 * `PortfolioSummaryDto.unpricedHoldingsCount` — a non-zero count means the
 * total is knowingly incomplete and must say so.
 */
export interface ZakatReportDto {
  /** The valuation reference date this report was computed against — used
   *  as "today" for crypto and as the point each fiscal year end resolves
   *  backward from. NOT a single valuation date for every line — see each
   *  line's own date fields. */
  asOf: string;
  stocks: ZakatStockLineDto[];
  crypto: ZakatCryptoLineDto[];
  stockZakatableSgd: number;
  /** Labelled separately from `stockZakatableSgd` on purpose — the crypto
   *  valuation basis is a user convention, not a MUIS ruling (zakat.md §2.3),
   *  and must never be presented with the same authority as the share figure. */
  cryptoZakatableSgd: number;
  totalZakatableSgd: number;
  /** 2.5% of `totalZakatableSgd`. The nisab comparison is deliberately NOT
   *  made here or anywhere in this UI — never render a "you owe / you don't
   *  owe" verdict, and never hard-code a nisab figure. */
  zakatPayableSgd: number;
  /** Count of assets (stock + crypto) excluded from the total entirely —
   *  every status except `Included`/`NotHeldAtFiscalYearEnd`. */
  excludedAssetCount: number;
}

/**
 * A zakat payment actually made — a recorded FACT, never derived from
 * `ZakatReportDto` (zakat.md §3.2). Must never be written from the computed
 * report, the same rule as "a close is never written into PriceQuote."
 */
export interface ZakatPaymentDto {
  id: number;
  /** A C# `DateOnly`, "YYYY-MM-DD" — never round-tripped through
   *  `toISOString()` (SGT is UTC+8; same trap as `Transaction.tradeDate`). */
  paidOn: string;
  amountSgd: number;
}

export interface CreateZakatPaymentRequest {
  paidOn: string;
  amountSgd: number;
}

export type UpdateZakatPaymentRequest = CreateZakatPaymentRequest;
