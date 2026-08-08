import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { PRICES_HUB_CONNECTION_FACTORY, PriceStore } from './price-store';
import { FakeHubConnection } from './testing/fake-hub-connection';
import { API_ROUTES } from '../api/api-routes';
import { PriceRefreshStatus, QuoteUpdateNotification } from '../api/models';

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
});
