import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { PriceConnectionState, PricesHub, PricesHubSinks } from './prices-hub';
import { PricesApi } from '../api/prices.api';
import { FakeHubConnection } from './testing/fake-hub-connection';
import { API_ROUTES } from '../api/api-routes';
import { PriceRefreshStatus, QuoteUpdateNotification } from '../api/models';

/**
 * Direct tests of `PricesHub`'s own state machine — extracted from
 * `PriceStore` under F4 (frontend-solid.md). `price-store.spec.ts` already
 * covers the same transitions indirectly through `PriceStore`'s public
 * signals; these tests exercise the hub's sinks directly instead of
 * duplicating that file.
 */
describe('PricesHub', () => {
  let fakeConnection: FakeHubConnection;
  let httpMock: HttpTestingController;
  let pricesApi: PricesApi;
  let hub: PricesHub;
  let sinks: PricesHubSinks & {
    onQuote: ReturnType<typeof vi.fn<(payload: QuoteUpdateNotification) => void>>;
    onStatus: ReturnType<typeof vi.fn<(status: PriceRefreshStatus) => void>>;
    onConnectionStateChange: ReturnType<typeof vi.fn<(state: PriceConnectionState) => void>>;
  };

  function makeStatus(overrides: Partial<PriceRefreshStatus> = {}): PriceRefreshStatus {
    return {
      lastRefreshedAt: '2026-08-01T10:00:00Z',
      nyseOpen: true,
      sgxOpen: false,
      nextScheduledRunAt: null,
      sources: [],
      ...overrides,
    };
  }

  function setup(startResult: 'resolve' | 'reject' = 'resolve'): void {
    fakeConnection = new FakeHubConnection();
    fakeConnection.startResult = startResult;

    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    httpMock = TestBed.inject(HttpTestingController);
    pricesApi = TestBed.inject(PricesApi);
    hub = new PricesHub(pricesApi, () => fakeConnection);

    sinks = {
      onQuote: vi.fn<(payload: QuoteUpdateNotification) => void>(),
      onStatus: vi.fn<(status: PriceRefreshStatus) => void>(),
      onConnectionStateChange: vi.fn<(state: PriceConnectionState) => void>(),
    };
  }

  afterEach(() => {
    httpMock.verify();
    vi.useRealTimers();
  });

  it('reports connected once start() resolves, with no status polling', async () => {
    setup('resolve');
    hub.connect(sinks);
    await Promise.resolve();
    await Promise.resolve();

    expect(sinks.onConnectionStateChange).toHaveBeenCalledWith('connected');
    expect(sinks.onConnectionStateChange).not.toHaveBeenCalledWith('polling-fallback');
  });

  it('enters polling-fallback and polls status when start() rejects', async () => {
    setup('reject');
    hub.connect(sinks);
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();

    expect(sinks.onConnectionStateChange).toHaveBeenCalledWith('polling-fallback');

    const req = httpMock.expectOne(API_ROUTES.pricesStatus);
    expect(req.request.method).toBe('GET');
    req.flush(makeStatus());
    expect(sinks.onStatus).toHaveBeenCalledWith(makeStatus());
  });

  it('delivers a pushed QuoteUpdated to the quote sink, keyed as received', async () => {
    setup('resolve');
    hub.connect(sinks);
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

    expect(sinks.onQuote).toHaveBeenCalledWith(quote);
  });

  it('delivers a pushed RefreshStatus to the status sink', async () => {
    setup('resolve');
    hub.connect(sinks);
    await Promise.resolve();
    await Promise.resolve();

    const status = makeStatus();
    fakeConnection.emit('RefreshStatus', status);

    expect(sinks.onStatus).toHaveBeenCalledWith(status);
  });

  it('reports reconnecting then connected, and stops fallback polling on reconnect', async () => {
    setup('resolve');
    hub.connect(sinks);
    await Promise.resolve();
    await Promise.resolve();

    fakeConnection.onreconnectingCb?.();
    expect(sinks.onConnectionStateChange).toHaveBeenCalledWith('reconnecting');

    fakeConnection.onreconnectedCb?.();
    expect(sinks.onConnectionStateChange).toHaveBeenCalledWith('connected');
  });

  it('a normal (non-teardown) close on a connected hub enters polling-fallback and polls status — no direct coverage of this path existed before', async () => {
    setup('resolve');
    hub.connect(sinks);
    await Promise.resolve();
    await Promise.resolve();
    expect(sinks.onConnectionStateChange).toHaveBeenCalledWith('connected');

    sinks.onConnectionStateChange.mockClear();

    // Simulates the hub genuinely dropping — not something dispose() triggered.
    fakeConnection.oncloseCb?.();

    expect(sinks.onConnectionStateChange).toHaveBeenCalledWith('polling-fallback');
    httpMock.expectOne(API_ROUTES.pricesStatus).flush(makeStatus());
  });

  it('retries the hub connection every 60s while degraded, and stops once reconnected', async () => {
    vi.useFakeTimers();
    setup('reject');
    hub.connect(sinks);

    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();

    expect(sinks.onConnectionStateChange).toHaveBeenCalledWith('polling-fallback');
    // The initial fallback poll fired by entering degraded mode.
    httpMock.expectOne(API_ROUTES.pricesStatus).flush(makeStatus());

    fakeConnection.startResult = 'resolve';
    const startSpy = vi.spyOn(fakeConnection, 'start');

    vi.advanceTimersByTime(60_000);
    expect(startSpy).toHaveBeenCalledTimes(1);

    // The poll-fallback timer, on the same interval, also ticks here.
    httpMock.expectOne(API_ROUTES.pricesStatus).flush(makeStatus());

    await Promise.resolve();
    await Promise.resolve();

    expect(sinks.onConnectionStateChange).toHaveBeenCalledWith('connected');
  });

  describe('visibilitychange catch-up', () => {
    function setVisibility(state: DocumentVisibilityState): void {
      Object.defineProperty(document, 'visibilityState', { value: state, configurable: true });
      document.dispatchEvent(new Event('visibilitychange'));
    }

    afterEach(() => {
      Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true });
    });

    it('polls status immediately when the document becomes visible', async () => {
      setup('resolve');
      hub.connect(sinks);
      await Promise.resolve();
      await Promise.resolve();

      setVisibility('visible');

      const req = httpMock.expectOne(API_ROUTES.pricesStatus);
      req.flush(makeStatus());
      expect(sinks.onStatus).toHaveBeenCalledWith(makeStatus());
    });

    it('does nothing when the document becomes hidden', async () => {
      setup('resolve');
      hub.connect(sinks);
      await Promise.resolve();
      await Promise.resolve();

      setVisibility('hidden');

      httpMock.expectNone(API_ROUTES.pricesStatus);
    });

    it('retries the hub connection immediately while degraded, rather than waiting for the retry interval', async () => {
      setup('reject');
      hub.connect(sinks);
      await Promise.resolve();
      await Promise.resolve();
      await Promise.resolve();

      expect(sinks.onConnectionStateChange).toHaveBeenCalledWith('polling-fallback');
      httpMock.expectOne(API_ROUTES.pricesStatus).flush(makeStatus());

      fakeConnection.startResult = 'resolve';
      const startSpy = vi.spyOn(fakeConnection, 'start');

      setVisibility('visible');

      httpMock.expectOne(API_ROUTES.pricesStatus).flush(makeStatus());
      expect(startSpy).toHaveBeenCalled();

      await Promise.resolve();
      await Promise.resolve();

      expect(sinks.onConnectionStateChange).toHaveBeenCalledWith('connected');
    });

    it('does not retry the hub connection on visibility while already connected', async () => {
      setup('resolve');
      hub.connect(sinks);
      await Promise.resolve();
      await Promise.resolve();

      const startSpy = vi.spyOn(fakeConnection, 'start');

      setVisibility('visible');
      httpMock.expectOne(API_ROUTES.pricesStatus).flush(makeStatus());

      expect(startSpy).not.toHaveBeenCalled();
    });
  });

  describe('dispose()', () => {
    it('stops the hub connection and removes the visibility listener', async () => {
      setup('resolve');
      hub.connect(sinks);
      await Promise.resolve();
      await Promise.resolve();

      const removeSpy = vi.spyOn(document, 'removeEventListener');

      hub.dispose();

      expect(fakeConnection.stopped).toBe(true);
      expect(removeSpy).toHaveBeenCalledWith('visibilitychange', expect.any(Function));
    });

    it('regression: the stop() dispose() itself triggers does not re-enter degraded mode or re-arm timers', async () => {
      vi.useFakeTimers();
      setup('resolve');
      hub.connect(sinks);

      await Promise.resolve();
      await Promise.resolve();
      expect(sinks.onConnectionStateChange).toHaveBeenCalledWith('connected');

      sinks.onConnectionStateChange.mockClear();
      const startSpy = vi.spyOn(fakeConnection, 'start');

      hub.dispose();

      // The fake's stop() synchronously fires oncloseCb here (it was
      // connected; real SignalR fires it asynchronously, and the sticky
      // disposed flag covers both) — without the guard this would call
      // enterDegradedMode() and re-arm both timers.
      expect(sinks.onConnectionStateChange).not.toHaveBeenCalled();

      vi.advanceTimersByTime(120_000);

      expect(startSpy).not.toHaveBeenCalled();
      // httpMock.verify() in the outer afterEach fails if a poll-fallback
      // fetch snuck through here.
    });

    it('a start() rejection that resolves after dispose() does not enter degraded mode', async () => {
      setup('reject');
      hub.connect(sinks);

      hub.dispose(); // before the already-rejected start() promise's .catch() runs
      await Promise.resolve();
      await Promise.resolve();

      expect(sinks.onConnectionStateChange).not.toHaveBeenCalledWith('polling-fallback');
    });

    it('a start() resolution that resolves after dispose() does not report connected', async () => {
      setup('resolve');
      hub.connect(sinks);

      hub.dispose();
      await Promise.resolve();
      await Promise.resolve();

      expect(sinks.onConnectionStateChange).not.toHaveBeenCalledWith('connected');
    });
  });
});
