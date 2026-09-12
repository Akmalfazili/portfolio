import { PriceRefreshStatus, QuoteProviderKind } from '../api/models';
import { joinWithAnd, SOURCE_MARKET_LABEL, SOURCE_MARKET_TITLE } from './refresh-outcome';
import { formatDueIn, formatRelativeTime } from '../../shared/time/relative-time';

/**
 * Backs the refresh-details panel opened from the toolbar's "Updated N ago"
 * trigger — the D5 messaging that used to be discoverable only on hover,
 * after the click, moved to something readable *before* pressing refresh.
 *
 * Pure and unit-testable on its own, same shape as `refresh-outcome.ts`.
 *
 * Honesty constraints this file exists to enforce (D10/D26/D33/D35/D38/D45
 * defect family — "not attempted" must never collapse into "zero" or
 * "failed"):
 *  - A provider absent from `status.sources` (a brand-new install, before its
 *    first ever cycle) renders as "no data yet" — never a confident zero and
 *    never presented as a failure.
 *  - `willRefresh` reflects ONLY the market gate (D5) — crypto is always on;
 *    US/SGX depend on `nyseOpen`/`sgxOpen`. It says nothing about the
 *    cooldown/in-flight state, which the button itself already disables for.
 *  - A failed last attempt (`lastRunSuccess: false` with a `lastError`) is
 *    surfaced distinctly from "market closed, not attempted" — never the same
 *    row state.
 */
const MARKET_ORDER: readonly QuoteProviderKind[] = ['TwelveData', 'Yahoo', 'CoinGecko'];

const MAX_ERROR_SUMMARY_LENGTH = 80;

export interface MarketRefreshRow {
  readonly provider: QuoteProviderKind;
  /**
   * The row TITLE — from `SOURCE_MARKET_TITLE`, NOT `SOURCE_MARKET_LABEL`.
   * A title sits alone in its own column ("US stocks" / "SGX" / "Crypto"),
   * never mid-sentence, so it needs its own title-cased map; see that map's
   * comment in `refresh-outcome.ts` for why the two must not be merged.
   */
  readonly label: string;
  /** Crypto only — there is no market-hours concept for it at all. */
  readonly alwaysOpen: boolean;
  readonly isOpen: boolean;
  /** What pressing refresh right now will do to this market — the whole point of this row. */
  readonly willRefresh: boolean;
  /** False when this provider has never once been recorded — "no data yet", not a zero. */
  readonly hasData: boolean;
  readonly lastSuccessLabel: string;
  readonly nextDueLabel: string;
  /** Twelve Data's own automatic cadence, when the backend has reported it. Null otherwise. */
  readonly cadenceLabel: string | null;
  /** True only when the source WAS attempted and failed — never true for "not attempted". */
  readonly attemptedAndFailed: boolean;
  /** Short, truncated summary — never the raw provider error string. */
  readonly errorSummary: string | null;
}

export function buildMarketRefreshRows(
  status: PriceRefreshStatus,
  nowMs: number,
): MarketRefreshRow[] {
  return MARKET_ORDER.map((provider) => buildRow(provider, status, nowMs));
}

function buildRow(
  provider: QuoteProviderKind,
  status: PriceRefreshStatus,
  nowMs: number,
): MarketRefreshRow {
  const alwaysOpen = provider === 'CoinGecko';
  const isOpen = alwaysOpen ? true : provider === 'TwelveData' ? status.nyseOpen : status.sgxOpen;
  const willRefresh = alwaysOpen || isOpen;

  const source = status.sources.find((s) => s.source === provider);
  const hasData = source !== undefined;
  const attemptedAndFailed = hasData && !source.lastRunSuccess && source.lastError != null;

  return {
    provider,
    label: SOURCE_MARKET_TITLE[provider],
    alwaysOpen,
    isOpen,
    willRefresh,
    hasData,
    lastSuccessLabel: hasData ? formatRelativeTime(source.lastSuccessAt, nowMs) : 'no data yet',
    nextDueLabel: hasData ? formatDueIn(source.nextDueAt, nowMs) : 'not yet scheduled',
    cadenceLabel:
      provider === 'TwelveData' && status.effectiveTwelveDataIntervalSeconds != null
        ? formatIntervalLabel(status.effectiveTwelveDataIntervalSeconds)
        : null,
    attemptedAndFailed,
    errorSummary: attemptedAndFailed ? summarizeError(source.lastError) : null,
  };
}

function formatIntervalLabel(seconds: number): string {
  if (seconds < 60) {
    return `every ~${seconds}s while open`;
  }
  const minutes = Math.round(seconds / 60);
  return `every ~${minutes} min while open`;
}

function summarizeError(error: string | null): string | null {
  if (!error) {
    return null;
  }
  const oneLine = error.replace(/\s+/g, ' ').trim();
  return oneLine.length > MAX_ERROR_SUMMARY_LENGTH
    ? `${oneLine.slice(0, MAX_ERROR_SUMMARY_LENGTH - 1)}…`
    : oneLine;
}

/**
 * The idle, pre-click tooltip copy: what a press will do RIGHT NOW, so the
 * user knows before clicking rather than only discovering it on hover after
 * (the gap this whole feature closes). `status: null` covers a fresh tab
 * before the first `RefreshStatus` push/poll has arrived.
 */
export function describeIdleRefreshPreview(status: PriceRefreshStatus | null): string {
  const footer =
    'Daily closing prices and dividend history update on their own schedule and are not touched by this.';

  if (!status) {
    return `Fetches live prices now. ${footer}`;
  }

  const open: string[] = [];
  const closed: string[] = [];
  (status.nyseOpen ? open : closed).push(SOURCE_MARKET_LABEL.TwelveData);
  (status.sgxOpen ? open : closed).push(SOURCE_MARKET_LABEL.Yahoo);
  open.push(SOURCE_MARKET_LABEL.CoinGecko);

  if (closed.length === 0) {
    return `Fetches live prices now for ${joinWithAnd(open)}. ${footer}`;
  }

  const verb = closed.length === 1 ? 'is' : 'are';
  return `Fetches live prices now for ${joinWithAnd(open)} — ${joinWithAnd(closed)} ${verb} closed and won't be refreshed. ${footer}`;
}
