import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

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
  currentPriceNative: null,
  currentPriceUsd: null,
  priceAsOf: null,
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
  currentPriceNative: 0.00050448,
  currentPriceUsd: 0.00050448,
  priceAsOf: '2026-08-01T10:48:50+00:00',
  marketValueUsd: 504.48,
  unrealizedPnlUsd: 104.38,
  unrealizedPnlPercent: 26.0885,
  realizedPnlUsd: 0,
};

describe('HoldingsTable', () => {
  let fixture: ComponentFixture<HoldingsTable>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HoldingsTable],
      providers: [provideRouter([])],
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
});
