import {
  MarketCloseStatus,
  PriceRefreshStatus,
  QuoteProviderKind,
  SourceRefreshStatus,
} from '../api/models';
import { joinWithAnd, SOURCE_MARKET_LABEL, SOURCE_MARKET_TITLE } from './refresh-outcome';
import { formatRelativeTime } from '../../shared/time/relative-time';
import { formatDateOnly } from '../../shared/util/local-date';

/**
 * Backs the refresh-details panel opened from the toolbar's "Updated N ago"
 * trigger.
 *
 * The panel answers one question per market, in two lines: is it trading,
 * and how current is the price the user is actually looking at right now. A
 * third line appears only when something is wrong. Everything this used to
 * show beyond that — "Will refresh now"/"Won't refresh", the next
 * automatic-check countdown, the Twelve Data cadence line, and the
 * " · recorded …" clause on the close line — was cut as noise; see the
 * 2026-09-14 tracker.md entry for the before/after.
 *
 * Pure and unit-testable on its own, same shape as `refresh-outcome.ts`.
 *
 * Honesty constraints this file exists to enforce (D10/D26/D33/D35/D38/D45
 * defect family — "not attempted" must never collapse into "zero" or
 * "failed"):
 *  - A provider absent from `status.sources` (a brand-new install, before its
 *    first ever cycle) renders as "Waiting for first update" — never a
 *    confident zero and never presented as a failure.
 *  - A source that HAS run but has never once succeeded renders "No
 *    successful update yet" — distinct from both "waiting" (never ran at
 *    all) and a genuine last-known-good timestamp.
 *  - A failed last attempt (`lastRunSuccess: false` with a `lastError`) is
 *    surfaced distinctly from "market closed, not attempted" — never the same
 *    row state.
 *
 * Freshness line (`MarketRefreshRow.freshnessLabel`) — the fix for a real
 * live symptom (2026-09-14): a stack that wasn't running during NYSE hours
 * for several days read "Last updated 5d ago" for US stocks while the market
 * was closed, which is the wrong thing to lead with — the price on screen is
 * Friday's close, not a 5-day-old live quote. So a CLOSED market with an
 * honest close on file leads with the close date instead of the live
 * timestamp, mirroring the backend's own D48 read-time fallback:
 *  - `!hasData` → "Waiting for first update".
 *  - Source present but never successfully run (`lastSuccessAt` null) → "No
 *    successful update yet" — UNLESS a closed market's paired close entry
 *    covers it (see below), in which case the close line wins instead of
 *    reporting on the live leg at all.
 *  - An OPEN market, or crypto (always open) → always the live
 *    "Updated N ago" reading. Live data is what matters while a market
 *    trades, so a close entry is never consulted here at all.
 *  - A CLOSED market (TwelveData/Yahoo) with a paired close entry that has a
 *    `latestCloseDate`, where the live leg is either never-succeeded or its
 *    UTC calendar date is NO LATER than `latestCloseDate` → the close label.
 *    Mirrors `PortfolioSummaryService`'s D48 strict-`>` tie-break (a same-UTC-
 *    date tie goes to the close, not the live quote), so a live success
 *    recorded on the same calendar day as the close does not spuriously win.
 *  - Otherwise (closed market, live leg strictly newer than the close, or no
 *    usable close entry at all) → falls back to the live "Updated N ago" /
 *    "No successful update yet" reading, covering the honest gap between a
 *    session ending and that evening's backfill landing.
 *  - `closes` absent/null, or no matching entry, or `latestCloseDate` null —
 *    all behave EXACTLY as if `closes` did not exist: the live reading is
 *    used, unchanged.
 *
 * Closing-price (backfill) failure line — `PriceRefreshStatus.closes`
 * (2026-09-13, see that field's own header comment for the live incident this
 * fixes):
 *  - `TwelveData` rows pair with the `Nyse` close entry, `Yahoo` rows with
 *    `Sgx`. `CoinGecko` has no close entry at all — crypto keeps no price
 *    history — and must never gain one here.
 *  - `lastRunSuccess: null` on a close entry means "never attempted" — it
 *    must never render as a failure (same D10/D26/D33/D35/D38/D45 family as
 *    everywhere else in this file). Only `lastRunSuccess === false` renders
 *    the close-failed line.
 *  - THE SUPERSEDE RULE this field exists for: while a market is CLOSED, a
 *    live-quote failure that is *older* than a close entry's `lastSuccessAt`
 *    has been overtaken by events — the backfill that ran afterwards is what
 *    actually kept that market's price current, so the stale live-failure
 *    line is suppressed (`showLiveFailure` false) in favour of the freshness
 *    line's close reading. While the market is OPEN, the live failure is
 *    always shown regardless of any close entry — live data is what matters
 *    when the market is trading, so it must never be hidden behind a
 *    backfill fact.
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
  /** Crypto only — there is no market-hours concept for it at all. Renders "24/7". */
  readonly alwaysOpen: boolean;
  readonly isOpen: boolean;
  /** False when this provider has never once been recorded — "no data yet", not a zero. */
  readonly hasData: boolean;
  /**
   * The line-2 freshness reading — see this file's header comment for the
   * full rule. One of "Waiting for first update", "No successful update
   * yet", "Updated N ago", or "Closing prices · Fri 11 Sep".
   */
  readonly freshnessLabel: string;
  /** True only when the source WAS attempted and failed — never true for "not attempted". */
  readonly attemptedAndFailed: boolean;
  /** Short, truncated summary — never the raw provider error string. */
  readonly errorSummary: string | null;
  /**
   * When the failed attempt happened, in the same "N ago" wording as
   * `freshnessLabel`'s "Updated N ago" (via `formatRelativeTime`) so a
   * failure from a market that has since closed — and therefore hasn't been
   * retried — cannot read as having "just" happened. Null only when there is
   * no failure to time (`attemptedAndFailed` false) or the backend somehow
   * sent no `lastAttemptedAt` on a failed row; the template falls back to the
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
   * "Closing-price update failed 2h ago · {truncated error}" (or without the
   * "N ago" clause if the close entry has no `lastAttemptedAt`). Null unless
   * the close entry's `lastRunSuccess` is literally `false` — `null` (never
   * attempted) renders neither this nor a failure of any kind.
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
    hasData,
    freshnessLabel: computeFreshnessLabel(hasData, source, alwaysOpen, isOpen, close, nowMs),
    attemptedAndFailed,
    errorSummary: attemptedAndFailed ? summarizeError(source.lastError) : null,
    lastAttemptedLabel:
      attemptedAndFailed && source.lastAttemptedAt
        ? formatRelativeTime(source.lastAttemptedAt, nowMs)
        : null,
    showLiveFailure: attemptedAndFailed && !liveFailureSuperseded,
    closeFailedLabel:
      close && close.lastRunSuccess === false ? formatCloseFailedLabel(close, nowMs) : null,
  };
}

/** See this file's header comment for the full rule this implements. */
function computeFreshnessLabel(
  hasData: boolean,
  source: SourceRefreshStatus | undefined,
  alwaysOpen: boolean,
  isOpen: boolean,
  close: MarketCloseStatus | null,
  nowMs: number,
): string {
  if (!hasData) {
    return 'Waiting for first update';
  }

  // Live data is what matters while a market trades (or always, for
  // crypto) — a close entry is never consulted here, even if one exists.
  if (alwaysOpen || isOpen) {
    return liveReading(source!, nowMs);
  }

  // Closed market: an honest close on file that is at least as new as the
  // live leg (or the live leg never succeeded at all) is the more current,
  // more honest thing to lead with. Same-UTC-date tie goes to the close,
  // mirroring the backend's D48 strict-`>` rule.
  if (close?.latestCloseDate) {
    const liveDateUtc = source!.lastSuccessAt ? utcDateOnly(source!.lastSuccessAt) : null;
    if (liveDateUtc === null || liveDateUtc <= close.latestCloseDate) {
      return `Closing prices · ${formatDateOnly(close.latestCloseDate)}`;
    }
  }

  return liveReading(source!, nowMs);
}

