import { describeRefreshOutcome } from './refresh-outcome';
import { PriceRefreshCycleResult, PriceRefreshStatus } from '../api/models';

function status(overrides: Partial<PriceRefreshStatus> = {}): PriceRefreshStatus {
  return {
    lastRefreshedAt: '2026-07-31T04:00:00Z',
    nyseOpen: false,
    sgxOpen: false,
    nextScheduledRunAt: null,
    sources: [],
    ...overrides,
  };
}

describe('describeRefreshOutcome (D5 — why nothing moved)', () => {
  // Captured verbatim from a live POST /api/prices/refresh at 08:11 UTC on a Friday:
  // NYSE closed, SGX inside its afternoon session. Note there is NO TwelveData entry —
  // a gated source is omitted from `sources` rather than reported as attempted:false,
  // which is exactly why the closed-market set is derived from the status flags.
  const LIVE_NYSE_CLOSED_RESULT: PriceRefreshCycleResult = {
    outcome: 'Completed',
    cooldownSecondsRemaining: null,
    totalSymbolsRefreshed: 4,
    sources: [
      { source: 'Yahoo', attempted: true, success: true, symbolsRefreshed: 1, error: null },
      { source: 'CoinGecko', attempted: true, success: true, symbolsRefreshed: 3, error: null },
    ],
  };

  it('still says the US market was closed even though 4 symbols did refresh', () => {
    const message = describeRefreshOutcome(
      LIVE_NYSE_CLOSED_RESULT,
      status({ nyseOpen: false, sgxOpen: true }),
    );

    expect(message).toContain('Refreshed 4 symbols');
    expect(message).toContain('US market is closed');
    expect(message).toContain('not updated');
  });

  it('does not claim a market is closed when both are open', () => {
    const message = describeRefreshOutcome(
      LIVE_NYSE_CLOSED_RESULT,
      status({ nyseOpen: true, sgxOpen: true }),
    );

    expect(message).toBe('Refreshed 4 symbols (SGX, crypto).');
  });

  it('names both closed markets when only crypto was attempted at 3am', () => {
    // The 3am case: both equity providers gated, so neither appears in `sources` at all
    // and crypto refreshed nothing new.
    const result: PriceRefreshCycleResult = {
      outcome: 'Completed',
      cooldownSecondsRemaining: null,
      totalSymbolsRefreshed: 0,
      sources: [
        { source: 'CoinGecko', attempted: true, success: true, symbolsRefreshed: 0, error: null },
      ],
    };

    const message = describeRefreshOutcome(result, status({ nyseOpen: false, sgxOpen: false }));

    expect(message).toBe('Nothing to refresh right now — US market and SGX are closed.');
  });

  it('reports the refreshed count and source when something actually moved', () => {
    const result: PriceRefreshCycleResult = {
      outcome: 'Completed',
      cooldownSecondsRemaining: null,
      totalSymbolsRefreshed: 3,
      sources: [{ source: 'CoinGecko', attempted: true, success: true, symbolsRefreshed: 3, error: null }],
    };

    const message = describeRefreshOutcome(result, status({ nyseOpen: true, sgxOpen: true }));

    expect(message).toBe('Refreshed 3 symbols (crypto).');
  });

  it('reports a failure distinctly from a market-closed skip', () => {
    const result: PriceRefreshCycleResult = {
      outcome: 'Completed',
      cooldownSecondsRemaining: null,
      totalSymbolsRefreshed: 0,
      sources: [
        { source: 'TwelveData', attempted: true, success: false, symbolsRefreshed: 0, error: 'timeout' },
      ],
    };

    const message = describeRefreshOutcome(result, status({ nyseOpen: true, sgxOpen: true }));

    expect(message).toBe('Refresh attempted but failed for US market.');
  });

  it('reports plain up-to-date when everything was attempted and nothing changed', () => {
    const result: PriceRefreshCycleResult = {
      outcome: 'NothingDue',
      cooldownSecondsRemaining: null,
      totalSymbolsRefreshed: 0,
      sources: [{ source: 'CoinGecko', attempted: true, success: true, symbolsRefreshed: 0, error: null }],
    };

    const message = describeRefreshOutcome(result, status({ nyseOpen: true, sgxOpen: true }));

    expect(message).toBe('Already up to date — nothing changed since the last refresh.');
  });

  it('D38 residual — a Queued outcome says the refresh is running in the background, NOT "already up to date"', () => {
    // The real shape a queued manual refresh returns: totalSymbolsRefreshed 0
    // and an empty sources array — indistinguishable from "nothing was due"
    // unless `outcome` itself is read.
    const result: PriceRefreshCycleResult = {
      outcome: 'Queued',
      cooldownSecondsRemaining: null,
      totalSymbolsRefreshed: 0,
      sources: [],
    };

    const message = describeRefreshOutcome(result, status({ nyseOpen: true, sgxOpen: true }));

    expect(message).not.toContain('Already up to date');
    expect(message).toContain('running in the background');
  });

  it('D38 residual — a Queued outcome still appends the closed-market note when relevant', () => {
    const result: PriceRefreshCycleResult = {
      outcome: 'Queued',
      cooldownSecondsRemaining: null,
      totalSymbolsRefreshed: 0,
      sources: [],
    };

    const message = describeRefreshOutcome(result, status({ nyseOpen: true, sgxOpen: false }));

    expect(message).toContain('running in the background');
    expect(message).toContain('SGX is closed');
  });

  it('uses singular "symbol" for exactly one refreshed symbol', () => {
    const result: PriceRefreshCycleResult = {
      outcome: 'Completed',
      cooldownSecondsRemaining: null,
      totalSymbolsRefreshed: 1,
      sources: [{ source: 'TwelveData', attempted: true, success: true, symbolsRefreshed: 1, error: null }],
    };

    const message = describeRefreshOutcome(result, status({ nyseOpen: true, sgxOpen: true }));

    expect(message).toBe('Refreshed 1 symbol (US market).');
  });
});
