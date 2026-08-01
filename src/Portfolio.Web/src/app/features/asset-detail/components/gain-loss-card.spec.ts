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
  currentPriceNative: 1865.25,
  currentPriceUsd: 1865.25,
  priceAsOf: '2026-08-01T10:48:40+00:00',
  marketValueUsd: 932.625,
  unrealizedPnlUsd: 27.625,
  unrealizedPnlPercent: 3.0525,
  realizedPnlUsd: 0,
};

const UNPRICED: HoldingDto = {
  ...PRICED,
  symbol: 'AAPL',
  currentPriceNative: null,
  currentPriceUsd: null,
  priceAsOf: null,
  marketValueUsd: 0,
  costBasisUsd: 3864,
  unrealizedPnlUsd: -3864,
  unrealizedPnlPercent: -100,
  realizedPnlUsd: 23,
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
});
