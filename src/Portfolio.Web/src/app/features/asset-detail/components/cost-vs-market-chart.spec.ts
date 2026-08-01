import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideEchartsCore } from 'ngx-echarts';

import { CostVsMarketChart } from './cost-vs-market-chart';
import { PerformancePointDto } from '../../../core/api/models';

const POINTS: PerformancePointDto[] = [
  { date: '2026-07-20', costBasisUsd: 3201, marketValueUsd: 3265.9 },
  { date: '2026-07-21', costBasisUsd: 3201, marketValueUsd: 3277.4 },
  { date: '2026-07-22', costBasisUsd: 4830, marketValueUsd: 4888.35 },
  { date: '2026-07-23', costBasisUsd: 4830, marketValueUsd: 4824.9 },
  { date: '2026-07-24', costBasisUsd: 3864, marketValueUsd: 3996.24 },
];

describe('CostVsMarketChart', () => {
  let fixture: ComponentFixture<CostVsMarketChart>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [CostVsMarketChart],
      providers: [provideEchartsCore({ echarts: () => import('echarts') })],
    });
    fixture = TestBed.createComponent(CostVsMarketChart);
  });

  it('shows the empty state with no points at all', () => {
    fixture.componentRef.setInput('points', []);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('No priced history yet');
  });

  it('renders cost basis as a STEP series and market value as a smooth line — never the reverse', () => {
    fixture.componentRef.setInput('points', POINTS);
    fixture.detectChanges();

    const series = (fixture.componentInstance.options() as {
      series: { name: string; step?: string; smooth?: boolean }[];
    }).series;

    const cost = series.find((s) => s.name === 'Cost basis');
    const market = series.find((s) => s.name === 'Market value');

    expect(cost?.step).toBe('end');
    expect(cost?.smooth).toBeUndefined();
    expect(market?.smooth).toBe(true);
    expect(market?.step).toBeUndefined();
  });

  it('defaults to the "All" range and shows every point', () => {
    fixture.componentRef.setInput('points', POINTS);
    fixture.detectChanges();
    expect(fixture.componentInstance.range()).toBe('All');
    expect(fixture.componentInstance.filteredPoints()).toHaveLength(5);
  });

  it('filters to the last N days anchored on the series\' own last date, not wall-clock "today"', () => {
    // "today" in this test environment is nowhere near 2026-07 — a range
    // anchored to wall-clock time would filter everything out. Anchoring to
    // the series' own last point keeps all 5 in a "1M" window because they
    // span only 5 days.
    fixture.componentRef.setInput('points', POINTS);
    fixture.detectChanges();

    fixture.componentInstance.setRange('1M');
    fixture.detectChanges();
    expect(fixture.componentInstance.filteredPoints()).toHaveLength(5);
  });

  it('narrows correctly for a range shorter than the data span', () => {
    const wide: PerformancePointDto[] = [
      { date: '2025-01-01', costBasisUsd: 100, marketValueUsd: 100 },
      { date: '2025-06-01', costBasisUsd: 100, marketValueUsd: 150 },
      { date: '2025-12-01', costBasisUsd: 200, marketValueUsd: 250 },
      { date: '2025-12-15', costBasisUsd: 200, marketValueUsd: 260 },
      { date: '2025-12-31', costBasisUsd: 200, marketValueUsd: 270 },
    ];
    fixture.componentRef.setInput('points', wide);
    fixture.detectChanges();

    fixture.componentInstance.setRange('1M');
    fixture.detectChanges();
    // Anchored on 2025-12-31, a 30-day window keeps only Dec points.
    const filtered = fixture.componentInstance.filteredPoints();
    expect(filtered.map((p) => p.date)).toEqual(['2025-12-01', '2025-12-15', '2025-12-31']);
  });

  it('always keeps at least the series\' own last point, however narrow the range', () => {
    // A single point far in the past would filter to nothing under a
    // wall-clock-anchored range; anchored to its own date it always survives.
    const sparse: PerformancePointDto[] = [{ date: '2020-01-01', costBasisUsd: 10, marketValueUsd: 10 }];
    fixture.componentRef.setInput('points', sparse);
    fixture.componentInstance.setRange('1M');
    fixture.detectChanges();
    expect(fixture.componentInstance.filteredPoints()).toHaveLength(1);
  });
});
