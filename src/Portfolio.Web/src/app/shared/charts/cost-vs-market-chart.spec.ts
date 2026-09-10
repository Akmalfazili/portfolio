import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideEchartsCore } from 'ngx-echarts';

import { CostVsMarketChart } from './cost-vs-market-chart';
import { PerformancePointDto } from '../../core/api/models';

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
    expect(fixture.nativeElement.textContent).toContain('once the position has transactions');
  });

  it('overrides the empty-state copy via the emptyMessage input, for a caller plotting a different scope', () => {
    fixture.componentRef.setInput('points', []);
    fixture.componentRef.setInput(
      'emptyMessage',
      'No priced history yet — this chart fills in once your stocks have transactions and daily closes.',
    );
    fixture.detectChanges();
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('once your stocks have transactions and daily closes');
    expect(text).not.toContain('once the position has transactions');
  });

  it('renders cost basis as a STEP series and market value as a smooth line — never the reverse', () => {
    fixture.componentRef.setInput('points', POINTS);
    fixture.detectChanges();

    const series = (
      fixture.componentInstance.options() as {
        series: { name: string; step?: string; smooth?: boolean }[];
      }
    ).series;

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

  it("always keeps at least the series' own last point, however narrow the range", () => {
    // A single point far in the past would filter to nothing under a
    // wall-clock-anchored range; anchored to its own date it always survives.
    const sparse: PerformancePointDto[] = [
      { date: '2020-01-01', costBasisUsd: 10, marketValueUsd: 10 },
    ];
    fixture.componentRef.setInput('points', sparse);
    fixture.componentInstance.setRange('1M');
    fixture.detectChanges();
    expect(fixture.componentInstance.filteredPoints()).toHaveLength(1);
  });

  describe('D15 — colliding end labels', () => {
    type EndLabelSeries = { name: string; endLabel: { offset: [number, number] } };

    it('nudges the two end labels apart, greater value on top, when cost and market are close at the end', () => {
      // $689.33 vs $680.32 — the exact real-data case from the tracker's D15
      // entry, about $9 apart on a series ranging roughly $335→$690.
      const close: PerformancePointDto[] = [
        { date: '2026-07-20', costBasisUsd: 336.92, marketValueUsd: 340.1 },
        { date: '2026-07-22', costBasisUsd: 689.33, marketValueUsd: 650.0 },
        { date: '2026-07-24', costBasisUsd: 689.33, marketValueUsd: 680.32 },
      ];
      fixture.componentRef.setInput('points', close);
      fixture.detectChanges();

      const series = (fixture.componentInstance.options() as { series: EndLabelSeries[] }).series;
      const market = series.find((s) => s.name === 'Market value')!;
      const cost = series.find((s) => s.name === 'Cost basis')!;

      // Cost ($689.33) is the greater value at the end, so it goes on top
      // (negative y is up in ECharts' offset convention) and market goes
      // below — and, above all, they must not land on the same offset.
      expect(cost.endLabel.offset).not.toEqual(market.endLabel.offset);
      expect(cost.endLabel.offset[1]).toBeLessThan(0);
      expect(market.endLabel.offset[1]).toBeGreaterThan(0);
    });

    it('leaves both end labels un-offset when cost and market are clearly separated', () => {
      const separated: PerformancePointDto[] = [
        { date: '2026-07-20', costBasisUsd: 100, marketValueUsd: 100 },
        { date: '2026-07-24', costBasisUsd: 100, marketValueUsd: 400 },
      ];
      fixture.componentRef.setInput('points', separated);
      fixture.detectChanges();

      const series = (fixture.componentInstance.options() as { series: EndLabelSeries[] }).series;
      for (const s of series) {
        expect(s.endLabel.offset).toEqual([0, 0]);
      }
    });
  });

  describe('layout defect fixes (live-browser-measured)', () => {
    type GridOptions = { grid: { right: number } };
    type XAxisOptions = { xAxis: { axisLabel: { hideOverlap?: boolean } } };

    it('grows grid.right with a wider end label, rather than clipping it at a fixed reservation', () => {
      const narrow: PerformancePointDto[] = [
        { date: '2026-07-20', costBasisUsd: 400, marketValueUsd: 400 },
        { date: '2026-07-24', costBasisUsd: 400, marketValueUsd: 461.98 },
      ];
      fixture.componentRef.setInput('points', narrow);
      fixture.detectChanges();
      const narrowRight = (fixture.componentInstance.options() as GridOptions).grid.right;

      const wide: PerformancePointDto[] = [
        { date: '2026-07-20', costBasisUsd: 400, marketValueUsd: 400 },
        { date: '2026-07-24', costBasisUsd: 123456.78, marketValueUsd: 400 },
      ];
      fixture.componentRef.setInput('points', wide);
      fixture.detectChanges();
      const wideRight = (fixture.componentInstance.options() as GridOptions).grid.right;

      // "$123,456.78" is substantially wider than "$461.98" — the reserved
      // space must actually track that, not stay pinned at a constant that
      // clips whichever value happens to be too wide for it (the measured
      // live defect: "$53,940.96" rendering as "$53,940.9").
      expect(wideRight).toBeGreaterThan(narrowRight);
    });

    it('sets hideOverlap on the x-axis label so ticks thin out instead of colliding at narrow widths', () => {
      fixture.componentRef.setInput('points', POINTS);
      fixture.detectChanges();
      const options = fixture.componentInstance.options() as XAxisOptions;
      expect(options.xAxis.axisLabel.hideOverlap).toBe(true);
    });
  });

  describe('D19 — x-axis tick format tracks the visible range span', () => {
    function axisFormatter(
      fixture: ComponentFixture<CostVsMarketChart>,
    ): (value: number) => string {
      const options = fixture.componentInstance.options() as {
        xAxis: { axisLabel: { formatter: (value: number) => string } };
      };
      return options.xAxis.axisLabel.formatter;
    }

    /**
     * The formatter must be exercised with the timestamps ECharts ACTUALLY
     * hands it, which is the whole point of this helper existing.
     *
     * The series data passes plain "YYYY-MM-DD" strings to a `type: 'time'`
     * axis, and ECharts parses that shape as LOCAL midnight — not the UTC
     * midnight the native `Date('2026-07-22T00:00:00Z')` parser would give.
     * The first version of these tests fed in UTC midnight, which no code
     * path ever produces, and so passed against a formatter that rendered
     * the real axis a full day early at every positive UTC offset (browser
     * pass, Asia/Singapore: a Jul 20–24 series labelled "Jul 19 … Jul 23").
     *
     * Building the input with the local-midnight constructor keeps these
     * tests honest in any timezone, and makes them fail if the formatter
     * goes back to UTC.
     */
    const localMidnight = (y: number, monthIndex: number, d: number): number =>
      new Date(y, monthIndex, d).getTime();

    it('shows month + day over a short (1M-scale) span, never a bare day number', () => {
      const days: PerformancePointDto[] = [
        { date: '2026-07-20', costBasisUsd: 100, marketValueUsd: 100 },
        { date: '2026-07-24', costBasisUsd: 100, marketValueUsd: 105 },
      ];
      fixture.componentRef.setInput('points', days);
      fixture.detectChanges();

      const label = axisFormatter(fixture)(localMidnight(2026, 6, 22));
      expect(label).toBe('Jul 22');
    });

    it('labels a tick with the same calendar day the tooltip shows, at any UTC offset', () => {
      // The regression guard for the browser-pass finding. The tooltip reads
      // the raw date string, so an axis that disagrees with it by a day is
      // self-evidently wrong — and only reproduces at positive offsets, which
      // is why a UTC CI container never caught it.
      const days: PerformancePointDto[] = [
        { date: '2026-07-20', costBasisUsd: 100, marketValueUsd: 100 },
        { date: '2026-07-24', costBasisUsd: 100, marketValueUsd: 105 },
      ];
      fixture.componentRef.setInput('points', days);
      fixture.detectChanges();

      // Exactly how ECharts resolves the first point's "2026-07-20".
      expect(axisFormatter(fixture)(localMidnight(2026, 6, 20))).toBe('Jul 20');
      expect(axisFormatter(fixture)(localMidnight(2026, 6, 24))).toBe('Jul 24');
    });

    it('shows month + year over a ~1Y span', () => {
      const year: PerformancePointDto[] = [
        { date: '2025-08-01', costBasisUsd: 100, marketValueUsd: 100 },
        { date: '2026-07-24', costBasisUsd: 100, marketValueUsd: 130 },
      ];
      fixture.componentRef.setInput('points', year);
      fixture.detectChanges();

      const label = axisFormatter(fixture)(localMidnight(2026, 0, 15));
      expect(label).toBe('Jan 2026');
    });

    it('shows year only over a multi-year "All" span', () => {
      const multiYear: PerformancePointDto[] = [
        { date: '2022-01-01', costBasisUsd: 100, marketValueUsd: 100 },
        { date: '2026-07-24', costBasisUsd: 100, marketValueUsd: 200 },
      ];
      fixture.componentRef.setInput('points', multiYear);
      fixture.detectChanges();

      const label = axisFormatter(fixture)(localMidnight(2024, 5, 1));
      expect(label).toBe('2024');
    });
  });
});
