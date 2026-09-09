import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { TransactionsApi } from './transactions.api';
import { API_ROUTES } from './api-routes';
import { TransactionDto } from './models';

const TXN: TransactionDto = {
  id: 1,
  assetId: 3,
  assetSymbol: 'MSFT',
  assetClass: 'Stock',
  type: 'Buy',
  tradeDate: '2026-01-01',
  quantity: 1,
  pricePerUnit: 100,
  fees: 0,
  currency: 'USD',
  notes: null,
};

describe('TransactionsApi', () => {
  let api: TransactionsApi;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(TransactionsApi);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('list() issues a GET to /api/transactions', () => {
    TestBed.runInInjectionContext(() => api.list());
    TestBed.tick();
    const req = httpMock.expectOne(API_ROUTES.transactions);
    expect(req.request.method).toBe('GET');
    req.flush([TXN]);
  });

  it('byAsset() is never requested when the accessor returns undefined', () => {
    TestBed.runInInjectionContext(() => api.byAsset(() => undefined));
    TestBed.tick();
    httpMock.expectNone((r) => r.url.startsWith('/api/transactions'));
  });

  it('byAsset() requests /api/transactions?assetId={id} once the accessor resolves', () => {
    TestBed.runInInjectionContext(() => api.byAsset(() => 3));
    TestBed.tick();
    httpMock.expectOne(API_ROUTES.transactionsByAsset(3));
  });

  it('byAssetOnce() is a one-shot GET to the same URL as byAsset(), not a resource', () => {
    api.byAssetOnce(3).subscribe();
    const req = httpMock.expectOne(API_ROUTES.transactionsByAsset(3));
    expect(req.request.method).toBe('GET');
    req.flush([TXN]);
  });

  it('create() POSTs the request as-is', () => {
    api
      .create({
        assetId: 3,
        type: 'Buy',
        tradeDate: '2026-01-01',
        quantity: 1,
        pricePerUnit: 100,
        fees: 0,
        currency: 'USD',
        notes: null,
      })
      .subscribe();

    const req = httpMock.expectOne(API_ROUTES.transactions);
    expect(req.request.method).toBe('POST');
    req.flush(TXN);
  });

  it('update() PUTs the full request unchanged — no partial-replace shape, unlike AssetsApi.replace', () => {
    const request = {
      assetId: 3,
      type: 'Sell' as const,
      tradeDate: '2026-02-01',
      quantity: 1,
      pricePerUnit: 110,
      fees: 1.5,
      currency: 'USD',
      notes: 'trimmed',
    };
    api.update(1, request).subscribe();

    const req = httpMock.expectOne(API_ROUTES.transaction(1));
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual(request);
    req.flush({ ...TXN, ...request });
  });

  it('delete() DELETEs /api/transactions/{id}', () => {
    api.delete(1).subscribe();
    const req = httpMock.expectOne(API_ROUTES.transaction(1));
    expect(req.request.method).toBe('DELETE');
    req.flush(null, { status: 204, statusText: 'No Content' });
  });
});
