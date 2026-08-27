import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { HoldingsTable } from './holdings-table';
import { HoldingDto } from '../../../core/api/models';

const AAPL_NO_PRICE: HoldingDto = {
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

const ANVL_SUBCENT: HoldingDto = {
  assetId: 6,
  symbol: 'ANVL',
  name: 'Anvil',
  assetClass: 'Crypto',
  currency: 'USD',
  quantityHeld: 1_000_000,
  costBasisUsd: 400.1,
  averageCostUsd: 0.0004001,
  currentPriceNative: 0.00050448,
  currentPriceUsd: 0.00050448,
  priceAsOf: '2026-08-01T10:48:50+00:00',
  priceSource: 'Live',
  marketValueUsd: 504.48,
  unrealizedPnlUsd: 104.38,
  unrealizedPnlPercent: 26.0885,
  realizedPnlUsd: 0,
};

// D20/D17 — a stock priced from the last stored CLOSE rather than a live
// quote (market closed, no PriceQuote yet). priceAsOf carries the CLOSE's
// own date, not "now" — see tracker.md's D20 design note.
const MSFT_CLOSE: HoldingDto = {
  assetId: 2,
  symbol: 'MSFT',
  name: 'Microsoft Corporation',
  assetClass: 'Stock',
  currency: 'USD',
  quantityHeld: 3,
  costBasisUsd: 1000,
  averageCostUsd: 333.3333333333,
  currentPriceNative: 381.700012,
  currentPriceUsd: 381.700012,
  priceAsOf: '2026-07-24T00:00:00+00:00',
  priceSource: 'Close',
  marketValueUsd: 1145.1,
  unrealizedPnlUsd: 145.1,
  unrealizedPnlPercent: 14.51,
  realizedPnlUsd: 0,
};

// A fully sold-down position: quantityHeld 0 means "no average cost", not
// "missing price" — averageCostUsd is null for that reason and must render
// as an em-dash, never borrow the "Awaiting price" treatment.
const GOOGL_SOLD_DOWN: HoldingDto = {
  assetId: 3,
  symbol: 'GOOGL',
  name: 'Alphabet Inc.',
  assetClass: 'Stock',
  currency: 'USD',
  quantityHeld: 0,
  costBasisUsd: 0,
  averageCostUsd: null,
  currentPriceNative: 175.5,
  currentPriceUsd: 175.5,
  priceAsOf: '2026-08-01T10:48:50+00:00',
  priceSource: 'Live',
  marketValueUsd: 0,
  unrealizedPnlUsd: 0,
  unrealizedPnlPercent: null,
  realizedPnlUsd: 612.4,
};

describe('HoldingsTable', () => {
  let fixture: ComponentFixture<HoldingsTable>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HoldingsTable],
      providers: [provideRouter([]), provideNoopAnimations()],
    });
    fixture = TestBed.createComponent(HoldingsTable);
    fixture.componentRef.setInput('basePath', '/stocks');
  });

  it('renders "Awaiting price"/"Awaiting first price" instead of a -100% loss when there is no live quote yet', () => {
    fixture.componentRef.setInput('holdings', [AAPL_NO_PRICE]);
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Awaiting price');
    expect(text).toContain('Awaiting first price');
    expect(text).not.toContain('-100.00%');
    expect(text).not.toContain('-100%');
  });

  it('never floors a sub-cent current price to $0.00', () => {
    fixture.componentRef.setInput('holdings', [ANVL_SUBCENT]);
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('$0.00050448');
    expect(text).toContain('1,000,000');
  });

  it('links each row to its detail page under the given base path', () => {
    fixture.componentRef.setInput('holdings', [ANVL_SUBCENT]);
    fixture.detectChanges();

    const link = fixture.nativeElement.querySelector('a.holdings-table__symbol') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('/stocks/ANVL');
  });

  it('labels a Close-sourced price with its own close date, distinct from a live price', () => {
    fixture.componentRef.setInput('holdings', [MSFT_CLOSE]);
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('$381.70');
    expect(text).toContain('Close');
    expect(text).toContain('Fri 24 Jul');

    const priceCell = fixture.nativeElement.querySelector('.holdings-table__price-source') as HTMLElement | null;
    expect(priceCell).not.toBeNull();
  });

  it('renders a live price with no close-date caveat', () => {
    fixture.componentRef.setInput('holdings', [ANVL_SUBCENT]);
    fixture.detectChanges();

    const priceCell = fixture.nativeElement.querySelector('.holdings-table__price-source') as HTMLElement | null;
    expect(priceCell).toBeNull();
  });

  it('never floors a sub-cent average cost to $0.00', () => {
    fixture.componentRef.setInput('holdings', [ANVL_SUBCENT]);
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('$0.0004001');
  });

  it('renders an em-dash, not $0.00 or "Awaiting price", for a fully sold-down position with no average cost', () => {
    fixture.componentRef.setInput('holdings', [GOOGL_SOLD_DOWN]);
    fixture.detectChanges();

    const cells = fixture.nativeElement.querySelectorAll('td.holdings-table__num') as NodeListOf<HTMLElement>;
    // Column order: quantity, cost basis, avg cost, current price, ...
    expect(cells[2].textContent?.trim()).toBe('—');
  });

  describe('sorting and pagination', () => {
    function symbols(fixture: ComponentFixture<HoldingsTable>): string[] {
      return Array.from(
        fixture.nativeElement.querySelectorAll('a.holdings-table__symbol') as NodeListOf<HTMLElement>,
      ).map((el) => el.textContent?.trim() ?? '');
    }

    it('defaults to market value descending — the largest position first', () => {
      fixture.componentRef.setInput('holdings', [AAPL_NO_PRICE, ANVL_SUBCENT, MSFT_CLOSE]);
      fixture.detectChanges();

      // Market values: AAPL 0 (no price), ANVL 504.48, MSFT 1145.1
      expect(symbols(fixture)).toEqual(['MSFT', 'ANVL', 'AAPL']);
    });

    it('reaches the <th> with mat-sort-header, emitting a real aria-sort', () => {
      fixture.componentRef.setInput('holdings', [ANVL_SUBCENT]);
      fixture.detectChanges();

      const marketValueHeader = fixture.nativeElement.querySelector(
        'th[mat-sort-header="marketValue"]',
      ) as HTMLElement;
      expect(marketValueHeader.getAttribute('aria-sort')).toBe('descending');
      expect(marketValueHeader.getAttribute('scope')).toBe('col');
    });

    it('sorts the unrealized column on null (not the fabricated -100% figure) for a holding with no price yet', () => {
      fixture.componentRef.setInput('holdings', [MSFT_CLOSE, AAPL_NO_PRICE]);
      fixture.detectChanges();

      fixture.componentInstance.onSortChange({ active: 'unrealized', direction: 'asc' });
      fixture.detectChanges();

      // AAPL has no price -> null unrealized -> sorts last even in ascending order,
      // despite its "raw" unrealizedPnlUsd (-3864) being numerically the smallest.
      expect(symbols(fixture)).toEqual(['MSFT', 'AAPL']);
    });

    it('hides the pager entirely when the row count fits the smallest page size', () => {
      fixture.componentRef.setInput('holdings', [ANVL_SUBCENT]);
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('app-table-pager .table-pager')).toBeNull();
    });

    it(
      'shows the pager and paginates once the row count exceeds the smallest page size',
      () => {
        const many: HoldingDto[] = Array.from({ length: 30 }, (_, i) => ({
          ...ANVL_SUBCENT,
          assetId: i + 1,
          symbol: `SYM${i}`,
          marketValueUsd: 30 - i,
        }));
        fixture.componentRef.setInput('holdings', many);
        fixture.detectChanges();

        expect(fixture.nativeElement.querySelector('app-table-pager .table-pager')).not.toBeNull();
        // Default page size 25 — only the first (highest market-value) 25 rows render.
        expect(fixture.nativeElement.querySelectorAll('tbody tr').length).toBe(25);
      },
      // Rendering 30 Material-styled rows is genuinely more DOM work than this
      // file's other cases — the default 5s vitest budget is comfortable on an
      // idle machine but tight under load, so this gets explicit headroom
      // rather than a systemic timeout bump (see tracker.md).
      15000,
    );
  });
});
