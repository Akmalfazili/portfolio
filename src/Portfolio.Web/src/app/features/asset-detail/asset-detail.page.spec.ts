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
  createdAt: '2026-07-26T00:00:00+00:00',
  hasEverBeenPriced: true,
  providerHasEverSucceeded: true,
};

const AAPL_HOLDING: HoldingDto = {
  assetId: 1,
  symbol: 'AAPL',
  name: 'Apple Inc.',
  assetClass: 'Stock',
  currency: 'USD',
  quantityHeld: 12,
  costBasisUsd: 3864,
  averageCostUsd: 322,
  currentPriceNative: null,
  currentPriceUsd: null,
  priceAsOf: null,
  priceSource: null,
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
  unpricedHoldingsCount: 1,
  holdings: [AAPL_HOLDING],
};

const EMPTY_STOCK_SUMMARY: PortfolioSummaryDto = {
  assetClass: 'Stock',
  totalCostBasisUsd: 0,
  totalMarketValueUsd: 0,
  totalUnrealizedPnlUsd: 0,
  totalUnrealizedPnlPercent: null,
  totalRealizedPnlUsd: 0,
  unpricedHoldingsCount: 0,
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
  let fakeHub: FakeHubConnection;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [AssetDetailPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        provideEchartsCore({ echarts: () => import('echarts') }),
        {
          provide: PRICES_HUB_CONNECTION_FACTORY,
          // Captured (rather than discarded) so a test can push a QuoteUpdated
          // frame through the real PriceStore, which is the only way to exercise
          // the pushed-quote branch of the price header — see the D4 test below.
          useValue: () => (fakeHub = new FakeHubConnection()),
        },
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
    const withPrice: HoldingDto = {
      ...AAPL_HOLDING,
      currentPriceNative: 333.02,
      currentPriceUsd: 333.02,
      priceAsOf: '2026-08-07T11:15:00+00:00',
      priceSource: 'Live',
    };
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
    httpMock
      .expectOne(API_ROUTES.portfolioSummary('Stock'))
      .flush({ ...STOCK_SUMMARY_WITH_HOLDING, unpricedHoldingsCount: 0, holdings: [withPrice] });
    await flushStockAssetRequests();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('$333.02');
    expect(fixture.nativeElement.textContent).not.toContain('Waiting for a live quote');
    expect(fixture.nativeElement.textContent).not.toContain('Close ·');
  });

  it('labels a Close-sourced fallback price with its own close date, distinct from a live price', async () => {
    const closeSourced: HoldingDto = {
      ...AAPL_HOLDING,
      currentPriceNative: 333.019989,
      currentPriceUsd: 333.019989,
      priceAsOf: '2026-07-24T00:00:00+00:00',
      priceSource: 'Close',
    };
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
    httpMock
      .expectOne(API_ROUTES.portfolioSummary('Stock'))
      .flush({ ...STOCK_SUMMARY_WITH_HOLDING, unpricedHoldingsCount: 0, holdings: [closeSourced] });
    await flushStockAssetRequests();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('$333.02');
    expect(text).toContain('Close');
    expect(text).toContain('Fri 24 Jul');
  });

  /**
   * D4. The original `isCloseSourced` read `quote() === undefined && ...`, on the
   * stated reasoning that a genuine SignalR push is always live. It is not: on an
   * unmodelled SGX lunar holiday the refresh service polls anyway, Yahoo answers
   * with the previous session's close, and that gets broadcast like any other
   * tick. A push therefore *overrode* the honest label — leaving the most
   * prominent number on the page as the one place the stale price still read as
   * live, on exactly the days D4 is about.
   *
   * This drives the real `PriceStore` with a real hub frame rather than stubbing
   * the computed, because the defect lived in how the two sources were combined.
   */
  it('labels a PUSHED quote as a close when the backend classifies it as one (D4)', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
    httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(STOCK_SUMMARY_WITH_HOLDING);
    await flushStockAssetRequests();
    fixture.detectChanges();

    // What the hub actually sends on a lunar holiday: a successful fetch whose
    // payload is the previous session's close, stamped with that session's time.
    fakeHub.emit('QuoteUpdated', {
      assetId: 1,
      symbol: 'AAPL',
      price: 333.019989,
      currency: 'USD',
      asOf: '2026-07-24T08:00:00+00:00',
      source: 'Close',
    });
    await Promise.resolve();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('$333.02');
    // The caption must carry the pushed quote's OWN date, not "now".
    expect(text).toContain('Close');
    expect(text).toContain('Fri 24 Jul');
  });

  it('still treats a pushed Live quote as live', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
    httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(STOCK_SUMMARY_WITH_HOLDING);
    await flushStockAssetRequests();
    fixture.detectChanges();

    fakeHub.emit('QuoteUpdated', {
      assetId: 1,
      symbol: 'AAPL',
      price: 340.5,
      currency: 'USD',
      asOf: '2026-08-07T15:30:00+00:00',
      source: 'Live',
    });
    await Promise.resolve();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('$340.50');
    expect(text).not.toContain('Close ·');
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

  it('shows average cost beside the hero price for a holding, with no redundant unit label on a USD-native asset', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
    httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(STOCK_SUMMARY_WITH_HOLDING);
    await flushStockAssetRequests();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Avg cost');
    expect(text).toContain('$322.00');
    expect(text).not.toContain('Avg cost (USD)');
  });

  it('does not render an average cost line when the asset has no holding', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
    httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(EMPTY_STOCK_SUMMARY);
    await flushStockAssetRequests(PERFORMANCE, []);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).not.toContain('Avg cost');
  });

  it('shows "Shares held" with the quantity beside the hero price for a stock holding', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
    httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(STOCK_SUMMARY_WITH_HOLDING);
    await flushStockAssetRequests();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Shares held');
    expect(text).not.toContain('Units held');

    const value = fixture.nativeElement.querySelector('.detail__units-held-value') as HTMLElement;
    expect(value.textContent?.trim()).toBe('12');
  });

  it('labels the unit count "Units held" (not "Shares held") for a crypto holding, and renders the fractional quantity in full', async () => {
    fixture.componentRef.setInput('assetClass', 'Crypto');
    fixture.componentRef.setInput('symbol', 'ETH');
    const eth: AssetDto = { ...AAPL, id: 4, symbol: 'ETH', name: 'Ethereum', assetClass: 'Crypto' };
    const ethHolding: HoldingDto = {
      ...AAPL_HOLDING,
      assetId: 4,
      symbol: 'ETH',
      assetClass: 'Crypto',
      quantityHeld: 0.1234567891,
    };

    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([eth]);
    httpMock
      .expectOne(API_ROUTES.portfolioSummary('Crypto'))
      .flush({ ...EMPTY_STOCK_SUMMARY, assetClass: 'Crypto', holdings: [ethHolding] });
    (await waitForRequest(httpMock, API_ROUTES.transactionsByAsset(4))).flush([]);
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Units held');
    expect(text).not.toContain('Shares held');

    const value = fixture.nativeElement.querySelector('.detail__units-held-value') as HTMLElement;
    // Full 10 dp precision, never rounded to 2 dp like a money amount.
    expect(value.textContent?.trim()).toBe('0.1234567891');
  });

  it('shows a real "0", not an em-dash or a blank, for units held on a fully sold-down holding', async () => {
    const soldDown: HoldingDto = {
      ...AAPL_HOLDING,
      quantityHeld: 0,
      costBasisUsd: 0,
      averageCostUsd: null,
      currentPriceNative: 175.5,
      currentPriceUsd: 175.5,
      priceAsOf: '2026-08-07T11:15:00+00:00',
      priceSource: 'Live',
    };
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
    httpMock
      .expectOne(API_ROUTES.portfolioSummary('Stock'))
      .flush({ ...STOCK_SUMMARY_WITH_HOLDING, unpricedHoldingsCount: 0, holdings: [soldDown] });
    await flushStockAssetRequests();
    fixture.detectChanges();

    const value = fixture.nativeElement.querySelector('.detail__units-held-value') as HTMLElement;
    expect(value.textContent?.trim()).toBe('0');
  });

  it('does not render a units-held line when the asset has no holding', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
    httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(EMPTY_STOCK_SUMMARY);
    await flushStockAssetRequests(PERFORMANCE, []);
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).not.toContain('Shares held');
    expect(text).not.toContain('Units held');
  });

  it('renders an em-dash, not $0.00, for average cost on a fully sold-down holding', async () => {
    const soldDown: HoldingDto = {
      ...AAPL_HOLDING,
      quantityHeld: 0,
      costBasisUsd: 0,
      averageCostUsd: null,
      currentPriceNative: 175.5,
      currentPriceUsd: 175.5,
      priceAsOf: '2026-08-07T11:15:00+00:00',
      priceSource: 'Live',
    };
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
    httpMock
      .expectOne(API_ROUTES.portfolioSummary('Stock'))
      .flush({ ...STOCK_SUMMARY_WITH_HOLDING, unpricedHoldingsCount: 0, holdings: [soldDown] });
    await flushStockAssetRequests();
    fixture.detectChanges();

    const avgCostValue = fixture.nativeElement.querySelector('.detail__avg-cost-value') as HTMLElement;
    expect(avgCostValue.textContent?.trim()).toBe('—');
  });

  /**
   * `averageCostUsd` is always USD, but the hero price above it is in the
   * asset's own native currency (Z74 is SGD) — the same silent unit-mismatch
   * risk as D4/D20 if the two dollar-shaped figures sit side by side
   * unlabelled. The label must spell out "(USD)" for a non-USD asset.
   */
  it('labels average cost with "(USD)" when the asset is not USD-native', async () => {
    const z74: AssetDto = {
      ...AAPL,
      id: 9,
      symbol: 'Z74',
      name: 'Singtel',
      exchange: 'XSES',
      currency: 'SGD',
      quoteProviderKind: 'Yahoo',
    };
    const z74Holding: HoldingDto = {
      ...AAPL_HOLDING,
      assetId: 9,
      symbol: 'Z74',
      currency: 'SGD',
      currentPriceNative: 2.5,
      currentPriceUsd: 1.85,
      priceAsOf: '2026-08-07T11:15:00+00:00',
      priceSource: 'Live',
    };

    fixture.componentRef.setInput('symbol', 'Z74');
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([z74]);
    httpMock
      .expectOne(API_ROUTES.portfolioSummary('Stock'))
      .flush({ ...STOCK_SUMMARY_WITH_HOLDING, holdings: [z74Holding] });
    (await waitForRequest(httpMock, API_ROUTES.assetPerformance(9))).flush({ ...PERFORMANCE, assetId: 9 });
    (await waitForRequest(httpMock, API_ROUTES.transactionsByAsset(9))).flush([]);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Avg cost (USD)');
  });
});
