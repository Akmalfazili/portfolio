import { TestBed } from '@angular/core/testing';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { routes } from './app.routes';
import { PortfolioOverviewPage } from './features/portfolio/portfolio-overview.page';
import { AssetDetailPage } from './features/asset-detail/asset-detail.page';
import { API_ROUTES } from './core/api/api-routes';
import { PRICES_HUB_CONNECTION_FACTORY } from './core/prices/price-store';
import { FakeHubConnection } from './core/prices/testing/fake-hub-connection';

/**
 * End-to-end proof (within a test harness) that the Phase 7 routing contract
 * actually works: lazy loading, `data.assetClass` bound to the shared page's
 * input via `withComponentInputBinding()`, the `:symbol` route param bound
 * the same way, and the `data-section` attribute the shell drives the accent
 * tokens from — not just each piece asserted in isolation.
 */
describe('app routing — assetClass parameterisation', () => {
  let harness: RouterTestingHarness;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    document.documentElement.removeAttribute('data-section');
    await TestBed.configureTestingModule({
      providers: [
        provideRouter(routes, withComponentInputBinding()),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: PRICES_HUB_CONNECTION_FACTORY, useValue: () => new FakeHubConnection() },
      ],
    }).compileComponents();

    harness = await RouterTestingHarness.create();
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    document.documentElement.removeAttribute('data-section');
  });

  it('redirects / to /stocks', async () => {
    await harness.navigateByUrl('/');
    expect(harness.routeNativeElement).toBeTruthy();
    httpMock.expectOne(API_ROUTES.assets).flush([]);
  });

  it('binds data.assetClass="Stock" onto PortfolioOverviewPage for /stocks', async () => {
    const instance = await harness.navigateByUrl('/stocks', PortfolioOverviewPage);
    expect(instance.assetClass()).toBe('Stock');
    httpMock.expectOne(API_ROUTES.assets).flush([]);
  });

  it('binds data.assetClass="Crypto" onto the SAME PortfolioOverviewPage component for /crypto', async () => {
    const instance = await harness.navigateByUrl('/crypto', PortfolioOverviewPage);
    expect(instance.assetClass()).toBe('Crypto');
    httpMock.expectOne(API_ROUTES.assets).flush([]);
  });

  it('binds both the :symbol param and data.assetClass onto AssetDetailPage', async () => {
    const instance = await harness.navigateByUrl('/stocks/AAPL', AssetDetailPage);
    expect(instance.symbol()).toBe('AAPL');
    expect(instance.assetClass()).toBe('Stock');
    httpMock.expectOne(API_ROUTES.assets).flush([]);
  });

  it('an unknown path redirects to /stocks rather than a blank/broken route', async () => {
    const instance = await harness.navigateByUrl('/nonsense', PortfolioOverviewPage);
    expect(instance.assetClass()).toBe('Stock');
    httpMock.expectOne(API_ROUTES.assets).flush([]);
  });
});
