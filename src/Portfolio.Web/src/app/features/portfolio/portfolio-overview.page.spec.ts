import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';

import { PortfolioOverviewPage } from './portfolio-overview.page';
import { API_ROUTES } from '../../core/api/api-routes';
import { AssetDto } from '../../core/api/models';
import { PRICES_HUB_CONNECTION_FACTORY } from '../../core/prices/price-store';
import { FakeHubConnection } from '../../core/prices/testing/fake-hub-connection';

const ASSETS: AssetDto[] = [
  {
    id: 1,
    symbol: 'AAPL',
    name: 'Apple Inc.',
    assetClass: 'Stock',
    exchange: 'NASDAQ',
    currency: 'USD',
    quoteProviderKind: 'TwelveData',
    providerSymbol: 'AAPL',
    providerCoinId: null,
    isActive: true,
  },
  {
    id: 2,
    symbol: 'ethereum',
    name: 'Ethereum',
    assetClass: 'Crypto',
    exchange: null,
    currency: 'USD',
    quoteProviderKind: 'CoinGecko',
    providerSymbol: null,
    providerCoinId: 'ethereum',
    isActive: true,
  },
];

describe('PortfolioOverviewPage', () => {
  let fixture: ComponentFixture<PortfolioOverviewPage>;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [PortfolioOverviewPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: PRICES_HUB_CONNECTION_FACTORY, useValue: () => new FakeHubConnection() },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(PortfolioOverviewPage);
    fixture.componentRef.setInput('assetClass', 'Stock');
  });

  afterEach(() => httpMock.verify());

  it('shows the loading state before the request resolves', () => {
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Loading your holdings');
    httpMock.expectOne(API_ROUTES.assets).flush(ASSETS);
  });

  it('filters to the bound assetClass and lists only matching holdings', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush(ASSETS);
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('AAPL');
    expect(text).not.toContain('Ethereum');
  });

  it('shows the empty state with a call to action when nothing matches the section', async () => {
    fixture.componentRef.setInput('assetClass', 'Crypto');
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([ASSETS[0]]); // stock only
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('No crypto holdings yet');
    expect(fixture.nativeElement.querySelector('button')).toBeTruthy();
  });

  it('shows the error state and can retry', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush('boom', { status: 500, statusText: 'Server Error' });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain("Couldn't load your holdings");

    fixture.componentInstance.retry();
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush(ASSETS);
  });
});
