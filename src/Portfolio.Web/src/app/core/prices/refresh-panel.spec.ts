import { PriceRefreshStatus } from '../api/models';
import { buildMarketRefreshRows, describeIdleRefreshPreview } from './refresh-panel';

function status(overrides: Partial<PriceRefreshStatus> = {}): PriceRefreshStatus {
  return {
    lastRefreshedAt: '2026-07-31T04:00:00Z',
    nyseOpen: true,
    sgxOpen: true,
    nextScheduledRunAt: null,
    sources: [],
    ...overrides,
  };
}

describe('buildMarketRefreshRows', () => {
  const now = new Date('2026-07-31T12:10:00Z').getTime();

  it('marks crypto always-open and willRefresh regardless of the other markets', () => {
    const rows = buildMarketRefreshRows(status({ nyseOpen: false, sgxOpen: false }), now);
    const crypto = rows.find((r) => r.provider === 'CoinGecko')!;
    expect(crypto.alwaysOpen).toBe(true);
    expect(crypto.isOpen).toBe(true);
    expect(crypto.willRefresh).toBe(true);
  });

  it('a closed market reports isOpen/willRefresh false, not attempted-and-failed', () => {
    const rows = buildMarketRefreshRows(
      status({
        nyseOpen: false,
        sources: [
          {
            source: 'TwelveData',
            lastAttemptedAt: '2026-07-31T04:00:00Z',
            lastSuccessAt: '2026-07-31T04:00:00Z',
            lastRunSuccess: true,
            lastError: null,
            symbolsRefreshed: 4,
            nextDueAt: '2026-07-31T12:20:00Z',
          },
        ],
      }),
      now,
    );
    const us = rows.find((r) => r.provider === 'TwelveData')!;
    expect(us.isOpen).toBe(false);
    expect(us.willRefresh).toBe(false);
    expect(us.attemptedAndFailed).toBe(false);
  });

  it('a provider missing from sources renders as "no data yet", never a zero or a failure', () => {
    const rows = buildMarketRefreshRows(status({ sources: [] }), now);
    const sgx = rows.find((r) => r.provider === 'Yahoo')!;
    expect(sgx.hasData).toBe(false);
    expect(sgx.lastSuccessLabel).toBe('no data yet');
    expect(sgx.nextDueLabel).toBe('not yet scheduled');
    expect(sgx.attemptedAndFailed).toBe(false);
  });

  it('surfaces an attempted-and-failed source distinctly, with a truncated summary', () => {
    const longError =
      'Connection timed out while calling https://api.twelvedata.com/quote after 30000ms and three retries with backoff';
    const rows = buildMarketRefreshRows(
      status({
        sources: [
          {
            source: 'TwelveData',
            lastAttemptedAt: '2026-07-31T12:00:00Z',
            lastSuccessAt: '2026-07-31T04:00:00Z',
            lastRunSuccess: false,
            lastError: longError,
            symbolsRefreshed: 0,
            nextDueAt: '2026-07-31T12:20:00Z',
          },
        ],
      }),
      now,
    );
    const us = rows.find((r) => r.provider === 'TwelveData')!;
    expect(us.attemptedAndFailed).toBe(true);
    expect(us.errorSummary).not.toBeNull();
    expect(us.errorSummary!.length).toBeLessThan(longError.length);
    expect(us.errorSummary!.endsWith('…')).toBe(true);
  });

  it('titles each row from SOURCE_MARKET_TITLE, never the mid-sentence SOURCE_MARKET_LABEL', () => {
    // A screen reader/visual reader sees row titles in isolation, one per
    // column ("US stocks" / "SGX" / "Crypto") — never mid-sentence, where
    // `SOURCE_MARKET_LABEL`'s lowercase "crypto" is correct instead (see
    // describeRefreshOutcome/describeIdleRefreshPreview's own specs).
    const rows = buildMarketRefreshRows(status(), now);
    expect(rows.find((r) => r.provider === 'CoinGecko')!.label).toBe('Crypto');
    expect(rows.find((r) => r.provider === 'TwelveData')!.label).toBe('US stocks');
    expect(rows.find((r) => r.provider === 'Yahoo')!.label).toBe('SGX');
  });

  it('includes a Twelve Data cadence label only when the backend reports the interval', () => {
    const withInterval = buildMarketRefreshRows(
      status({ effectiveTwelveDataIntervalSeconds: 300 }),
      now,
    );
    expect(withInterval.find((r) => r.provider === 'TwelveData')!.cadenceLabel).toBe(
      'every ~5 min while open',
    );

    const withoutInterval = buildMarketRefreshRows(status(), now);
    expect(withoutInterval.find((r) => r.provider === 'TwelveData')!.cadenceLabel).toBeNull();
  });
});

describe('describeIdleRefreshPreview', () => {
  it('renders a waiting message when status has not loaded yet, never a closed-everything reading', () => {
    const message = describeIdleRefreshPreview(null);
    expect(message).toContain('Fetches live prices now');
    expect(message).not.toContain('closed');
  });

  it('names every market when all are open', () => {
    const message = describeIdleRefreshPreview(status({ nyseOpen: true, sgxOpen: true }));
    expect(message).toBe(
      'Fetches live prices now for US market, SGX and crypto. Daily closing prices and dividend history update on their own schedule and are not touched by this.',
    );
  });

  it('separates what will and will not refresh when one market is closed', () => {
    const message = describeIdleRefreshPreview(status({ nyseOpen: false, sgxOpen: true }));
    expect(message).toBe(
      "Fetches live prices now for SGX and crypto — US market is closed and won't be refreshed. Daily closing prices and dividend history update on their own schedule and are not touched by this.",
    );
  });

  it('names both closed markets together when only crypto will refresh', () => {
    const message = describeIdleRefreshPreview(status({ nyseOpen: false, sgxOpen: false }));
    expect(message).toContain('for crypto —');
    expect(message).toContain('US market and SGX are closed');
  });
});
