import { PriceRefreshCycleResult, PriceRefreshStatus, QuoteProviderKind } from '../api/models';

/**
 * Drawback D5 (tracker.md, Phase 5): a manual refresh bypasses the poll
 * *interval*, not the market *calendar* — a 3am click on `POST
 * /api/prices/refresh` returns `200 Completed` having only touched crypto,
 * which reads as a silent no-op/bug for stocks unless the UI says why.
 *
 * This turns a `PriceRefreshCycleResult` (+ the `nyseOpen`/`sgxOpen` flags on
 * the last known `PriceRefreshStatus`) into that explanation. Pure function so
 * it is unit-testable without a SignalR connection or an HTTP mock.
 *
 * IMPORTANT — where "was it skipped?" actually comes from. `SourceRefreshOutcome`
 * carries an `attempted` flag, and it is tempting to read the skipped set off
 * `result.sources.filter(s => !s.attempted)`. That filter can never match: when
 * a provider's market is closed, `PriceRefreshService.RunCycleAsync` records the
 * outcome to the status store and then `continue`s **without adding it to the
 * returned list** — so a gated source is absent from `sources` entirely rather
 * than present with `attempted: false`. Verified against the live API: with NYSE
 * closed, the 200 body listed only Yahoo and CoinGecko, no TwelveData entry.
 *
 * So the closed-market set is derived from the status snapshot's `nyseOpen` /
 * `sgxOpen` flags, which are authoritative and always present. `attempted` is
 * still used for the one thing it does report reliably: distinguishing a
 * provider that was called and failed from one that was never called.
 *
 * D38 residual — `outcome: 'Queued'` (a manual refresh whose Twelve Data
 * group needed more than one per-minute credit chunk, detached onto a
 * background task) carries `totalSymbolsRefreshed: 0` and an empty `sources`
 * array, which is EXACTLY the shape "nothing was due" also produces. It must
 * be handled before any branch that reads `totalSymbolsRefreshed`/`sources`,
 * or a genuinely in-flight background sweep reads as "already up to date" —
 * the opposite of what happened.
 */
const SOURCE_MARKET_LABEL: Record<QuoteProviderKind, string> = {
  TwelveData: 'US market',
  Yahoo: 'SGX',
  CoinGecko: 'crypto',
};

export function describeRefreshOutcome(
  result: PriceRefreshCycleResult,
  status: PriceRefreshStatus | null,
): string {
  const closed = closedMarkets(status);
  const closedNote =
    closed.length > 0 ? ` ${joinWithAnd(closed)} ${closed.length === 1 ? 'is' : 'are'} closed, so those holdings were not updated.` : '';

  // Exhaustiveness guard: `case`s that fall through to the shared logic below
  // are listed explicitly, and `default` routes anything else through
  // assertUnreachable(). Adding a fifth RefreshOutcome value without a case
  // here is a compile error, not a silent fall-through like the one that let
  // `Queued` slip past every branch below and render "up to date" for an
  // in-flight background sweep.
  switch (result.outcome) {
    case 'Queued':
      // Deliberately DOES append closedNote: a queued sweep and a closed
      // market are independent facts (e.g. Twelve Data's US batch queued
      // while SGX is separately shut), and the other branches below all
      // append it too — dropping it here would be an inconsistent, not a
      // more correct, message.
      return `A larger refresh is already running in the background — this page will update automatically when it finishes.${closedNote}`;
    case 'Completed':
    case 'NothingDue':
    case 'CooldownActive':
      break;
    default:
      return assertUnreachableOutcome(result.outcome);
  }

  if (result.totalSymbolsRefreshed > 0) {
    const refreshedFrom = result.sources
      .filter((source) => source.success && source.symbolsRefreshed > 0)
      .map((source) => SOURCE_MARKET_LABEL[source.source]);
    const suffix = refreshedFrom.length > 0 ? ` (${refreshedFrom.join(', ')})` : '';
    const noun = result.totalSymbolsRefreshed === 1 ? 'symbol' : 'symbols';
    return `Refreshed ${result.totalSymbolsRefreshed} ${noun}${suffix}.${closedNote}`;
  }

  const failed = result.sources.filter((source) => source.attempted && !source.success);
  if (failed.length > 0) {
    const names = failed.map((source) => SOURCE_MARKET_LABEL[source.source]).join(', ');
    return `Refresh attempted but failed for ${names}.${closedNote}`;
  }

  if (closed.length > 0) {
    return `Nothing to refresh right now — ${joinWithAnd(closed)} ${closed.length === 1 ? 'is' : 'are'} closed.`;
  }

  return 'Already up to date — nothing changed since the last refresh.';
}

/** Markets the calendar reports shut, named the way the rest of the UI names them. */
function closedMarkets(status: PriceRefreshStatus | null): string[] {
  if (!status) {
    return [];
  }
  const closed: string[] = [];
  if (!status.nyseOpen) {
    closed.push('US market');
  }
  if (!status.sgxOpen) {
    closed.push('SGX');
  }
  return closed;
}

function joinWithAnd(parts: string[]): string {
  return parts.length <= 1 ? (parts[0] ?? '') : `${parts.slice(0, -1).join(', ')} and ${parts.at(-1)}`;
}

/** Never actually reached at runtime — its role is purely to make the switch above exhaustive at compile time. */
function assertUnreachableOutcome(outcome: never): string {
  throw new Error(`Unhandled RefreshOutcome: ${outcome as string}`);
}
