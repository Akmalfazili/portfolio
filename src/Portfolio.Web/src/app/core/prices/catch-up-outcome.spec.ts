import {
  describeCatchUpCompletion,
  describeCatchUpPlan,
  describeCatchUpStatusLine,
} from './catch-up-outcome';
import { CatchUpCompletedNotification, CatchUpLeg, RefreshCatchUpPlan } from '../api/models';

function leg(overrides: Partial<CatchUpLeg> = {}): CatchUpLeg {
  return {
    state: 'NothingToFetch',
    fetchSymbols: [],
    fxPairs: [],
    retryPendingSymbols: [],
    notYetAvailableSymbols: [],
    runId: null,
    ...overrides,
  };
}

function plan(overrides: Partial<RefreshCatchUpPlan> = {}): RefreshCatchUpPlan {
  return {
    priceHistory: leg(),
    dividends: leg(),
    ...overrides,
  };
}

describe('describeCatchUpPlan', () => {
  it('renders nothing (empty string) when catchUp is absent — an older server, never presented as "up to date"', () => {
    expect(describeCatchUpPlan(undefined)).toBe('');
  });

  it('renders nothing (empty string) when catchUp is explicitly null', () => {
    expect(describeCatchUpPlan(null)).toBe('');
  });

  it('says both are already up to date when every list on both legs is empty', () => {
    expect(describeCatchUpPlan(plan())).toBe('Price history and dividends are already up to date.');
  });

  it('a single Queued leg with one symbol reads singular ("it arrives")', () => {
    const message = describeCatchUpPlan(
      plan({ priceHistory: leg({ state: 'Queued', fetchSymbols: ['NEWCO'] }) }),
    );
    expect(message).toBe(
      'Also fetching price history for NEWCO — this page will update when it arrives.',
    );
  });

  it('combines a Queued price-history leg and a Queued dividends leg into one sentence', () => {
    const message = describeCatchUpPlan(
      plan({
        priceHistory: leg({ state: 'Queued', fetchSymbols: ['NVDA'] }),
        dividends: leg({ state: 'Queued', fetchSymbols: ['NVDA'] }),
      }),
    );
    expect(message).toBe(
      'Also fetching price history for NVDA and dividends for NVDA — this page will update when they arrive.',
    );
  });

  it('lists FX pairs on the price-history fragment only', () => {
    const message = describeCatchUpPlan(
      plan({
        priceHistory: leg({ state: 'Queued', fetchSymbols: ['NEWCO'], fxPairs: ['USD/SGD'] }),
      }),
    );
    expect(message).toBe(
      'Also fetching price history for NEWCO and the USD/SGD rate — this page will update when it arrives.',
    );
  });

  it('summarizes more than three queued symbols as a count, never an unbounded list', () => {
    const message = describeCatchUpPlan(
      plan({
        priceHistory: leg({
          state: 'Queued',
          fetchSymbols: ['A', 'B', 'C', 'D', 'E'],
        }),
      }),
    );
    expect(message).toContain('price history for 5 stocks');
  });

  it('an AlreadyRunning leg names what will be picked up, honestly hedged', () => {
    const message = describeCatchUpPlan(
      plan({
        priceHistory: leg({ state: 'AlreadyRunning', fetchSymbols: ['NVDA'] }),
      }),
    );
    expect(message).toBe(
      'A price history fetch is already running — NVDA will be picked up when it finishes or on the next refresh.',
    );
  });

  it('a NothingToFetch leg with a non-empty retryPendingSymbols still reports the retry note', () => {
    const message = describeCatchUpPlan(
      plan({
        priceHistory: leg({ state: 'NothingToFetch', retryPendingSymbols: ['NVDA'] }),
      }),
    );
    expect(message).toBe('Price history for NVDA failed recently and will retry automatically.');
  });

  it('a dividends retry-pending note is labelled distinctly from price history', () => {
    const message = describeCatchUpPlan(
      plan({
        dividends: leg({ state: 'NothingToFetch', retryPendingSymbols: ['NVDA'] }),
      }),
    );
    expect(message).toBe('Dividends for NVDA failed recently and will retry automatically.');
  });

  it('notYetAvailableSymbols only ever applies to price history, never dividends', () => {
    const message = describeCatchUpPlan(
      plan({
        priceHistory: leg({ state: 'NothingToFetch', notYetAvailableSymbols: ['NEWCO'] }),
      }),
    );
    expect(message).toBe("NEWCO's first daily close isn't published yet.");
  });

  it('multiple not-yet-available symbols pluralize correctly', () => {
    const message = describeCatchUpPlan(
      plan({
        priceHistory: leg({
          state: 'NothingToFetch',
          notYetAvailableSymbols: ['NEWCO', 'FRESHCO'],
        }),
      }),
    );
    expect(message).toBe("NEWCO and FRESHCO's first daily closes aren't published yet.");
  });

  it('combines a Queued fragment with an independent retry-pending note on the SAME leg', () => {
    const message = describeCatchUpPlan(
      plan({
        priceHistory: leg({
          state: 'Queued',
          fetchSymbols: ['NEWCO'],
          retryPendingSymbols: ['OLDCO'],
        }),
      }),
    );
    expect(message).toBe(
      'Also fetching price history for NEWCO — this page will update when it arrives. Price history for OLDCO failed recently and will retry automatically.',
    );
  });
});

