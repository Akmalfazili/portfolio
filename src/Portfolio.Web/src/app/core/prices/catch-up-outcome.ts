import { CatchUpCompletedNotification, CatchUpKind, CatchUpLeg, RefreshCatchUpPlan } from '../api/models';
import { joinWithAnd } from './refresh-outcome';

/**
 * Copy for the 2026-09-14 catch-up feature (`POST /api/prices/refresh` now
 * also fetches missing price history and missing dividends in the
 * background) — split from `refresh-outcome.ts`/`refresh-panel.ts` because it
 * covers two genuinely different moments:
 *
 * - `describeCatchUpPlan` — what THIS CLICK queued (or found already
 *   queued/failing/not-yet-fetchable), appended to `describeRefreshOutcome`'s
 *   toast so a press reads honestly as "make everything current", not just
 *   "refreshed N live quotes".
 * - `describeCatchUpCompletion` / `describeCatchUpStatusLine` — what actually
 *   happened once the background fetch that click queued finishes, some time
 *   later, surfaced as refresh-indicator panel state (never a toast — a
 *   background completion is not a "just performed" action from the user's
 *   point of view; see the tracker's `NotificationService` rule).
 *
 * All symbols here are stocks — crypto keeps no price history or dividends
 * at all (locked decision, CLAUDE.md), so `fetchSymbols`/`retryPendingSymbols`
 * /`notYetAvailableSymbols`/`succeededSymbols`/`failed` never carry one.
 */

const LEG_LABEL: Record<CatchUpKind, string> = {
  PriceHistory: 'price history',
  Dividends: 'dividends',
};

const MAX_ERROR_SUMMARY_LENGTH = 80;

/** "NVDA" / "NVDA and MSFT" / "5 stocks" — never a bare, unbounded list. */
function summarizeSymbols(symbols: string[]): string {
  return symbols.length > 3 ? `${symbols.length} stocks` : joinWithAnd(symbols);
}

function legFor(plan: RefreshCatchUpPlan, kind: CatchUpKind): CatchUpLeg {
  return kind === 'PriceHistory' ? plan.priceHistory : plan.dividends;
}

function queuedFragment(kind: CatchUpKind, leg: CatchUpLeg): string {
  const base = `${LEG_LABEL[kind]} for ${summarizeSymbols(leg.fetchSymbols)}`;
  if (kind === 'PriceHistory' && leg.fxPairs.length > 0) {
    const rates = leg.fxPairs.map((pair) => `the ${pair} rate`);
    return `${base} and ${joinWithAnd(rates)}`;
  }
  return base;
}

function alreadyRunningSentence(kind: CatchUpKind, leg: CatchUpLeg): string {
  // Deliberately honest, not reassuring: the job that is already running may
  // not even include these symbols — see `CatchUpState`'s header comment.
  return `A ${LEG_LABEL[kind]} fetch is already running — ${summarizeSymbols(leg.fetchSymbols)} will be picked up when it finishes or on the next refresh.`;
}

function retryPendingSentence(kind: CatchUpKind, leg: CatchUpLeg): string {
  const label = kind === 'PriceHistory' ? 'Price history' : 'Dividends';
  return `${label} for ${summarizeSymbols(leg.retryPendingSymbols)} failed recently and will retry automatically.`;
}

function notYetAvailableSentence(leg: CatchUpLeg): string {
  const symbols = leg.notYetAvailableSymbols;
  if (symbols.length === 1) {
    return `${symbols[0]}'s first daily close isn't published yet.`;
  }
  const subject = symbols.length > 3 ? `${symbols.length} stocks'` : `${joinWithAnd(symbols)}'s`;
  return `${subject} first daily closes aren't published yet.`;
}

/**
 * Every per-leg note EXCEPT the Queued fragment, which `describeCatchUpPlan`
 * combines across both legs into one sentence instead (so a click that
 * queues both price history AND dividends for the same stock reads as one
 * sentence, not two near-duplicates).
 *
 * Exhaustive over `CatchUpState` by design, same discipline as
 * `describeRefreshOutcome`'s switch over `RefreshOutcome` — a fifth state
 * added to the union without a case here is a compile error, not a silent
 * fall-through.
 */
function nonQueuedLegNotes(kind: CatchUpKind, leg: CatchUpLeg): string[] {
  const notes: string[] = [];

  switch (leg.state) {
    case 'Queued':
      break; // combined across legs by describeCatchUpPlan instead
    case 'AlreadyRunning':
      if (leg.fetchSymbols.length > 0) {
        notes.push(alreadyRunningSentence(kind, leg));
      }
      break;
    case 'NothingToFetch':
      break;
    default:
      return assertUnreachableCatchUpState(leg.state);
  }

  if (leg.retryPendingSymbols.length > 0) {
    notes.push(retryPendingSentence(kind, leg));
  }
  if (kind === 'PriceHistory' && leg.notYetAvailableSymbols.length > 0) {
    notes.push(notYetAvailableSentence(leg));
  }

  return notes;
}

/**
 * The click-outcome sentence(s) appended to `describeRefreshOutcome`'s
 * message. `catchUp` absent/null (a scheduled cycle, or a server older than
 * this feature) renders as the empty string — say NOTHING, never "already up
 * to date", which is a different, stronger claim this function cannot make
 * without the field.
 */
