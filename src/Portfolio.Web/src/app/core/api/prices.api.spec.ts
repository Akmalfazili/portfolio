import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { PricesApi } from './prices.api';
import { API_ROUTES } from './api-routes';
import { PriceRefreshCycleResult, PriceRefreshStatus } from './models';

describe('PricesApi', () => {
  let api: PricesApi;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(PricesApi);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('refresh() POSTs an empty body to /api/prices/refresh', () => {
    api.refresh().subscribe();

    const req = httpMock.expectOne(API_ROUTES.pricesRefresh);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({});
    req.flush({
      outcome: 'Completed',
      cooldownSecondsRemaining: null,
      sources: [],
      totalSymbolsRefreshed: 0,
    } satisfies PriceRefreshCycleResult);
  });

  it('status() GETs /api/prices/status', () => {
    api.status().subscribe();

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
});