describe('describeCatchUpCompletion', () => {
  function completion(
    overrides: Partial<CatchUpCompletedNotification> = {},
  ): CatchUpCompletedNotification {
    return {
      kind: 'PriceHistory',
      succeededSymbols: [],
      failed: [],
      skippedForBudgetSymbols: [],
      rowsInserted: 0,
      completedAt: '2026-09-14T10:00:00Z',
      runId: 'run-1',
      ...overrides,
    };
  }

  it('reports a successful fetch', () => {
    const message = describeCatchUpCompletion(completion({ succeededSymbols: ['NEWCO'] }));
    expect(message).toBe('Fetched price history for NEWCO.');
  });

  it('reports a failure with its own short error, distinct from a budget skip', () => {
    const message = describeCatchUpCompletion(
      completion({ failed: [{ symbol: 'NEWCO', error: 'Twelve Data returned HTTP 500' }] }),
    );
    expect(message).toBe("Couldn't fetch price history for NEWCO — Twelve Data returned HTTP 500.");
  });

  it('renders an FX failure using the pair name directly, not "price history for the USD/SGD rate"', () => {
    const message = describeCatchUpCompletion(
      completion({ failed: [{ symbol: 'FX:USD/SGD', error: 'rate unavailable' }] }),
    );
    expect(message).toBe("Couldn't fetch the USD/SGD rate — rate unavailable.");
  });

  it('renders a whole-run failure (empty symbol — e.g. a database failure before reaching any stock) with no dangling "for"', () => {
    const message = describeCatchUpCompletion(
      completion({ failed: [{ symbol: '', error: 'Database connection failed' }] }),
    );
    expect(message).toBe("Couldn't fetch price history — Database connection failed.");
  });

  it('renders a whole-run dividends failure (empty symbol) labelled distinctly from price history', () => {
    const message = describeCatchUpCompletion(
      completion({ kind: 'Dividends', failed: [{ symbol: '', error: 'Database connection failed' }] }),
    );
    expect(message).toBe("Couldn't fetch dividends — Database connection failed.");
  });

  it('reports a budget skip distinctly from a failure', () => {
    const message = describeCatchUpCompletion(completion({ skippedForBudgetSymbols: ['NEWCO'] }));
    expect(message).toBe("Skipped NEWCO's price history — today's data budget is used up.");
  });

  it('multiple budget-skipped symbols summarize as a list', () => {
    const message = describeCatchUpCompletion(
      completion({ skippedForBudgetSymbols: ['NEWCO', 'FRESHCO'] }),
    );
    expect(message).toBe(
      "Skipped price history for NEWCO and FRESHCO — today's data budget is used up.",
    );
  });

  it('gives each failed symbol its own sentence rather than collapsing distinct errors', () => {
    const message = describeCatchUpCompletion(
      completion({
        failed: [
          { symbol: 'A', error: 'timeout' },
          { symbol: 'B', error: 'not found' },
        ],
      }),
    );
    expect(message).toBe(
      "Couldn't fetch price history for A — timeout. Couldn't fetch price history for B — not found.",
    );
  });

  it('labels dividends distinctly from price history', () => {
    const message = describeCatchUpCompletion(
      completion({ kind: 'Dividends', succeededSymbols: ['NEWCO'] }),
    );
    expect(message).toBe('Fetched dividends for NEWCO.');
  });

  it('truncates a long error rather than dumping the raw provider message', () => {
    const longError =
      'Connection timed out while calling https://api.twelvedata.com/quote after 30000ms and three retries with backoff';
    const message = describeCatchUpCompletion(
      completion({ failed: [{ symbol: 'NEWCO', error: longError }] }),
    );
    expect(message.length).toBeLessThan(longError.length + 40);
    expect(message).toContain('…');
  });
});

