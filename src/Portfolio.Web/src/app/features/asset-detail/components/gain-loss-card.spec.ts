import { ComponentFixture, TestBed } from '@angular/core/testing';

import { GainLossCard } from './gain-loss-card';
import { HoldingDto } from '../../../core/api/models';

const PRICED: HoldingDto = {
  assetId: 4,
  symbol: 'ETH',
  name: 'Ethereum',
  assetClass: 'Crypto',
  currency: 'USD',
  quantityHeld: 0.5,
  costBasisUsd: 905,
  averageCostUsd: 1810,
  currentPriceNative: 1865.25,
  currentPriceUsd: 1865.25,
  priceAsOf: '2026-08-01T10:48:40+00:00',
  priceSource: 'Live',
  marketValueUsd: 932.625,
  unrealizedPnlUsd: 27.625,
  unrealizedPnlPercent: 3.0525,
  realizedPnlUsd: 0,
  dividendsTrailing12MonthUsd: null,
  dividendsAllTimeUsd: null,
  dividendCoverageStatus: null,
};

const UNPRICED: HoldingDto = {
  ...PRICED,
  symbol: 'AAPL',
  currentPriceNative: null,
  currentPriceUsd: null,
  priceAsOf: null,
  priceSource: null,
  marketValueUsd: 0,
  costBasisUsd: 3864,
  averageCostUsd: 322,
  unrealizedPnlUsd: -3864,
  unrealizedPnlPercent: -100,
  realizedPnlUsd: 23,
};

// Fully sold-down: quantityHeld 0 means averageCostUsd is null for that
// reason alone — a priced holding, so this must NOT hit the "Awaiting price"
// path, and averageCostUsd must render as an em-dash, not $0.00.
const SOLD_DOWN: HoldingDto = {
  ...PRICED,
  symbol: 'DOGE',
  quantityHeld: 0,
  costBasisUsd: 0,
  averageCostUsd: null,
  marketValueUsd: 0,
  unrealizedPnlUsd: 0,
  unrealizedPnlPercent: null,
  realizedPnlUsd: 410.5,
};

describe('GainLossCard', () => {
  let fixture: ComponentFixture<GainLossCard>;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [GainLossCard] });
    fixture = TestBed.createComponent(GainLossCard);
  });

  it('shows cost basis, market value and signed unrealized/realized gain for a priced holding', () => {
    fixture.componentRef.setInput('holding', PRICED);
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('$905.00');
    expect(text).toContain('$1,810.00');
    expect(text).toContain('$932.63');
    expect(text).toContain('+$27.63');
    expect(text).toContain('+3.05%');
  });

  it('shows "Awaiting price"/"Awaiting first price" for an unpriced holding, never a -100% loss', () => {
    fixture.componentRef.setInput('holding', UNPRICED);
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Awaiting price');
    expect(text).toContain('Awaiting first price');
    expect(text).not.toContain('-100.00%');
  });

  it('still shows realized gain/loss even when there is no live quote', () => {
    fixture.componentRef.setInput('holding', UNPRICED);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('+$23.00');
  });

  it('renders an em-dash for average cost on a fully sold-down position, not $0.00 or "Awaiting price"', () => {
    fixture.componentRef.setInput('holding', SOLD_DOWN);
    fixture.detectChanges();

    const tiles = fixture.nativeElement.querySelectorAll('app-stat-tile');
    const avgCostTile = Array.from(tiles as NodeListOf<HTMLElement>).find(
      (tile) => tile.querySelector('.stat-tile__label')?.textContent?.trim() === 'Avg cost',
    );
    expect(avgCostTile?.querySelector('.stat-tile__value')?.textContent?.trim()).toBe('—');
  });
});
