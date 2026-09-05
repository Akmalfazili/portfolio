import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting, TestRequest } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideEchartsCore } from 'ngx-echarts';

import { AssetDetailPage } from './asset-detail.page';
import { API_ROUTES } from '../../core/api/api-routes';
import {
  AssetDividendHistoryDto,
  AssetDto,
  AssetPerformanceDto,
  HoldingDto,
  PortfolioSummaryDto,
  TransactionDto,
} from '../../core/api/models';
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
  fiscalYearEndMonth: null,
  fiscalYearEndDay: null,
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
  dividendsTrailing12MonthUsd: 4.5,
  dividendsAllTimeUsd: 12.75,
  dividendCoverageStatus: 'Covered',
};

const STOCK_SUMMARY_WITH_HOLDING: PortfolioSummaryDto = {
  assetClass: 'Stock',
  totalCostBasisUsd: 3864,
  totalMarketValueUsd: 0,
  totalUnrealizedPnlUsd: -3864,
  totalUnrealizedPnlPercent: -100,
  totalRealizedPnlUsd: 23,
  unpricedHoldingsCount: 1,
  totalDividendsTrailing12MonthUsd: 4.5,
  totalDividendsAllTimeUsd: 12.75,
  dividendsUncoveredCount: 0,
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
  totalDividendsTrailing12MonthUsd: null,
  totalDividendsAllTimeUsd: null,
  dividendsUncoveredCount: 0,
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

const DIVIDEND_HISTORY: AssetDividendHistoryDto = {
  assetId: 1,
  symbol: 'AAPL',
  name: 'Apple Inc.',
  currency: 'USD',
  trailing12MonthIncomeUsd: 4.5,
  allTimeIncomeUsd: 12.75,
  coverageStatus: 'Covered',
  payments: [
    { exDate: '2026-05-09', amountPerShareNative: 0.26, currency: 'USD', unitsHeldAtExDate: 10, incomeUsd: 2.6 },
    { exDate: '2026-02-09', amountPerShareNative: 0.24, currency: 'USD', unitsHeldAtExDate: 8, incomeUsd: 1.92 },
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

  /** Flushes the three requests that fire as soon as `asset()` resolves to a
   *  found Stock asset — every scenario below hits this once AAPL is found,
   *  regardless of whether a holding exists. */
  async function flushStockAssetRequests(
    performance: AssetPerformanceDto = PERFORMANCE,
    transactions: TransactionDto[] = TRANSACTIONS,
    dividends: AssetDividendHistoryDto = DIVIDEND_HISTORY,
  ) {
    (await waitForRequest(httpMock, API_ROUTES.assetPerformance(1))).flush(performance);
    (await waitForRequest(httpMock, API_ROUTES.transactionsByAsset(1))).flush(transactions);
    (await waitForRequest(httpMock, API_ROUTES.assetDividends(1))).flush(dividends);
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

  /**
   * THE regression test for the D40-shaped defect: `httpResource.reload()`
   * preserves the previous value while flipping `isLoading()` back to `true`
   * (Angular's own `_resource-chunk.mjs`), so gating the whole page's
   * content on `isLoading()` alone unmounted and rebuilt the page every time
   * `PriceStore` completed a refresh cycle. This test fails against the
   * pre-fix template (which checks `isLoading()` before `asset()`) because
   * the reload puts `isLoading()` back to `true` and the whole
   * `@else if (asset(); as found)` branch — and "AAPL" with it — disappears
   * behind "Loading asset…" while the reload requests are still in flight.
   */
  it('keeps the page mounted during a background reload triggered by a refresh cycle — a reload must not blank an asset that already has data', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
    httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(STOCK_SUMMARY_WITH_HOLDING);
    await flushStockAssetRequests();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('AAPL');

    fakeHub.emit('RefreshStatus', {
      lastRefreshedAt: '2026-08-01T10:00:00Z',
      nyseOpen: false,
      sgxOpen: false,
      nextScheduledRunAt: null,
      sources: [],
    });
    fixture.detectChanges();
    fakeHub.emit('RefreshStatus', {
      lastRefreshedAt: '2026-08-01T10:05:00Z',
      nyseOpen: false,
      sgxOpen: false,
      nextScheduledRunAt: null,
      sources: [],
    });
    fixture.detectChanges();

    // summaryResource and performanceResource are both reloading now (in
    // flight, isLoading() true on each) but their previous values are still
    // there, so the page must keep showing them rather than unmounting.
    const midReloadText = fixture.nativeElement.textContent as string;
    expect(midReloadText).not.toContain('Loading asset');
    expect(midReloadText).toContain('AAPL');

    httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(STOCK_SUMMARY_WITH_HOLDING);
    (await waitForRequest(httpMock, API_ROUTES.assetPerformance(1))).flush(PERFORMANCE);
  });

  it('does not blank the page on a failed background reload — the toolbar refresh indicator surfaces refresh health, not a full-page error', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
    httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(STOCK_SUMMARY_WITH_HOLDING);
    await flushStockAssetRequests();
    fixture.detectChanges();

    fakeHub.emit('RefreshStatus', {
      lastRefreshedAt: '2026-08-01T10:00:00Z',
      nyseOpen: false,
      sgxOpen: false,
      nextScheduledRunAt: null,
      sources: [],
    });
    fixture.detectChanges();
    fakeHub.emit('RefreshStatus', {
      lastRefreshedAt: '2026-08-01T10:05:00Z',
      nyseOpen: false,
      sgxOpen: false,
      nextScheduledRunAt: null,
      sources: [],
    });
    fixture.detectChanges();

    httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush('boom', { status: 500, statusText: 'Server Error' });
    (await waitForRequest(httpMock, API_ROUTES.assetPerformance(1))).flush(PERFORMANCE);
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).not.toContain("Couldn't load this asset");
    expect(text).toContain('AAPL');
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
    // The dividends endpoint 400s for a crypto asset id — it must never even
    // be called, the same "undefined httpResource url" treatment as performance.
    httpMock.expectNone(API_ROUTES.assetDividends(4));
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).not.toContain('Cost vs market value');
    expect(fixture.nativeElement.textContent).not.toContain('Dividends');
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
    (await waitForRequest(httpMock, API_ROUTES.assetDividends(9))).flush({
      ...DIVIDEND_HISTORY,
      assetId: 9,
      symbol: 'Z74',
      currency: 'SGD',
      payments: [{ exDate: '2026-05-09', amountPerShareNative: 0.103, currency: 'SGD', unitsHeldAtExDate: 100, incomeUsd: 7.65 }],
    });
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Avg cost (USD)');
  });

  it("renders a dividend payment's amount-per-share in the asset's own native currency, not USD, for a non-USD stock", async () => {
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
    (await waitForRequest(httpMock, API_ROUTES.assetDividends(9))).flush({
      ...DIVIDEND_HISTORY,
      assetId: 9,
      symbol: 'Z74',
      currency: 'SGD',
      payments: [{ exDate: '2026-05-09', amountPerShareNative: 0.103, currency: 'SGD', unitsHeldAtExDate: 100, incomeUsd: 7.65 }],
    });
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    // Rendered in SGD (the asset's own currency), never USD-formatted —
    // beside the already-converted USD income figure. Intl.NumberFormat
    // separates the ISO code from the amount with a non-breaking space
    // (U+00A0), same as money.pipe.spec.ts's own SGD case.
    expect(/SGD\s*0\.10/.test(text)).toBe(true);
    expect(text).toContain('$7.65');
  });

  describe('the dividends panel', () => {
    it('shows the trailing-12-month/all-time totals and the payment history table for a Covered stock', async () => {
      fixture.detectChanges();
      httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
      httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(STOCK_SUMMARY_WITH_HOLDING);
      await flushStockAssetRequests();
      fixture.detectChanges();

      const text = fixture.nativeElement.textContent as string;
      expect(text).toContain('Dividends');
      expect(text).toContain('Trailing 12 months');
      expect(text).toContain('$4.50');
      expect(text).toContain('All-time');
      expect(text).toContain('$12.75');
      expect(text).toContain('2026-05-09');
      expect(text).toContain('2026-02-09');
      // The required estimate caveat.
      expect(text).toContain('Estimated from units held on each ex-date');
      expect(text).toContain('excludes withholding tax, DRIP and scrip handling');
    });

    it('reads differently for NotYetFetched than for a Covered-but-no-payments (genuinely non-dividend-paying) stock', async () => {
      fixture.detectChanges();
      httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
      httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(STOCK_SUMMARY_WITH_HOLDING);
      await flushStockAssetRequests(PERFORMANCE, TRANSACTIONS, { ...DIVIDEND_HISTORY, coverageStatus: 'NotYetFetched', trailing12MonthIncomeUsd: null, allTimeIncomeUsd: null, payments: [] });
      fixture.detectChanges();

      const text = fixture.nativeElement.textContent as string;
      expect(text).toContain('Dividend data not yet fetched');
      expect(text).not.toContain('No dividends paid');
    });

    it('shows an honest "no dividends paid" state for a Covered stock with an empty payment history — not the same as NotYetFetched', async () => {
      fixture.detectChanges();
      httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
      httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(STOCK_SUMMARY_WITH_HOLDING);
      await flushStockAssetRequests(PERFORMANCE, TRANSACTIONS, {
        ...DIVIDEND_HISTORY,
        coverageStatus: 'Covered',
        trailing12MonthIncomeUsd: 0,
        allTimeIncomeUsd: 0,
        payments: [],
      });
      fixture.detectChanges();

      const text = fixture.nativeElement.textContent as string;
      expect(text).toContain('No dividends paid');
      expect(text).not.toContain('Dividend data not yet fetched');
      // The headline totals still show a real, honest zero.
      expect(text).toContain('$0.00');
    });

    it('shows a distinct, retryable failure state for FetchFailed, never the same as "no dividends"', async () => {
      fixture.detectChanges();
      httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
      httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(STOCK_SUMMARY_WITH_HOLDING);
      await flushStockAssetRequests(PERFORMANCE, TRANSACTIONS, {
        ...DIVIDEND_HISTORY,
        coverageStatus: 'FetchFailed',
        trailing12MonthIncomeUsd: null,
        allTimeIncomeUsd: null,
        payments: [],
      });
      fixture.detectChanges();

      const text = fixture.nativeElement.textContent as string;
      expect(text).toContain("Couldn't load dividend data");
      expect(fixture.nativeElement.querySelector('button')).toBeTruthy();

      // Retrying re-requests the same endpoint.
      fixture.componentInstance.retryDividends();
      fixture.detectChanges();
      (await waitForRequest(httpMock, API_ROUTES.assetDividends(1))).flush(DIVIDEND_HISTORY);
    });

    it('keeps the dividends panel mounted during a manual retry reload — a reload must not blank a payment table already on screen', async () => {
      fixture.detectChanges();
      httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
      httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(STOCK_SUMMARY_WITH_HOLDING);
      await flushStockAssetRequests();
      fixture.detectChanges();

      expect(fixture.nativeElement.textContent).toContain('2026-05-09');

      fixture.componentInstance.retryDividends();
      fixture.detectChanges();

      // The reload is in flight (dividendsResource.isLoading() is true) but
      // the previous payment history is still there, so the panel must keep
      // showing it rather than swapping in "Loading dividend history…".
      const midReloadText = fixture.nativeElement.textContent as string;
      expect(midReloadText).not.toContain('Loading dividend history');
      expect(midReloadText).toContain('2026-05-09');

      (await waitForRequest(httpMock, API_ROUTES.assetDividends(1))).flush(DIVIDEND_HISTORY);
    });

    it('never renders the dividends panel for crypto', async () => {
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
      httpMock.expectNone(API_ROUTES.assetDividends(4));
      fixture.detectChanges();

      expect(fixture.nativeElement.textContent).not.toContain('Dividends');
    });

    it('sorts by ex-date descending by default, and reaches the <th> with mat-sort-header', async () => {
      fixture.detectChanges();
      httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
      httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(STOCK_SUMMARY_WITH_HOLDING);
      await flushStockAssetRequests();
      fixture.detectChanges();

      const exDateHeader = fixture.nativeElement.querySelector('th[mat-sort-header="exDate"]') as HTMLElement;
      expect(exDateHeader.getAttribute('aria-sort')).toBe('descending');
      expect(exDateHeader.getAttribute('scope')).toBe('col');

      fixture.componentInstance.onDividendSortChange({ active: 'exDate', direction: 'asc' });
      fixture.detectChanges();
      const dates = Array.from(
        fixture.nativeElement.querySelectorAll(
          '.detail__dividends-table tbody tr td:first-child',
        ) as NodeListOf<HTMLElement>,
      ).map((el) => el.textContent?.trim());
      expect(dates).toEqual(['2026-02-09', '2026-05-09']);
    });
  });

  describe('the transactions panel — sorting, pagination and virtualization', () => {
    const MANY_TRANSACTIONS: TransactionDto[] = Array.from({ length: 30 }, (_, i) => ({
      ...TRANSACTIONS[0],
      id: i + 1,
      tradeDate: `2026-07-${String(i + 1).padStart(2, '0')}`,
    }));

    it('defaults to trade date descending', async () => {
      fixture.detectChanges();
      httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
      httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(STOCK_SUMMARY_WITH_HOLDING);
      await flushStockAssetRequests(PERFORMANCE, [
        { ...TRANSACTIONS[0], id: 1, tradeDate: '2026-07-20' },
        { ...TRANSACTIONS[0], id: 2, tradeDate: '2026-08-01' },
      ]);
      fixture.detectChanges();

      const dates = Array.from(
        fixture.nativeElement.querySelectorAll(
          'table.detail__transactions tbody td:first-child',
        ) as NodeListOf<HTMLElement>,
      ).map((el) => el.textContent?.trim());
      expect(dates).toEqual(['2026-08-01', '2026-07-20']);
    });

    it('reaches the <th> with mat-sort-header, emitting aria-sort', async () => {
      fixture.detectChanges();
      httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
      httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(STOCK_SUMMARY_WITH_HOLDING);
      await flushStockAssetRequests();
      fixture.detectChanges();

      const dateHeader = fixture.nativeElement.querySelector('th[mat-sort-header="date"]') as HTMLElement;
      expect(dateHeader.getAttribute('aria-sort')).toBe('descending');
      expect(dateHeader.getAttribute('scope')).toBe('col');
    });

    it('hides the pager when the row count fits the smallest page size', async () => {
      fixture.detectChanges();
      httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
      httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(STOCK_SUMMARY_WITH_HOLDING);
      await flushStockAssetRequests();
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('app-table-pager .table-pager')).toBeNull();
    });

    it(
      'paginates once the row count exceeds the smallest page size',
      async () => {
        fixture.detectChanges();
        httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
        httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(STOCK_SUMMARY_WITH_HOLDING);
        await flushStockAssetRequests(PERFORMANCE, MANY_TRANSACTIONS);
        fixture.detectChanges();

        expect(fixture.nativeElement.querySelector('app-table-pager .table-pager')).not.toBeNull();
        expect(fixture.nativeElement.querySelectorAll('table.detail__transactions tbody tr').length).toBe(25);
      },
      // 30 rows plus flushStockAssetRequests's own multi-tick polling is
      // genuinely more work than this file's other cases — see the identical
      // note in holdings-table.spec.ts.
      15000,
    );

    it(
      'switches to the virtualized CSS-grid view when "All" is selected',
      async () => {
        fixture.detectChanges();
        httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
        httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(STOCK_SUMMARY_WITH_HOLDING);
        await flushStockAssetRequests(PERFORMANCE, MANY_TRANSACTIONS);
        fixture.detectChanges();

        expect(fixture.nativeElement.querySelector('table.detail__transactions')).not.toBeNull();

        fixture.componentInstance.tableState.setPageSize(Infinity);
        fixture.detectChanges();

        expect(fixture.nativeElement.querySelector('table.detail__transactions')).toBeNull();
        const grid = fixture.nativeElement.querySelector('.detail__grid') as HTMLElement;
        expect(grid).not.toBeNull();
        expect(grid.getAttribute('role')).toBe('table');
        expect(fixture.nativeElement.querySelector('cdk-virtual-scroll-viewport')).not.toBeNull();
      },
      15000,
    );

    it(
      'keeps the ARIA table owning its rows, and counts them, while virtualized',
      async () => {
        fixture.detectChanges();
        httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
        httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(STOCK_SUMMARY_WITH_HOLDING);
        await flushStockAssetRequests(PERFORMANCE, MANY_TRANSACTIONS);
        fixture.detectChanges();

        fixture.componentInstance.tableState.setPageSize(Infinity);
        fixture.detectChanges();
        await fixture.whenStable();
        fixture.detectChanges();

        const grid = fixture.nativeElement.querySelector('.detail__grid') as HTMLElement;
        // Header row included in the count.
        expect(grid.getAttribute('aria-rowcount')).toBe(String(MANY_TRANSACTIONS.length + 1));
        expect(
          fixture.nativeElement.querySelector('.detail__grid-row--head')!.getAttribute('aria-rowindex'),
        ).toBe('1');

        // Without these two, role=table does not own its role=row children —
        // CDK's viewport and content wrapper sit between them. See
        // shared/table/virtual-rowgroup.ts.
        const viewport = fixture.nativeElement.querySelector('cdk-virtual-scroll-viewport') as HTMLElement;
        expect(viewport.getAttribute('role')).toBe('presentation');
        expect(
          viewport.querySelector('.cdk-virtual-scroll-content-wrapper')!.getAttribute('role'),
        ).toBe('rowgroup');
      },
      15000,
    );
  });
});
