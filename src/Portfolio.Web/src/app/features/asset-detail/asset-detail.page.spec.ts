import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';

import { AssetDetailPage } from './asset-detail.page';
import { API_ROUTES } from '../../core/api/api-routes';
import { AssetDto } from '../../core/api/models';
import { PRICES_HUB_CONNECTION_FACTORY } from '../../core/prices/price-store';
import { FakeHubConnection } from '../../core/prices/testing/fake-hub-connection';

const AAPL: AssetDto = {
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
};

describe('AssetDetailPage', () => {
  let fixture: ComponentFixture<AssetDetailPage>;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [AssetDetailPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: PRICES_HUB_CONNECTION_FACTORY, useValue: () => new FakeHubConnection() },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(AssetDetailPage);
    fixture.componentRef.setInput('assetClass', 'Stock');
    fixture.componentRef.setInput('symbol', 'AAPL');
  });

  afterEach(() => httpMock.verify());

  it('renders the found asset with a "waiting for a live quote" placeholder before any push arrives', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('AAPL');
    expect(text).toContain('Apple Inc.');
    expect(text).toContain('Waiting for a live quote');
  });

  it('shows a not-found state for a symbol that belongs to the other asset class', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
    await fixture.whenStable();
    fixture.componentRef.setInput('assetClass', 'Crypto');
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('was not found');
  });

  it('shows the error state on a failed lookup', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush('boom', { status: 500, statusText: 'Server Error' });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain("Couldn't load this asset");
  });
});
