import { MarketCloseStatus, PriceRefreshStatus, QuoteProviderKind } from '../api/models';
import { joinWithAnd, SOURCE_MARKET_LABEL, SOURCE_MARKET_TITLE } from './refresh-outcome';
import { formatDueIn, formatRelativeTime } from '../../shared/time/relative-time';
import { formatDateOnly } from '../../shared/util/local-date';

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
 *
 * Closing-price (backfill) status — `PriceRefreshStatus.closes` (2026-09-13,
 * see that field's own header comment for the live incident this fixes):
 *  - `TwelveData` rows pair with the `Nyse` close entry, `Yahoo` rows with
 *    `Sgx`. `CoinGecko` has no close entry at all — crypto keeps no price
 *    history — and must never gain one here.
 *  - `closes` absent/null (an older cached snapshot, or a backend that
 *    predates the field) must behave EXACTLY as if it did not exist — no
 *    close line, and the live-failure line renders exactly as it did before
 *    this field existed.
 *  - `lastRunSuccess: null` on a close entry means "never attempted" — it
 *    must never render as a failure (same D10/D26/D33/D35/D38/D45 family as
 *    everywhere else in this file). Only `lastRunSuccess === false` renders
 *    the close-failed line.
 *  - `latestCloseDate: null` means there is no honest floor to report yet, so
 *    no "closing prices through <date>" claim is made even if a run has
 *    otherwise succeeded.
 *  - THE SUPERSEDE RULE this field exists for: while a market is CLOSED, a
 *    live-quote failure that is *older* than a close entry's `lastSuccessAt`
 *    has been overtaken by events — the backfill that ran afterwards is what
 *    actually kept that market's price current, so the stale live-failure
 *    line is suppressed (`showLiveFailure` false) in favour of the close
 *    line. While the market is OPEN, the live failure is always shown
 *    regardless of any close entry — live data is what matters when the
 *    market is trading, so it must never be hidden behind a backfill fact.
 */
const MARKET_ORDER: readonly QuoteProviderKind[] = ['TwelveData', 'Yahoo', 'CoinGecko'];

/** `MarketRefreshRow.provider` -> the `MarketCloseStatus.market` it pairs
 *  with. `CoinGecko` is deliberately absent — crypto keeps no close history. */
const CLOSE_MARKET_BY_PROVIDER: Partial<Record<QuoteProviderKind, MarketCloseStatus['market']>> = {
  TwelveData: 'Nyse',
  Yahoo: 'Sgx',
};

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
  /**
   * When the failed attempt happened, in the same "N ago" wording as
   * `lastSuccessLabel` (via `formatRelativeTime`) so a failure from a market
   * that has since closed — and therefore hasn't been retried — cannot read
   * as having "just" happened. Null only when there is no failure to time
   * (`attemptedAndFailed` false) or the backend somehow sent no
   * `lastAttemptedAt` on a failed row; the template falls back to the
   * un-timestamped wording in that case rather than rendering "Invalid Date".
   */
  readonly lastAttemptedLabel: string | null;
  /**
   * Whether the template should render the live-failure line at all. Equal to
   * `attemptedAndFailed` in every case EXCEPT the supersede rule above: a
   * closed market whose backfill has since recorded a success later than the
   * stale live failure. Always read this instead of `attemptedAndFailed` for
   * display — `attemptedAndFailed` stays the raw fact, this is the display
   * decision built on top of it.
   */
  readonly showLiveFailure: boolean;
  /**
   * "Closing prices through Fri 11 Sep · recorded 17h ago" — null for
   * `CoinGecko` always, and null whenever there is nothing honest to claim
   * (`closes` absent, no matching entry, or `latestCloseDate` null).
   *
   * The " · recorded …" clause itself is dropped (rendering just "Closing
   * prices through Fri 11 Sep") when `lastSuccessAt` is null — a pre-D47
   * successful backfill run recorded with no dated success at all. Pairing
   * a real date with "recorded never" would read as self-contradicting, so
   * the clause is omitted rather than lying with `formatRelativeTime`'s
   * "never" fallback.
   */
  readonly closeThroughLabel: string | null;
  /**
   * "Last closing-price update failed 2h ago — {truncated error}" (or without
   * the "N ago" clause if the close entry has no `lastAttemptedAt`). Null
   * unless the close entry's `lastRunSuccess` is literally `false` —
   * `null` (never attempted) renders neither this nor a failure of any kind.
   */
  readonly closeFailedLabel: string | null;
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

  const closeMarket = CLOSE_MARKET_BY_PROVIDER[provider] ?? null;
  const close = closeMarket ? (status.closes?.find((c) => c.market === closeMarket) ?? null) : null;

  // Supersede rule: only while the market is CLOSED can a later close success
  // override a stale live failure. An OPEN market always shows the live
  // failure, and a `close` that is missing or has no recorded success at all
  // can never supersede anything.
  const liveFailureSuperseded =
    attemptedAndFailed &&
    !isOpen &&
    close !== null &&
    close.lastSuccessAt != null &&
    source!.lastAttemptedAt != null &&
    new Date(close.lastSuccessAt).getTime() > new Date(source!.lastAttemptedAt).getTime();

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
    lastAttemptedLabel:
      attemptedAndFailed && source.lastAttemptedAt
        ? formatRelativeTime(source.lastAttemptedAt, nowMs)
        : null,
    showLiveFailure: attemptedAndFailed && !liveFailureSuperseded,
    closeThroughLabel:
      close && close.latestCloseDate ? formatCloseThroughLabel(close, nowMs) : null,
    closeFailedLabel:
      close && close.lastRunSuccess === false ? formatCloseFailedLabel(close, nowMs) : null,
  };
}

function formatCloseThroughLabel(close: MarketCloseStatus, nowMs: number): string {
  const throughDate = `Closing prices through ${formatDateOnly(close.latestCloseDate!)}`;
  // A pre-D47 success with no dated `lastSuccessAt` at all — omit the
  // "recorded …" clause rather than render the self-contradicting
  // "recorded never" that `formatRelativeTime`'s null fallback would produce.
  return close.lastSuccessAt
    ? `${throughDate} · recorded ${formatRelativeTime(close.lastSuccessAt, nowMs)}`
    : throughDate;
}

function formatCloseFailedLabel(close: MarketCloseStatus, nowMs: number): string {
  const summary = summarizeError(close.lastError) ?? 'unknown error';
  return close.lastAttemptedAt
    ? `Last closing-price update failed ${formatRelativeTime(close.lastAttemptedAt, nowMs)} — ${summary}`
    : `Last closing-price update failed — ${summary}`;
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