export function describeCatchUpPlan(catchUp: RefreshCatchUpPlan | null | undefined): string {
  if (catchUp == null) {
    return '';
  }

  const queuedKinds = (['PriceHistory', 'Dividends'] as const).filter((kind) => {
    const leg = legFor(catchUp, kind);
    return leg.state === 'Queued' && leg.fetchSymbols.length > 0;
  });
  const queuedFragments = queuedKinds.map((kind) => queuedFragment(kind, legFor(catchUp, kind)));

  const sentences: string[] = [];
  if (queuedFragments.length > 0) {
    const pronoun = queuedFragments.length === 1 ? 'it arrives' : 'they arrive';
    sentences.push(
      `Also fetching ${joinWithAnd(queuedFragments)} — this page will update when ${pronoun}.`,
    );
  }
  sentences.push(...nonQueuedLegNotes('PriceHistory', catchUp.priceHistory));
  sentences.push(...nonQueuedLegNotes('Dividends', catchUp.dividends));

  if (sentences.length === 0) {
    return 'Price history and dividends are already up to date.';
  }
  return sentences.join(' ');
}

function summarizeError(error: string): string {
  const oneLine = error.replace(/\s+/g, ' ').trim();
  return oneLine.length > MAX_ERROR_SUMMARY_LENGTH
    ? `${oneLine.slice(0, MAX_ERROR_SUMMARY_LENGTH - 1)}…`
    : oneLine;
}

/**
 * One `failed` entry's own sentence. `failure.symbol` has three shapes, each
 * worded differently rather than forced through one "for <subject>" template:
 * - `""` — the whole run threw before reaching any single stock (e.g. a
 *   database failure) — there is no subject to name, so "for " would leave a
 *   dangling double space ("Couldn't fetch price history  — <error>.").
 * - `"FX:USD/SGD"` — the price-history leg's FX-rate fetch, which isn't a
 *   stock and reads clumsily forced through the "price history for X"
 *   template ("Couldn't fetch price history for the USD/SGD rate" is
 *   clumsy) — named directly instead: "Couldn't fetch the USD/SGD rate".
 * - anything else — a real stock symbol, the original "for SYMBOL" template.
 */
function describeFailureSentence(
  label: string,
  failure: { symbol: string; error: string },
): string {
  const errorSuffix = ` — ${summarizeError(failure.error)}.`;
  if (failure.symbol === '') {
    return `Couldn't fetch ${label}${errorSuffix}`;
  }
  if (failure.symbol.startsWith('FX:')) {
    return `Couldn't fetch the ${failure.symbol.slice(3)} rate${errorSuffix}`;
  }
  return `Couldn't fetch ${label} for ${failure.symbol}${errorSuffix}`;
}

/**
 * The line for a `CatchUpCompleted` push that has actually landed — distinct
 * sentences per succeeded/failed/skipped-for-budget, never collapsed into one
 * bucket (D10/D26/D33/D35/D38/D45 family). Each `failed` entry gets its own
 * sentence because different symbols in the same completion can fail for
 * different reasons.
 */
export function describeCatchUpCompletion(notification: CatchUpCompletedNotification): string {
  const label = LEG_LABEL[notification.kind];
  const sentences: string[] = [];

  if (notification.succeededSymbols.length > 0) {
    sentences.push(`Fetched ${label} for ${summarizeSymbols(notification.succeededSymbols)}.`);
  }

  for (const failure of notification.failed) {
    sentences.push(describeFailureSentence(label, failure));
  }

  if (notification.skippedForBudgetSymbols.length > 0) {
    const symbols = notification.skippedForBudgetSymbols;
    sentences.push(
      symbols.length === 1
        ? `Skipped ${symbols[0]}'s ${label} — today's data budget is used up.`
        : `Skipped ${label} for ${summarizeSymbols(symbols)} — today's data budget is used up.`,
    );
  }

  return sentences.join(' ');
}

/**
 * The refresh-indicator panel's own per-leg status line — returns non-null
 * ONLY for a `Queued` leg of the CURRENT plan: "Fetching…" while `inFlight`
 * (its own `runId`-matched completion hasn't arrived yet — see
 * `PriceStore.isLegInFlight`), else that completion's own outcome once it
 * has. Every other case — `NothingToFetch`, `AlreadyRunning`, or a `Queued`
 * leg the caller failed to pass a same-`runId` completion for — renders
 * `null`, deliberately: the click's own toast (`describeRefreshOutcome`)
 * already covers "already up to date" / "already running" / retry-pending /
 * not-yet-available for those, and this panel line adding a second, DIFFERENT
 * account of the same click would be redundant at best.
 *
 * The `completion.runId === leg.runId` check is a defensive belt-and-braces
 * one, not the primary guard — `PriceStore` is expected to have already
 * matched by id before calling this — but repeating it here means this pure
 * function's own contract ("only this leg's own completion, never a stale
 * one") holds regardless of what the caller passes in. This is the fix for a
 * live bug: an earlier version trusted a `completion` argument that could be
 * "the latest seen for this kind" rather than "this leg's own" — click 1
 * queued both legs and finished ("Fetched price history for 7 stocks."/
 * "Fetched dividends for 22 stocks."); click 2 returned `NothingToFetch` for
 * both (confirmed nothing was spent), but the panel kept showing click 1's
 * completion text, stating a PREVIOUS click's outcome as the CURRENT one's.
 *
 * Pure function of already-computed store state, so it needs no store/hub to
 * unit test.
 */
export function describeCatchUpStatusLine(
  kind: CatchUpKind,
  leg: CatchUpLeg,
  inFlight: boolean,
  completion: CatchUpCompletedNotification | null,
): string | null {
  if (leg.state !== 'Queued') {
    return null;
  }
  if (inFlight) {
    return `Fetching ${LEG_LABEL[kind]} for ${summarizeSymbols(leg.fetchSymbols)}…`;
  }
  if (completion && leg.runId != null && completion.runId === leg.runId) {
    return describeCatchUpCompletion(completion);
  }
  return null;
}

/** Never actually reached at runtime — makes the switch above exhaustive at compile time. */
function assertUnreachableCatchUpState(state: never): string[] {
  throw new Error(`Unhandled CatchUpState: ${state as string}`);
}
