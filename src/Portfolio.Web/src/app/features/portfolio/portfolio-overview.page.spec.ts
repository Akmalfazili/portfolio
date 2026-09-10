import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideEchartsCore } from 'ngx-echarts';

import { PortfolioOverviewPage } from './portfolio-overview.page';
import { API_ROUTES } from '../../core/api/api-routes';
import {
  AnnualReturnsDto,
  PortfolioAllocationDto,
  PortfolioSummaryDto,
} from '../../core/api/models';
import { PRICES_HUB_CONNECTION_FACTORY, PriceStore } from '../../core/prices/price-store';
import { FakeHubConnection } from '../../core/prices/testing/fake-hub-connection';

const STOCK_SUMMARY: PortfolioSummaryDto = {
  assetClass: 'Stock',
  totalCostBasisUsd: 3864,
  totalMarketValueUsd: 0,
  totalUnrealizedPnlUsd: -3864,
  totalUnrealizedPnlPercent: -100,
  totalRealizedPnlUsd: 23,
  unpricedHoldingsCount: 1,
  totalDividendsTrailing12MonthUsd: 45.6,
  totalDividendsAllTimeUsd: 120.4,
  dividendsUncoveredCount: 0,
  holdings: [
    {
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
      dividendsTrailing12MonthUsd: 45.6,
      dividendsAllTimeUsd: 120.4,
      dividendCoverageStatus: 'Covered',
    },
  ],
};

/**
 * The D17 case, as the live API really sends it: AAPL has a real cost basis but
 * no price from any source, so its market value is 0 because the value is
 * UNKNOWN — not because the position is worthless.
 */
const STOCK_ALLOCATION: PortfolioAllocationDto = {
  assetClass: 'Stock',
  totalMarketValueUsd: 0,
  items: [
    {
      assetId: 1,
      symbol: 'AAPL',
      name: 'Apple Inc.',
      marketValueUsd: 0,
      percentageOfTotal: 0,
      hasPrice: false,
    },
  ],
  unpricedHoldingsCount: 1,
};

const ANNUAL_RETURNS: AnnualReturnsDto = {
  years: [{ year: 2026, timeWeightedReturnPercent: 1.7775 }],
};

