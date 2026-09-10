import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { PortfolioApi } from './portfolio.api';
import { API_ROUTES } from './api-routes';

describe('PortfolioApi', () => {
  let api: PortfolioApi;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(PortfolioApi);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('summary() requests /api/portfolio/{assetClass}/summary, reactive on the accessor', () => {
    TestBed.runInInjectionContext(() => api.summary(() => 'Crypto'));
    TestBed.tick();
    httpMock.expectOne(API_ROUTES.portfolioSummary('Crypto'));
  });

  it('allocation() requests /api/portfolio/{assetClass}/allocation', () => {
    TestBed.runInInjectionContext(() => api.allocation(() => 'Stock'));
    TestBed.tick();
    httpMock.expectOne(API_ROUTES.portfolioAllocation('Stock'));
  });

  it('annualReturns() requests the stock-only endpoint for Stock', () => {
    TestBed.runInInjectionContext(() => api.annualReturns(() => 'Stock'));
    TestBed.tick();
    httpMock.expectOne(API_ROUTES.stockAnnualReturns);
  });

  it('annualReturns() is never requested for Crypto — crypto keeps no price history', () => {
    TestBed.runInInjectionContext(() => api.annualReturns(() => 'Crypto'));
    TestBed.tick();
    httpMock.expectNone(API_ROUTES.stockAnnualReturns);
  });

  it('performance() requests the stock-only portfolio-wide endpoint for Stock', () => {
    TestBed.runInInjectionContext(() => api.performance(() => 'Stock'));
    TestBed.tick();
    httpMock.expectOne(API_ROUTES.stockPerformance);
  });

  it('performance() is never requested for Crypto — crypto keeps no price history', () => {
    TestBed.runInInjectionContext(() => api.performance(() => 'Crypto'));
    TestBed.tick();
    httpMock.expectNone(API_ROUTES.stockPerformance);
  });
});
