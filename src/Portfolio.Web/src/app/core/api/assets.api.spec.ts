import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { AssetsApi } from './assets.api';
import { API_ROUTES } from './api-routes';
import { AssetDto } from './models';

const EXISTING: AssetDto = {
  id: 3,
  symbol: 'MSFT',
  name: 'Microsoft Corporation',
  assetClass: 'Stock',
  exchange: 'NASDAQ',
  currency: 'USD',
  quoteProviderKind: 'TwelveData',
  providerSymbol: 'MSFT',
  providerCoinId: null,
  isActive: true,
  createdAt: '2026-01-01T00:00:00+00:00',
  hasEverBeenPriced: true,
  providerHasEverSucceeded: true,
  fiscalYearEndMonth: null,
  fiscalYearEndDay: null,
};

describe('AssetsApi', () => {
  let api: AssetsApi;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(AssetsApi);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('list() issues a GET to /api/assets', () => {
    TestBed.runInInjectionContext(() => api.list());
    TestBed.tick();
    const req = httpMock.expectOne(API_ROUTES.assets);
    expect(req.request.method).toBe('GET');
    req.flush([EXISTING]);
  });

  it('performance() is never requested when the accessor returns undefined', () => {
    TestBed.runInInjectionContext(() => api.performance(() => undefined));
    TestBed.tick();
    httpMock.expectNone((r) => r.url.includes('/performance'));
  });

  it('performance() requests /api/assets/{id}/performance once the accessor resolves', () => {
    TestBed.runInInjectionContext(() => api.performance(() => 3));
    TestBed.tick();
    httpMock.expectOne(API_ROUTES.assetPerformance(3));
  });

  it('dividends() is never requested when the accessor returns undefined', () => {
    TestBed.runInInjectionContext(() => api.dividends(() => undefined));
    TestBed.tick();
    httpMock.expectNone((r) => r.url.includes('/dividends'));
  });

  it('dividends() requests /api/assets/{id}/dividends once the accessor resolves', () => {
    TestBed.runInInjectionContext(() => api.dividends(() => 3));
    TestBed.tick();
    httpMock.expectOne(API_ROUTES.assetDividends(3));
  });

  it('create() POSTs the request as-is', () => {
    api
      .create({
        symbol: 'GOOGL',
        name: 'Alphabet Inc.',
        assetClass: 'Stock',
        exchange: null,
        currency: 'USD',
        quoteProviderKind: 'TwelveData',
        providerSymbol: 'GOOGL',
        providerCoinId: null,
        fiscalYearEndMonth: null,
        fiscalYearEndDay: null,
      })
      .subscribe();

    const req = httpMock.expectOne(API_ROUTES.assets);
    expect(req.request.method).toBe('POST');
    req.flush(EXISTING);
  });

  it('delete() DELETEs /api/assets/{id}', () => {
    api.delete(3).subscribe();
    const req = httpMock.expectOne(API_ROUTES.asset(3));
    expect(req.request.method).toBe('DELETE');
    req.flush(null, { status: 204, statusText: 'No Content' });
  });

  // --- replace() — the ONE place "PUT is a full replace" is encoded. Both
  // shapes below must reproduce the exact bodies the two hand-built call
  // sites sent before F1 (frontend-solid.md) — see asset-management.page.ts's
  // old toggleActive and asset-form.dialog.ts's old edit submit.

  it('replace() with only { isActive } resends every other field from the existing asset unchanged (AssetManagementPage.toggleActive shape)', () => {
    api.replace(EXISTING, { isActive: false }).subscribe();

    const req = httpMock.expectOne(API_ROUTES.asset(EXISTING.id));
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({
      symbol: 'MSFT',
      name: 'Microsoft Corporation',
      assetClass: 'Stock',
      exchange: 'NASDAQ',
      currency: 'USD',
      quoteProviderKind: 'TwelveData',
      providerSymbol: 'MSFT',
      providerCoinId: null,
      fiscalYearEndMonth: null,
      fiscalYearEndDay: null,
      isActive: false,
    });
    req.flush({ ...EXISTING, isActive: false });
  });

  it('replace() with a full CreateAssetRequest plus no isActive override carries the existing isActive through unchanged (AssetFormDialog edit-submit shape)', () => {
    api
      .replace(EXISTING, {
        symbol: 'MSFT',
        name: 'Microsoft Corporation',
        assetClass: 'Stock',
        exchange: 'NASDAQ',
        currency: 'USD',
        quoteProviderKind: 'TwelveData',
        providerSymbol: 'MSFT',
        providerCoinId: null,
        fiscalYearEndMonth: 6,
        fiscalYearEndDay: 30,
      })
      .subscribe();

    const req = httpMock.expectOne(API_ROUTES.asset(EXISTING.id));
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({
      symbol: 'MSFT',
      name: 'Microsoft Corporation',
      assetClass: 'Stock',
      exchange: 'NASDAQ',
      currency: 'USD',
      quoteProviderKind: 'TwelveData',
      providerSymbol: 'MSFT',
      providerCoinId: null,
      fiscalYearEndMonth: 6,
      fiscalYearEndDay: 30,
      isActive: true, // carried through from EXISTING, never part of `changes` here
    });
    req.flush({ ...EXISTING, fiscalYearEndMonth: 6, fiscalYearEndDay: 30 });
  });
});
