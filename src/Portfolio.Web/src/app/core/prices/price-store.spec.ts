import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { PRICES_HUB_CONNECTION_FACTORY, PriceStore } from './price-store';
import { FakeHubConnection } from './testing/fake-hub-connection';
import { API_ROUTES } from '../api/api-routes';
import {
  CatchUpCompletedNotification,
  PriceRefreshCycleResult,
  PriceRefreshStatus,
  QuoteUpdateNotification,
} from '../api/models';

describe('PriceStore', () => {
  let fakeConnection: FakeHubConnection;
  let httpMock: HttpTestingController;
  let store: PriceStore;

  function setup(startResult: 'resolve' | 'reject' = 'resolve'): void {
    fakeConnection = new FakeHubConnection();
    fakeConnection.startResult = startResult;

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: PRICES_HUB_CONNECTION_FACTORY, useValue: () => fakeConnection },
      ],
    });

    httpMock = TestBed.inject(HttpTestingController);
    store = TestBed.inject(PriceStore);
  }

  afterEach(() => {
    httpMock.verify();
  });

  it('becomes connected once the hub start() resolves, with no HTTP polling', async () => {
    setup('resolve');
    await Promise.resolve();
    await Promise.resolve();

    expect(store.connectionState()).toBe('connected');
    expect(store.isDegraded()).toBe(false);
  });

  it('applies a pushed QuoteUpdated notification, keyed by assetId', async () => {
    setup('resolve');
    await Promise.resolve();
    await Promise.resolve();

    const quote: QuoteUpdateNotification = {
      assetId: 1,
      symbol: 'ANVL',
      price: 0.0005326,
      currency: 'USD',
      asOf: '2026-07-31T12:00:00Z',
      source: 'Live',
    };
    fakeConnection.emit('QuoteUpdated', quote);

    expect(store.priceFor(1)).toEqual(quote);
  });

  it('applies a pushed RefreshStatus snapshot immediately on connect', async () => {
    setup('resolve');
    await Promise.resolve();
    await Promise.resolve();

    const status: PriceRefreshStatus = {
      lastRefreshedAt: '2026-07-31T12:00:00Z',
      nyseOpen: true,
      sgxOpen: false,
      nextScheduledRunAt: '2026-07-31T12:05:00Z',
      sources: [],
    };
    fakeConnection.emit('RefreshStatus', status);

    expect(store.status()).toEqual(status);
    expect(store.lastRefreshedAt()).toBe('2026-07-31T12:00:00Z');
  });

  it('falls back to polling GET /api/prices/status when the hub genuinely fails to connect', async () => {
    setup('reject');
    // Flush the rejected start() promise chain.
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();

    expect(store.connectionState()).toBe('polling-fallback');
    expect(store.isDegraded()).toBe(true);

    const req = httpMock.expectOne(API_ROUTES.pricesStatus);
    expect(req.request.method).toBe('GET');
    req.flush({
      lastRefreshedAt: null,
      nyseOpen: false,
      sgxOpen: false,
      nextScheduledRunAt: null,
      sources: [],
    } satisfies PriceRefreshStatus);
  });

  it('recovers to connected once onreconnected fires, and stops any fallback polling', async () => {
    setup('resolve');
    await Promise.resolve();
    await Promise.resolve();

    fakeConnection.onreconnectingCb?.();
    expect(store.connectionState()).toBe('reconnecting');

    fakeConnection.onreconnectedCb?.();
    expect(store.connectionState()).toBe('connected');
  });

  it('refreshNow() posts to /api/prices/refresh and records the result', async () => {
    setup('resolve');
    await Promise.resolve();
    await Promise.resolve();

    store.refreshNow();
    expect(store.refreshing()).toBe(true);

    const req = httpMock.expectOne(API_ROUTES.pricesRefresh);
    expect(req.request.method).toBe('POST');
    req.flush({
      outcome: 'Completed',
      cooldownSecondsRemaining: null,
      sources: [],
      totalSymbolsRefreshed: 3,
    });

    expect(store.refreshing()).toBe(false);
    expect(store.lastRefreshResult()?.totalSymbolsRefreshed).toBe(3);

    // refreshNow() also re-fetches status after a successful cycle.
    httpMock.expectOne(API_ROUTES.pricesStatus).flush({
      lastRefreshedAt: '2026-07-31T12:00:00Z',
      nyseOpen: true,
      sgxOpen: true,
      nextScheduledRunAt: null,
      sources: [],
    } satisfies PriceRefreshStatus);
  });

  it('a 429 response starts a cooldown countdown driven by secondsRemaining, not a client timer', async () => {
    setup('resolve');
    await Promise.resolve();
    await Promise.resolve();

    store.refreshNow();
    const req = httpMock.expectOne(API_ROUTES.pricesRefresh);
    req.flush({ secondsRemaining: 17 }, { status: 429, statusText: 'Too Many Requests' });

    expect(store.cooldownSecondsRemaining()).toBe(17);
    // A second click while cooling down must not issue another request.
    store.refreshNow();
    httpMock.expectNone(API_ROUTES.pricesRefresh);
  });

  describe('2026-09-14 catch-up feature', () => {
    /** Fires refreshNow(), flushes the POST with `result`, and flushes the
     *  status GET refreshNow() also issues on success — mirrors the existing
     *  "refreshNow() posts to /api/prices/refresh" test's shape above. */
    function completeRefresh(result: PriceRefreshCycleResult): void {
      store.refreshNow();
      httpMock.expectOne(API_ROUTES.pricesRefresh).flush(result);
      httpMock.expectOne(API_ROUTES.pricesStatus).flush({
        lastRefreshedAt: '2026-09-14T10:00:00Z',
        nyseOpen: true,
        sgxOpen: true,
        nextScheduledRunAt: null,
        sources: [],
      } satisfies PriceRefreshStatus);
    }

    function queuedResult(runId = 'run-1'): PriceRefreshCycleResult {
      return {
        outcome: 'Completed',
        cooldownSecondsRemaining: null,
        sources: [],
        totalSymbolsRefreshed: 0,
        catchUp: {
          priceHistory: {
            state: 'Queued',
            fetchSymbols: ['NEWCO'],
            fxPairs: [],
            retryPendingSymbols: [],
            notYetAvailableSymbols: [],
            runId,
          },
          dividends: {
            state: 'NothingToFetch',
            fetchSymbols: [],
            fxPairs: [],
            retryPendingSymbols: [],
            notYetAvailableSymbols: [],
            runId: null,
          },
        },
      };
    }

    function completion(
      overrides: Partial<CatchUpCompletedNotification> = {},
    ): CatchUpCompletedNotification {
      return {
        kind: 'PriceHistory',
        succeededSymbols: ['NEWCO'],
        failed: [],
        skippedForBudgetSymbols: [],
        rowsInserted: 12,
        completedAt: '2026-09-14T10:00:30Z',
        runId: 'run-1',
        ...overrides,
      };
    }

    it('records the catch-up plan carried on a manual refresh result', async () => {
      setup('resolve');
      await Promise.resolve();
      await Promise.resolve();

      completeRefresh(queuedResult());

      expect(store.catchUpPlan()).toEqual(queuedResult().catchUp);
    });

    it('clears a previous catch-up plan when a later result has none (absent catchUp)', async () => {
      setup('resolve');
      await Promise.resolve();
      await Promise.resolve();

      completeRefresh(queuedResult());
      expect(store.catchUpPlan()).not.toBeNull();

      completeRefresh({
        outcome: 'Completed',
        cooldownSecondsRemaining: null,
        sources: [],
        totalSymbolsRefreshed: 1,
        // no `catchUp` at all — an older server, or a shape that omits it.
      });

      expect(store.catchUpPlan()).toBeNull();
    });

    it('a Queued leg is in flight until a CatchUpCompleted with its runId arrives (plan before completion)', async () => {
      setup('resolve');
      await Promise.resolve();
      await Promise.resolve();

      completeRefresh(queuedResult('run-1'));

      expect(store.priceHistoryCatchUpInFlight()).toBe(true);
      // The dividends leg is NothingToFetch (runId: null) — never in flight.
      expect(store.dividendsCatchUpInFlight()).toBe(false);

      fakeConnection.emit('CatchUpCompleted', completion({ runId: 'run-1', rowsInserted: 12 }));

      expect(store.priceHistoryCatchUpInFlight()).toBe(false);
      expect(store.lastPriceHistoryCatchUpCompletion()?.rowsInserted).toBe(12);
    });

    it('THE RACE: a completion that arrives BEFORE its own plan is still matched once the plan lands, and never reads as in flight', async () => {
      // The exact bug this fixes: the backend starts the detached catch-up
      // task before it sends the POST response, so a fast leg (one Yahoo
      // dividend call) can finish and push CatchUpCompleted before
      // refreshNow()'s own `next` handler has even run.
      setup('resolve');
      await Promise.resolve();
      await Promise.resolve();

      // The completion arrives first — no plan is even tracked yet.
      fakeConnection.emit('CatchUpCompleted', completion({ runId: 'run-1', rowsInserted: 12 }));
      expect(store.priceHistoryCatchUpInFlight()).toBe(false); // nothing queued yet, trivially not in flight

      // The plan for that same run lands moments later.
      completeRefresh(queuedResult('run-1'));

      // Must NOT read "still fetching" forever — the id match was already on
      // file from before the plan arrived.
      expect(store.priceHistoryCatchUpInFlight()).toBe(false);
      expect(store.lastPriceHistoryCatchUpCompletion()?.rowsInserted).toBe(12);
    });

    it('plan before completion — the leg is in flight until the same-runId completion lands', async () => {
      setup('resolve');
      await Promise.resolve();
      await Promise.resolve();

      completeRefresh(queuedResult('run-1'));
      expect(store.priceHistoryCatchUpInFlight()).toBe(true);

      fakeConnection.emit('CatchUpCompleted', completion({ runId: 'run-1' }));
      expect(store.priceHistoryCatchUpInFlight()).toBe(false);
    });

    it('a completion for a DIFFERENT runId does not clear the current plan\'s in-flight state', async () => {
      setup('resolve');
      await Promise.resolve();
      await Promise.resolve();

      completeRefresh(queuedResult('run-2'));
      expect(store.priceHistoryCatchUpInFlight()).toBe(true);

      // A completion for some other run (an earlier click's re-queued leg,
      // or an unrelated one) must not be mistaken for this plan's own.
      fakeConnection.emit('CatchUpCompleted', completion({ runId: 'run-1' }));

      expect(store.priceHistoryCatchUpInFlight()).toBe(true);
      // Deliberately NOT shown — there is no "latest seen for this kind"
      // fallback; a completion for a run this plan didn't queue is not this
      // plan's news to report.
      expect(store.lastPriceHistoryCatchUpCompletion()).toBeNull();

      // The matching runId is what actually resolves it.
      fakeConnection.emit('CatchUpCompleted', completion({ runId: 'run-2' }));
      expect(store.priceHistoryCatchUpInFlight()).toBe(false);
      expect(store.lastPriceHistoryCatchUpCompletion()?.runId).toBe('run-2');
    });

    it('a leg with runId: null (NothingToFetch/AlreadyRunning) is never in flight, regardless of any completion', async () => {
      setup('resolve');
      await Promise.resolve();
      await Promise.resolve();

      // dividends leg is NothingToFetch with runId: null in queuedResult().
      completeRefresh(queuedResult('run-1'));
      expect(store.dividendsCatchUpInFlight()).toBe(false);

      fakeConnection.emit(
        'CatchUpCompleted',
        completion({ kind: 'Dividends', runId: 'run-1' }),
      );
      expect(store.dividendsCatchUpInFlight()).toBe(false);
    });

    it('REGRESSION: click 1 queues+finishes, click 2 finds NothingToFetch — the panel signal must not keep showing click 1\'s completion', async () => {
      // The exact live bug report: click 1 queued both legs and the panel
      // correctly read "Fetched price history for 7 stocks."; click 2, 60s
      // later, returned NothingToFetch for both legs (confirmed nothing was
      // spent), but the panel kept showing click 1's "Fetched…" text as if
      // it were click 2's outcome — a PREVIOUS click's news stated as CURRENT.
      setup('resolve');
      await Promise.resolve();
      await Promise.resolve();

      // Click 1: queued, then completes.
      completeRefresh(queuedResult('click-1-run'));
      fakeConnection.emit(
        'CatchUpCompleted',
        completion({ runId: 'click-1-run', succeededSymbols: ['A', 'B', 'C', 'D', 'E', 'F', 'G'] }),
      );
      expect(store.lastPriceHistoryCatchUpCompletion()?.runId).toBe('click-1-run');

      // Click 2: nothing due for either leg this time.
      completeRefresh({
        outcome: 'Completed',
        cooldownSecondsRemaining: null,
        sources: [],
        totalSymbolsRefreshed: 0,
        catchUp: {
          priceHistory: {
            state: 'NothingToFetch',
            fetchSymbols: [],
            fxPairs: [],
            retryPendingSymbols: [],
            notYetAvailableSymbols: [],
            runId: null,
          },
          dividends: {
            state: 'NothingToFetch',
            fetchSymbols: [],
            fxPairs: [],
            retryPendingSymbols: [],
            notYetAvailableSymbols: [],
            runId: null,
          },
        },
      });

      // Must be null now — click 1's completion is not click 2's news.
      expect(store.lastPriceHistoryCatchUpCompletion()).toBeNull();
      expect(store.lastDividendsCatchUpCompletion()).toBeNull();
      expect(store.priceHistoryCatchUpInFlight()).toBe(false);
      expect(store.dividendsCatchUpInFlight()).toBe(false);
    });

    it('sets the catch-up landing marker only when rowsInserted > 0', async () => {
      setup('resolve');
      await Promise.resolve();
      await Promise.resolve();

      fakeConnection.emit('CatchUpCompleted', completion({ rowsInserted: 0 }));
      expect(store.catchUpLandingMarker()).toBeNull();

      fakeConnection.emit('CatchUpCompleted', completion({ rowsInserted: 5 }));
      expect(store.catchUpLandingMarker()).not.toBeNull();
    });
  });

  describe('visibilitychange catch-up', () => {
    function setVisibility(state: DocumentVisibilityState): void {
      Object.defineProperty(document, 'visibilityState', { value: state, configurable: true });
      document.dispatchEvent(new Event('visibilitychange'));
    }

    afterEach(() => {
      // Restore jsdom's default so later tests/files aren't affected.
      Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true });
    });

    it('issues GET /api/prices/status when the document becomes visible', async () => {
      setup('resolve');
      await Promise.resolve();
      await Promise.resolve();

      setVisibility('visible');

      const req = httpMock.expectOne(API_ROUTES.pricesStatus);
      expect(req.request.method).toBe('GET');
      req.flush({
        lastRefreshedAt: '2026-08-01T10:00:00Z',
        nyseOpen: true,
        sgxOpen: false,
        nextScheduledRunAt: null,
        sources: [],
      } satisfies PriceRefreshStatus);

      expect(store.lastRefreshedAt()).toBe('2026-08-01T10:00:00Z');
    });

    it('does nothing when the document becomes hidden', async () => {
      setup('resolve');
      await Promise.resolve();
      await Promise.resolve();

      setVisibility('hidden');

      httpMock.expectNone(API_ROUTES.pricesStatus);
    });

    it('retries the hub connection immediately on visibility, rather than waiting out the retry interval, while polling-fallback', async () => {
      setup('reject');
      await Promise.resolve();
      await Promise.resolve();
      await Promise.resolve();

      expect(store.connectionState()).toBe('polling-fallback');

      // The initial fallback poll fired by entering degraded mode.
      httpMock.expectOne(API_ROUTES.pricesStatus).flush({
        lastRefreshedAt: null,
        nyseOpen: false,
        sgxOpen: false,
        nextScheduledRunAt: null,
        sources: [],
      } satisfies PriceRefreshStatus);

      fakeConnection.startResult = 'resolve';
      const startSpy = vi.spyOn(fakeConnection, 'start');

      setVisibility('visible');

      // The visibility handler's own status catch-up fetch.
      httpMock.expectOne(API_ROUTES.pricesStatus).flush({
        lastRefreshedAt: null,
        nyseOpen: false,
        sgxOpen: false,
        nextScheduledRunAt: null,
        sources: [],
      } satisfies PriceRefreshStatus);

      expect(startSpy).toHaveBeenCalled();

      await Promise.resolve();
      await Promise.resolve();

      expect(store.connectionState()).toBe('connected');
    });

    it('does not retry the hub connection on visibility while already connected', async () => {
      setup('resolve');
      await Promise.resolve();
      await Promise.resolve();
      expect(store.connectionState()).toBe('connected');

      const startSpy = vi.spyOn(fakeConnection, 'start');

      setVisibility('visible');
      httpMock.expectOne(API_ROUTES.pricesStatus).flush({
        lastRefreshedAt: null,
        nyseOpen: false,
        sgxOpen: false,
        nextScheduledRunAt: null,
        sources: [],
      } satisfies PriceRefreshStatus);

      expect(startSpy).not.toHaveBeenCalled();
    });
  });
});
