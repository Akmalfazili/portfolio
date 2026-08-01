import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideEchartsCore } from 'ngx-echarts';

import { AllocationPieChart, AllocationSlice } from './allocation-pie-chart';

const SLICES: AllocationSlice[] = [
  { assetId: 4, symbol: 'ETH', name: 'Ethereum', value: 932.625, percent: 64.8961 },
  { assetId: 6, symbol: 'ANVL', name: 'Anvil', value: 504.48, percent: 35.1039 },
];

describe('AllocationPieChart', () => {
  let fixture: ComponentFixture<AllocationPieChart>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [AllocationPieChart],
      providers: [provideEchartsCore({ echarts: () => import('echarts') })],
    });
    fixture = TestBed.createComponent(AllocationPieChart);
  });

  it('renders a legend row per slice with symbol, percent and money value', () => {
    fixture.componentRef.setInput('slices', SLICES);
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('ETH');
    expect(text).toContain('ANVL');
    expect(text).toContain('64.9%');
    expect(text).toContain('$932.63');
  });

  it('shows an empty message and no chart when nothing is held', () => {
    fixture.componentRef.setInput('slices', []);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('No market value to allocate yet');
    expect(fixture.nativeElement.querySelector('[echarts]')).toBeFalsy();
  });

  it('folds a 9th+ holding into a single "Other" legend row rather than generating a 9th colour', () => {
    const many: AllocationSlice[] = Array.from({ length: 10 }, (_, i) => ({
      assetId: i + 1,
      symbol: `A${i}`,
      name: `Asset ${i}`,
      value: 100 - i,
      percent: 10,
    }));
    fixture.componentRef.setInput('slices', many);
    fixture.detectChanges();

    expect(fixture.componentInstance.legendRows()).toHaveLength(8);
    expect(fixture.componentInstance.legendRows().at(-1)?.symbol).toBe('Other');
  });

  it('assigns colour by asset identity, not by current value rank', () => {
    fixture.componentRef.setInput('slices', SLICES);
    fixture.detectChanges();
    const firstPass = new Map(fixture.componentInstance.legendRows().map((r) => [r.assetId, r.color]));

    // Same assets, reversed relative size — colour must not repaint.
    const reordered: AllocationSlice[] = [
      { ...SLICES[1], value: 5000 }, // ANVL now the larger slice
      { ...SLICES[0], value: 10 },
    ];
    fixture.componentRef.setInput('slices', reordered);
    fixture.detectChanges();
    const secondPass = new Map(fixture.componentInstance.legendRows().map((r) => [r.assetId, r.color]));

    expect(secondPass.get(4)).toBe(firstPass.get(4));
    expect(secondPass.get(6)).toBe(firstPass.get(6));
  });
});