describe('PortfolioOverviewPage', () => {
  let fixture: ComponentFixture<PortfolioOverviewPage>;
  let httpMock: HttpTestingController;
  let fakeHub: FakeHubConnection;

  beforeEach(() => {
    fakeHub = new FakeHubConnection();
    TestBed.configureTestingModule({
      imports: [PortfolioOverviewPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        provideEchartsCore({ echarts: () => import('echarts') }),
        { provide: PRICES_HUB_CONNECTION_FACTORY, useValue: () => fakeHub },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
    // Constructs PriceStore's hub connection eagerly.
    TestBed.inject(PriceStore);
    fixture = TestBed.createComponent(PortfolioOverviewPage);
    fixture.componentRef.setInput('assetClass', 'Stock');
  });

  afterEach(() => httpMock.verify());

  function flushInitial(
    summary = STOCK_SUMMARY,
    allocation = STOCK_ALLOCATION,
    annualReturns = ANNUAL_RETURNS,
  ) {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(summary);
    httpMock.expectOne(API_ROUTES.portfolioAllocation('Stock')).flush(allocation);
    httpMock.expectOne(API_ROUTES.stockAnnualReturns).flush(annualReturns);
  }

  it('shows the loading state before the requests resolve', () => {
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Loading your holdings');
    flushInitial();
  });

  it('requests summary, allocation and annual-returns with the AssetClass capitalised', async () => {
    flushInitial();
    await fixture.whenStable();
  });

  it('does NOT request annual-returns for the crypto section', () => {
    fixture.componentRef.setInput('assetClass', 'Crypto');
    fixture.detectChanges();
    httpMock
      .expectOne(API_ROUTES.portfolioSummary('Crypto'))
      .flush({ ...STOCK_SUMMARY, assetClass: 'Crypto' });
    httpMock
      .expectOne(API_ROUTES.portfolioAllocation('Crypto'))
      .flush({ ...STOCK_ALLOCATION, assetClass: 'Crypto' });
    httpMock.expectNone(API_ROUTES.stockAnnualReturns);
  });

  it('shows the empty state with a call to action when there are no holdings', async () => {
    flushInitial(
      { ...STOCK_SUMMARY, holdings: [] },
      { ...STOCK_ALLOCATION, items: [] },
      { years: [] },
    );
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('No stock holdings yet');
    expect(fixture.nativeElement.querySelector('button')).toBeTruthy();
  });

  it('shows the error state and can retry', async () => {
    fixture.detectChanges();
    httpMock
      .expectOne(API_ROUTES.portfolioSummary('Stock'))
      .flush('boom', { status: 500, statusText: 'Server Error' });
    httpMock.expectOne(API_ROUTES.portfolioAllocation('Stock')).flush(STOCK_ALLOCATION);
    httpMock.expectOne(API_ROUTES.stockAnnualReturns).flush(ANNUAL_RETURNS);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain("Couldn't load your holdings");

    fixture.componentInstance.retry();
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(STOCK_SUMMARY);
    httpMock.expectOne(API_ROUTES.portfolioAllocation('Stock')).flush(STOCK_ALLOCATION);
    httpMock.expectOne(API_ROUTES.stockAnnualReturns).flush(ANNUAL_RETURNS);
  });

  it('renders summary tiles, the holdings table, and the annual-return chart for stocks; AAPL shows "Awaiting price" not -100%', async () => {
    flushInitial();
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('AAPL');
    expect(text).toContain('Awaiting price');
    expect(text).toContain('Annual return');
  });

  it('D17 — caveats the totals rather than presenting them as complete when a holding is unpriced', async () => {
    // STOCK_SUMMARY is exactly the real-world case the tracker calls out:
    // AAPL has a real cost basis but no quote, so totalMarketValueUsd is 0
    // and the naive headline reads -100% — the tile caveat must make clear
    // that's a missing price, not a crash.
    flushInitial();
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('1 holding has no price yet');
    expect(text).toContain("totals below don't include its market value, not a loss");
  });

  it('shows no caveat once every holding is priced', async () => {
    flushInitial({ ...STOCK_SUMMARY, unpricedHoldingsCount: 0 });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).not.toContain('no price yet');
  });

  it('shows a dividend tile headlining the trailing-12-month total, with all-time as supporting detail, for stocks', async () => {
    flushInitial();
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Dividends (12m)');
    expect(text).toContain('$45.60');
    expect(text).toContain('All-time $120.40');
  });

  it('does not render a dividend tile for crypto', async () => {
    fixture.componentRef.setInput('assetClass', 'Crypto');
    fixture.detectChanges();
    httpMock
      .expectOne(API_ROUTES.portfolioSummary('Crypto'))
      .flush({ ...STOCK_SUMMARY, assetClass: 'Crypto' });
    httpMock
      .expectOne(API_ROUTES.portfolioAllocation('Crypto'))
      .flush({ ...STOCK_ALLOCATION, assetClass: 'Crypto' });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).not.toContain('Dividends (12m)');
  });

  it('applies the five-tile row modifier class for stocks (five tiles: cost basis, market value, unrealized, realized, dividends), never for crypto (four)', async () => {
    flushInitial();
    await fixture.whenStable();
    fixture.detectChanges();

    const stockTiles = fixture.nativeElement.querySelector('.overview__tiles');
    expect(stockTiles.classList.contains('overview__tiles--five')).toBe(true);
    expect(stockTiles.querySelectorAll('app-stat-tile').length).toBe(5);
  });

  it('does not apply the five-tile row modifier class for crypto', async () => {
    fixture.componentRef.setInput('assetClass', 'Crypto');
    fixture.detectChanges();
    httpMock
      .expectOne(API_ROUTES.portfolioSummary('Crypto'))
      .flush({ ...STOCK_SUMMARY, assetClass: 'Crypto' });
    httpMock
      .expectOne(API_ROUTES.portfolioAllocation('Crypto'))
      .flush({ ...STOCK_ALLOCATION, assetClass: 'Crypto' });
    await fixture.whenStable();
    fixture.detectChanges();

    const cryptoTiles = fixture.nativeElement.querySelector('.overview__tiles');
    expect(cryptoTiles.classList.contains('overview__tiles--five')).toBe(false);
    expect(cryptoTiles.querySelectorAll('app-stat-tile').length).toBe(4);
  });

  it('D17-shaped — caveats the dividend total, rather than presenting it as complete, when a stock is not Covered', async () => {
    flushInitial({ ...STOCK_SUMMARY, dividendsUncoveredCount: 2 });
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('2 stocks have incomplete dividend data');
    expect(text).toContain('may understate income, not a real shortfall');
  });

  it('shows no dividend caveat once every stock is Covered', async () => {
    flushInitial({ ...STOCK_SUMMARY, dividendsUncoveredCount: 0 });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).not.toContain('incomplete dividend data');
  });

  it('does not render the annual-return chart for crypto', async () => {
    fixture.componentRef.setInput('assetClass', 'Crypto');
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.portfolioSummary('Crypto')).flush({
      ...STOCK_SUMMARY,
      assetClass: 'Crypto',
      holdings: [{ ...STOCK_SUMMARY.holdings[0], assetClass: 'Crypto', symbol: 'ETH' }],
    });
    httpMock.expectOne(API_ROUTES.portfolioAllocation('Crypto')).flush({
      ...STOCK_ALLOCATION,
      assetClass: 'Crypto',
      items: [{ ...STOCK_ALLOCATION.items[0], symbol: 'ETH' }],
    });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).not.toContain('Annual return');
  });

  it('toggles the allocation pie between market value and cost basis without a new HTTP request', async () => {
    flushInitial();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.componentInstance.allocationMode()).toBe('market');
    fixture.componentInstance.setAllocationMode('cost');
    fixture.detectChanges();

    expect(fixture.componentInstance.allocationMode()).toBe('cost');
    expect(fixture.componentInstance.allocationSlices()[0].value).toBe(3864); // cost basis, not market value (0)
  });

  it('reloads summary/allocation/annual-returns after a refresh cycle completes, but not on the first status snapshot', async () => {
    flushInitial();
    await fixture.whenStable();
    fixture.detectChanges();

    fakeHub.emit('RefreshStatus', {
      lastRefreshedAt: '2026-08-01T10:00:00Z',
      nyseOpen: false,
      sgxOpen: false,
      nextScheduledRunAt: null,
      sources: [],
    });
    fixture.detectChanges();
    httpMock.expectNone(API_ROUTES.portfolioSummary('Stock'));

    fakeHub.emit('RefreshStatus', {
      lastRefreshedAt: '2026-08-01T10:05:00Z',
      nyseOpen: false,
      sgxOpen: false,
      nextScheduledRunAt: null,
      sources: [],
    });
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(STOCK_SUMMARY);
    httpMock.expectOne(API_ROUTES.portfolioAllocation('Stock')).flush(STOCK_ALLOCATION);
    httpMock.expectOne(API_ROUTES.stockAnnualReturns).flush(ANNUAL_RETURNS);
  });

  /**
   * THE regression test for the D40-shaped defect: `httpResource.reload()`
   * preserves the previous value while flipping `isLoading()` back to `true`
   * (verified against Angular's own `_resource-chunk.mjs`), so gating the
   * whole page's content on `isLoading()` alone unmounted and rebuilt 340-670
   * DOM nodes, both ECharts instances, and the holdings table's sort/page
   * state on every 2-minute crypto-cadence refresh cycle. This test fails
   * against the pre-fix template (which checks `isLoading()` before
   * `summary()`) because the reload puts `isLoading()` back to `true` and the
   * whole `@else if (summary(); as s)` branch — and "AAPL" with it —
   * disappears behind the loading spinner while the reload requests are
   * still in flight.
   */
  it('keeps holdings content mounted during a background reload — a reload must not blank a page that already has data', async () => {
    flushInitial();
    await fixture.whenStable();
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

    // The reload requests are now in flight — each resource's isLoading() is
    // true — but the previous value is still there, so the page must keep
    // showing it rather than unmounting the content behind a spinner.
    const midReloadText = fixture.nativeElement.textContent as string;
    expect(midReloadText).not.toContain('Loading your holdings');
    expect(midReloadText).toContain('AAPL');

    httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(STOCK_SUMMARY);
    httpMock.expectOne(API_ROUTES.portfolioAllocation('Stock')).flush(STOCK_ALLOCATION);
    httpMock.expectOne(API_ROUTES.stockAnnualReturns).flush(ANNUAL_RETURNS);
  });

  it('does not blank the page on a failed background reload — the toolbar refresh indicator surfaces refresh health, not a full-page error', async () => {
    flushInitial();
    await fixture.whenStable();
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

    httpMock
      .expectOne(API_ROUTES.portfolioSummary('Stock'))
      .flush('boom', { status: 500, statusText: 'Server Error' });
    httpMock.expectOne(API_ROUTES.portfolioAllocation('Stock')).flush(STOCK_ALLOCATION);
    httpMock.expectOne(API_ROUTES.stockAnnualReturns).flush(ANNUAL_RETURNS);
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).not.toContain("Couldn't load your holdings");
    expect(text).toContain('AAPL');
  });

  it("shows the allocation panel's own loading state without blanking the already-loaded summary tiles", async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.portfolioSummary('Stock')).flush(STOCK_SUMMARY);
    // Allocation request deliberately left unflushed — still in flight, so
    // `whenStable()` cannot be used here (it would hang on that same open
    // request); a microtask flush is enough for the summary response alone.
    await Promise.resolve();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).not.toContain('Loading your holdings');
    expect(text).toContain('AAPL');
    expect(text).toContain('Loading allocation');

    httpMock.expectOne(API_ROUTES.portfolioAllocation('Stock')).flush(STOCK_ALLOCATION);
    httpMock.expectOne(API_ROUTES.stockAnnualReturns).flush(ANNUAL_RETURNS);
  });
});
