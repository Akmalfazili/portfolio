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

  it('times a failed attempt with the same "N ago" wording as lastSuccessLabel, so a stale failure cannot read as current', () => {
    // A frozen SGX status from a Friday-evening outage, read on a Sunday
    // while the market is closed (D-style staleness bug): the failure
    // shouldn't read as if it "just" happened just because nothing has
    // retried it since.
    const rows = buildMarketRefreshRows(
      status({
        sgxOpen: false,
        sources: [
          {
            source: 'Yahoo',
            lastAttemptedAt: '2026-07-31T04:00:00Z', // 8h10m before `now`
            lastSuccessAt: '2026-07-28T04:00:00Z',
            lastRunSuccess: false,
            lastError: 'Yahoo request failed.',
            symbolsRefreshed: 0,
            nextDueAt: '2026-08-03T02:00:00Z',
          },
        ],
      }),
      now,
    );
    const sgx = rows.find((r) => r.provider === 'Yahoo')!;
    expect(sgx.attemptedAndFailed).toBe(true);
    expect(sgx.lastAttemptedLabel).toBe('8h ago');
  });

  it('degrades to a null lastAttemptedLabel — never "Invalid Date" — when a failed row has no lastAttemptedAt', () => {
    const rows = buildMarketRefreshRows(
      status({
        sources: [
          {
            source: 'Yahoo',
            lastAttemptedAt: null,
            lastSuccessAt: '2026-07-28T04:00:00Z',
            lastRunSuccess: false,
            lastError: 'Yahoo request failed.',
            symbolsRefreshed: 0,
            nextDueAt: null,
          },
        ],
      }),
      now,
    );
    const sgx = rows.find((r) => r.provider === 'Yahoo')!;
    expect(sgx.attemptedAndFailed).toBe(true);
    expect(sgx.lastAttemptedLabel).toBeNull();
  });

  it('never surfaces a lastAttemptedLabel for a row that was not attempted-and-failed', () => {
    const rows = buildMarketRefreshRows(
      status({
        sources: [
          {
            source: 'TwelveData',
            lastAttemptedAt: '2026-07-31T12:00:00Z',
            lastSuccessAt: '2026-07-31T12:00:00Z',
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
    expect(us.attemptedAndFailed).toBe(false);
    expect(us.lastAttemptedLabel).toBeNull();
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

  describe('closing-price (backfill) status and the live-failure supersede rule', () => {
    // The exact 2026-09-13 live incident: SGX closed all weekend, Yahoo's last
    // live poll (Friday evening, before the close) failed in a host network
    // outage, but Friday's SGX close was recorded successfully the next day.
    const sgxNow = new Date('2026-09-13T10:00:00Z').getTime();

    function sgxStatus(closes: PriceRefreshStatus['closes']) {
      return status({
        sgxOpen: false,
        sources: [
          {
            source: 'Yahoo',
            lastAttemptedAt: '2026-09-11T08:56:27Z',
            lastSuccessAt: '2026-09-04T09:00:00Z',
            lastRunSuccess: false,
            lastError: 'Yahoo request failed.',
            symbolsRefreshed: 0,
            nextDueAt: null,
          },
        ],
        closes,
      });
    }

    it('supersedes a stale closed-market live failure with a later close success', () => {
      const rows = buildMarketRefreshRows(
        sgxStatus([
          {
            market: 'Sgx',
            latestCloseDate: '2026-09-11',
            lastAttemptedAt: '2026-09-12T17:01:22Z',
            lastSuccessAt: '2026-09-12T17:01:22Z',
            lastRunSuccess: true,
            lastError: null,
          },
        ]),
        sgxNow,
      );
      const sgx = rows.find((r) => r.provider === 'Yahoo')!;
      expect(sgx.attemptedAndFailed).toBe(true); // the raw fact is unchanged
      expect(sgx.showLiveFailure).toBe(false); // but display suppresses it
      expect(sgx.closeThroughLabel).toBe('Closing prices through Fri 11 Sep · recorded 17h ago');
      expect(sgx.closeFailedLabel).toBeNull();
    });

    it('still shows the live failure while the market is OPEN, even with a later close success', () => {
      const openStatus = status({
        sgxOpen: true,
        sources: [
          {
            source: 'Yahoo',
            lastAttemptedAt: '2026-09-11T08:56:27Z',
            lastSuccessAt: '2026-09-04T09:00:00Z',
            lastRunSuccess: false,
            lastError: 'Yahoo request failed.',
            symbolsRefreshed: 0,
            nextDueAt: null,
          },
        ],
        closes: [
          {
            market: 'Sgx',
            latestCloseDate: '2026-09-11',
            lastAttemptedAt: '2026-09-12T17:01:22Z',
            lastSuccessAt: '2026-09-12T17:01:22Z',
            lastRunSuccess: true,
            lastError: null,
          },
        ],
      });
      const rows = buildMarketRefreshRows(openStatus, sgxNow);
      const sgx = rows.find((r) => r.provider === 'Yahoo')!;
      expect(sgx.showLiveFailure).toBe(true);
    });

    it('does NOT supersede when the close success is OLDER than the live failure', () => {
      const rows = buildMarketRefreshRows(
        sgxStatus([
          {
            market: 'Sgx',
            latestCloseDate: '2026-09-04',
            lastAttemptedAt: '2026-09-04T09:00:00Z',
            lastSuccessAt: '2026-09-04T09:00:00Z', // before the 09-11 live failure
            lastRunSuccess: true,
            lastError: null,
          },
        ]),
        sgxNow,
      );
      const sgx = rows.find((r) => r.provider === 'Yahoo')!;
      expect(sgx.showLiveFailure).toBe(true);
    });

    it('behaves exactly as if `closes` did not exist when it is absent', () => {
      const rows = buildMarketRefreshRows(sgxStatus(undefined), sgxNow);
      const sgx = rows.find((r) => r.provider === 'Yahoo')!;
      expect(sgx.showLiveFailure).toBe(true);
      expect(sgx.closeThroughLabel).toBeNull();
      expect(sgx.closeFailedLabel).toBeNull();
    });

    it('behaves exactly as if `closes` did not exist when it is null', () => {
      const rows = buildMarketRefreshRows(sgxStatus(null), sgxNow);
      const sgx = rows.find((r) => r.provider === 'Yahoo')!;
      expect(sgx.showLiveFailure).toBe(true);
      expect(sgx.closeThroughLabel).toBeNull();
      expect(sgx.closeFailedLabel).toBeNull();
    });

    it('drops the "recorded …" clause, never "recorded never", when latestCloseDate is set but lastSuccessAt is null', () => {
      // A pre-D47 successful backfill run recorded with Market == null, so
      // the backend has a real closing date on file but no dated success to
      // attribute it to. "Closing prices through Fri 11 Sep · recorded
      // never" would contradict itself — the clause must be dropped entirely.
      const rows = buildMarketRefreshRows(
        sgxStatus([
          {
            market: 'Sgx',
            latestCloseDate: '2026-09-11',
            lastAttemptedAt: null,
            lastSuccessAt: null,
            lastRunSuccess: true,
            lastError: null,
          },
        ]),
        sgxNow,
      );
      const sgx = rows.find((r) => r.provider === 'Yahoo')!;
      expect(sgx.closeThroughLabel).toBe('Closing prices through Fri 11 Sep');
    });

    it('a close entry with lastRunSuccess: null ("never attempted") shows neither a failure nor a supersede', () => {
      const rows = buildMarketRefreshRows(
        sgxStatus([
          {
            market: 'Sgx',
            latestCloseDate: null,
            lastAttemptedAt: null,
            lastSuccessAt: null,
            lastRunSuccess: null,
            lastError: null,
          },
        ]),
        sgxNow,
      );
      const sgx = rows.find((r) => r.provider === 'Yahoo')!;
      expect(sgx.closeFailedLabel).toBeNull();
      expect(sgx.closeThroughLabel).toBeNull();
      expect(sgx.showLiveFailure).toBe(true); // nothing to supersede with
    });

    it('a close entry with lastRunSuccess: false renders a distinct, timed failure line', () => {
      const rows = buildMarketRefreshRows(
        sgxStatus([
          {
            market: 'Sgx',
            latestCloseDate: '2026-09-04',
            lastAttemptedAt: '2026-09-12T17:01:22Z',
            lastSuccessAt: '2026-09-04T09:00:00Z',
            lastRunSuccess: false,
            lastError: 'Yahoo chart endpoint returned HTTP 500',
          },
        ]),
        sgxNow,
      );
      const sgx = rows.find((r) => r.provider === 'Yahoo')!;
      expect(sgx.closeFailedLabel).toBe(
        'Last closing-price update failed 17h ago — Yahoo chart endpoint returned HTTP 500',
      );
      // A failed backfill run can still have an honest floor from an earlier success.
      expect(sgx.closeThroughLabel).toBe('Closing prices through Fri 4 Sep · recorded 9d ago');
    });

    it('never attaches a close entry to CoinGecko — crypto keeps no price history', () => {
      const rows = buildMarketRefreshRows(
        status({
          closes: [
            {
              market: 'Nyse',
              latestCloseDate: null,
              lastAttemptedAt: null,
              lastSuccessAt: null,
              lastRunSuccess: null,
              lastError: null,
            },
            {
              market: 'Sgx',
              latestCloseDate: null,
              lastAttemptedAt: null,
              lastSuccessAt: null,
              lastRunSuccess: null,
              lastError: null,
            },
          ],
        }),
        now,
      );
      const crypto = rows.find((r) => r.provider === 'CoinGecko')!;
      expect(crypto.closeThroughLabel).toBeNull();
      expect(crypto.closeFailedLabel).toBeNull();
    });
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