function liveReading(source: SourceRefreshStatus, nowMs: number): string {
  return source.lastSuccessAt == null
    ? 'No successful update yet'
    : `Updated ${formatRelativeTime(source.lastSuccessAt, nowMs)}`;
}

/** The UTC calendar date ("YYYY-MM-DD") of an ISO instant. */
function utcDateOnly(iso: string): string {
  return new Date(iso).toISOString().slice(0, 10);
}

function formatCloseFailedLabel(close: MarketCloseStatus, nowMs: number): string {
  const summary = summarizeError(close.lastError) ?? 'unknown error';
  return close.lastAttemptedAt
    ? `Closing-price update failed ${formatRelativeTime(close.lastAttemptedAt, nowMs)} · ${summary}`
    : `Closing-price update failed · ${summary}`;
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

/**
 * The panel FOOTER's one-line scope statement — a terser descendant of
 * `describeIdleRefreshPreview` above, written for a panel that already shows
 * each market's Open/Closed state on its own row, so closed markets need no
 * further mention here. `status: null` covers a fresh tab before the first
 * status snapshot has arrived.
 */
export function describeRefreshScope(status: PriceRefreshStatus | null): string {
  if (!status) {
    return 'Refresh fetches live prices now.';
  }

  const openLabels: string[] = [];
  if (status.nyseOpen) {
    openLabels.push(SOURCE_MARKET_LABEL.TwelveData);
  }
  if (status.sgxOpen) {
    openLabels.push(SOURCE_MARKET_LABEL.Yahoo);
  }
  openLabels.push(SOURCE_MARKET_LABEL.CoinGecko); // crypto is always open

  if (status.nyseOpen && status.sgxOpen) {
    return 'Refresh updates all markets now.';
  }
  return `Refresh updates ${joinWithAnd(openLabels)} now.`;
}