describe('describeCatchUpStatusLine', () => {
  function completedNotification(
    overrides: Partial<CatchUpCompletedNotification> = {},
  ): CatchUpCompletedNotification {
    return {
      kind: 'PriceHistory',
      succeededSymbols: ['NEWCO'],
      failed: [],
      skippedForBudgetSymbols: [],
      rowsInserted: 3,
      completedAt: '2026-09-14T09:00:00Z',
      runId: 'run-1',
      ...overrides,
    };
  }

  it('click 1: a Queued leg still in flight reads "Fetching…", even with an unrelated completion on file', () => {
    const line = describeCatchUpStatusLine(
      'PriceHistory',
      leg({ state: 'Queued', fetchSymbols: ['NEWCO'], runId: 'run-1' }),
      true,
      completedNotification({ runId: 'run-old', succeededSymbols: ['OLDCO'] }),
    );
    expect(line).toBe('Fetching price history for NEWCO…');
  });

  it('click 1: once the runId-matched completion arrives, reads that completion\'s own outcome', () => {
    const line = describeCatchUpStatusLine(
      'Dividends',
      leg({ state: 'Queued', fetchSymbols: ['NEWCO'], runId: 'run-1' }),
      false,
      completedNotification({ kind: 'Dividends', runId: 'run-1', succeededSymbols: ['NEWCO'] }),
    );
    expect(line).toBe('Fetched dividends for NEWCO.');
  });

  it('click 2: a NothingToFetch leg renders nothing, even though click 1\'s completion is still the last one this store ever saw', () => {
    // THE regression this covers: click 1 queued+finished ("Fetched price
    // history for 7 stocks."); click 2 (a later, separate click) found
    // nothing due (`NothingToFetch`) — the panel must not keep showing click
    // 1's completion text as if it belonged to click 2. describeRefreshOutcome
    // already says "already up to date" for this click's own toast.
    const line = describeCatchUpStatusLine(
      'PriceHistory',
      leg({ state: 'NothingToFetch', runId: null }),
      false,
      completedNotification({ runId: 'run-1' }), // click 1's completion, still on file
    );
    expect(line).toBeNull();
  });

  it('an AlreadyRunning leg renders nothing — the click\'s own toast already says so', () => {
    const line = describeCatchUpStatusLine(
      'PriceHistory',
      leg({ state: 'AlreadyRunning', fetchSymbols: ['NVDA'], runId: null }),
      false,
      null,
    );
    expect(line).toBeNull();
  });

  it('a Queued leg with a completion for a DIFFERENT runId still reads "Fetching…", never that unrelated outcome', () => {
    // Realistic shape: PriceStore's runId map has no entry for THIS leg's
    // run (so isLegInFlight is still true), but does hold an unrelated run's
    // completion (e.g. the other leg's, or a previous click's).
    const line = describeCatchUpStatusLine(
      'PriceHistory',
      leg({ state: 'Queued', fetchSymbols: ['NEWCO'], runId: 'run-2' }),
      true,
      completedNotification({ runId: 'run-1' }),
    );
    expect(line).toBe('Fetching price history for NEWCO…');
  });

  it('defensive check: even if a caller passes a mismatched runId completion alongside inFlight: false, it is not shown as this leg\'s outcome', () => {
    const line = describeCatchUpStatusLine(
      'PriceHistory',
      leg({ state: 'Queued', fetchSymbols: ['NEWCO'], runId: 'run-2' }),
      false,
      completedNotification({ runId: 'run-1' }),
    );
    expect(line).toBeNull();
  });

  it('renders nothing when neither in flight nor ever completed', () => {
    expect(describeCatchUpStatusLine('PriceHistory', leg(), false, null)).toBeNull();
  });
});
