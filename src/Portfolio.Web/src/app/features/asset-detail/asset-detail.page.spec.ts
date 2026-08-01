import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting, TestRequest } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideEchartsCore } from 'ngx-echarts';

import { AssetDetailPage } from './asset-detail.page';
import { API_ROUTES } from '../../core/api/api-routes';
import { AssetDto, AssetPerformanceDto, HoldingDto, PortfolioSummaryDto, TransactionDto } from '../../core/api/models';
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

const AAPL_HOLDING: HoldingDto = {
  assetId: 1,
  symbol: 'AAPL',
  name: 'Apple Inc.',
  assetClass: 'Stock',
  currency: 'USD',
  quantityHeld: 12,
  costBasisUsd: 3864,
  currentPriceNative: null,
  currentPriceUsd: null,
  priceAsOf: null,
  marketValueUsd: 0,
  unrealizedPnlUsd: -3864,
  unrealizedPnlPercent: -100,
  realizedPnlUsd: 23,
};

const STOCK_SUMMARY_WITH_HOLDING: PortfolioSummaryDto = {
  assetClass: 'Stock',
  totalCostBasisUsd: 3864,
  totalMarketValueUsd: 0,
  totalUnrealizedPnlUsd: -3864,
  totalUnrealizedPnlPercent: -100,
  totalRealizedPnlUsd: 23,
  holdings: [AAPL_HOLDING],
};

const EMPTY_STOCK_SUMMARY: PortfolioSummaryDto = {
  assetClass: 'Stock',
  totalCostBasisUsd: 0,
  totalMarketValueUsd: 0,
  totalUnrealizedPnlUsd: 0,
  totalUnrealizedPnlPercent: null,
  totalRealizedPnlUsd: 0,
  holdings: [],
};

const PERFORMANCE: AssetPerformanceDto = {
  assetId: 1,
  symbol: 'AAPL',
  name: 'Apple Inc.',
  currency: 'USD',
  points: [
    { date: '2026-07-20', costBasisUsd: 3201, marketValueUsd: 3265.9 },
    { date: '2026-07-24', costBasisUsd: 3864, marketValueUsd: 3996.24 },
  ],
};

const TRANSACTIONS: TransactionDto[] = [
  {
    id: 16,
    assetId: 1,
    assetSymbol: 'AAPL',
    assetClass: 'Stock',
    type: 'Buy',
    tradeDate: '2026-07-20',
    quantity: 10,
    pricePerUnit: 320,
    fees: 1,
    currency: 'USD',
    notes: 'probe',
  },
];

/**
 * `performanceResource`/`transactionsResource` derive their request URL from
 * `asset()`, which itself only resolves once `assetsResource` (a SEPARATE
 * httpResource) has flushed — i.e. these are resources-of-a-resource. The
 * dependent HTTP call is dispatched asynchronously by an internal effect,
 * not synchronously when the first response is flushed, and `whenStable()`
 * cannot be used to wait for it here: it tracks the newly-dispatched request
 * as a pending task and hangs forever until that same request is flushed —
 * which we can't do before we can see it. Polling `httpMock.match()` a few
 * ticks is the reliable way to observe it appear.
 */
async function waitForRequest(httpMock: HttpTestingController, url: string, tries = 30): Promise<TestRequest> {
  for (let i = 0; i < tries; i++) {
    const reqs = httpMock.match(url);
    if (reqs.length > 0) {
      return reqs[0];
    }
    await Promise.resolve();
    TestBed.tick();
  }
  throw new Error(`Request to ${url} never appeared after ${tries} tries`);
}

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
        provideEchartsCore({ echarts: () => import('echarts') }),
        { provide: PRICES_HUB_CONNECTION_FACTORY, useValue: () => new FakeHubConnection() },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(AssetDetailPage);
    fixture.componentRef.setInput('assetClass', 'Stock');
    fixture.componentRef.setInput('symbol', 'AAPL');
  });

  afterEach(() => httpMock.verify());

  /** Flushes the two requests that fire as soon as `asset()` resolves to a
   *  found Stock asset — every scenario below hits this once AAPL is found,
   *  regardless of whether a holding exists. */
  async function flushStockAssetRequests(
    performance: AssetPerformanceDto = PERFORMANCE,
    transactions: TransactionDto[] = TRANSACTIONS,
  ) {
    (await waitForRequest(httpMock, API_ROUTES.assetPerformance(1))).flush(performance);
    (await waitForRequest(httpMock, API_ROUTES.transactionsByAsset(1))).flush(transactions);
  }

  it('shows a not-found state for a symbol that belongs to the other asset class', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
    httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(EMPTY_STOCK_SUMMARY);
    await flushStockAssetRequests();
    fixture.detectChanges();

    fixture.componentRef.setInput('assetClass', 'Crypto');
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.portfolioSummary('Crypto')).flush({ ...EMPTY_STOCK_SUMMARY, assetClass: 'Crypto' });
    await Promise.resolve();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('was not found');
  });

  it('shows the error state on a failed lookup', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush('boom', { status: 500, statusText: 'Server Error' });
    httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(EMPTY_STOCK_SUMMARY);
    await Promise.resolve();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain("Couldn't load this asset");
  });

  it('shows a "no transactions yet" state when the asset exists but has no holding', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
    httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(EMPTY_STOCK_SUMMARY);
    await flushStockAssetRequests(PERFORMANCE, []);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('No transactions yet for this asset');
  });

  it('falls back to the last persisted native price before any live push arrives, never "Waiting" if a snapshot price exists', async () => {
    const withPrice: HoldingDto = { ...AAPL_HOLDING, currentPriceNative: 333.02, currentPriceUsd: 333.02 };
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
    httpMock
      .expectOne(API_ROUTES.portfolioSummary('Stock'))
      .flush({ ...STOCK_SUMMARY_WITH_HOLDING, holdings: [withPrice] });
    await flushStockAssetRequests();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('$333.02');
    expect(fixture.nativeElement.textContent).not.toContain('Waiting for a live quote');
  });

  it('shows "Waiting for a live quote" when neither a push nor a persisted quote exists', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
    httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(STOCK_SUMMARY_WITH_HOLDING);
    await flushStockAssetRequests();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Waiting for a live quote');
  });

  it('renders the gain/loss card, the cost-vs-market chart (stocks only) and the transactions list', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
    httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(STOCK_SUMMARY_WITH_HOLDING);
    await flushStockAssetRequests();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Gain / loss');
    expect(text).toContain('Cost vs market value');
    expect(text).toContain('Transactions');
    expect(text).toContain('2026-07-20');
  });

  it('never requests performance or renders the line chart for crypto', async () => {
    fixture.componentRef.setInput('assetClass', 'Crypto');
    fixture.componentRef.setInput('symbol', 'ETH');
    const eth: AssetDto = { ...AAPL, id: 4, symbol: 'ETH', name: 'Ethereum', assetClass: 'Crypto' };
    const ethHolding: HoldingDto = { ...AAPL_HOLDING, assetId: 4, symbol: 'ETH', assetClass: 'Crypto' };

    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([eth]);
    httpMock
      .expectOne(API_ROUTES.portfolioSummary('Crypto'))
      .flush({ ...EMPTY_STOCK_SUMMARY, assetClass: 'Crypto', holdings: [ethHolding] });
    (await waitForRequest(httpMock, API_ROUTES.transactionsByAsset(4))).flush([]);
    httpMock.expectNone(API_ROUTES.assetPerformance(4));
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).not.toContain('Cost vs market value');
  });
});
