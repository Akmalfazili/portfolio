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

  /**
   * D17 residual. Before this, an unpriced holding rendered a `0.0% / $0.00`
   * legend row — visually identical to a genuinely negligible position, so a
   * holding whose value is simply unknown read as one worth nothing. The
   * summary tiles had carried this caveat since D17; the pie never did.
   */
  /** The legend row for one symbol, so assertions target that row and not the
   *  whole component's text (where "100.0%" trivially contains "0.0%"). */
  function rowTextFor(symbol: string): string {
    const rows = Array.from(
      fixture.nativeElement.querySelectorAll('.allocation-pie__row'),
    ) as HTMLElement[];
    const row = rows.find(
      (r) => r.querySelector('.allocation-pie__symbol')?.textContent?.trim() === symbol,
    );
    if (!row) {
      throw new Error(`No legend row for ${symbol}`);
    }
    return row.textContent ?? '';
  }

  it('says "No price yet" instead of 0.0% for a holding whose price is unknown', () => {
    fixture.componentRef.setInput('slices', [
      { assetId: 1, symbol: 'AAPL', name: 'Apple Inc.', value: 0, percent: 0, unpriced: true },
      { assetId: 4, symbol: 'ETH', name: 'Ethereum', value: 932.625, percent: 100 },
    ] satisfies AllocationSlice[]);
    fixture.detectChanges();

    const aapl = rowTextFor('AAPL');
    expect(aapl).toContain('No price yet');
    // The misleading pair must not be rendered for that row at all.
    expect(aapl).not.toContain('%');
    expect(aapl).not.toContain('$0.00');

    // The priced holding alongside it is untouched.
    expect(rowTextFor('ETH')).toContain('$932.63');
  });

  it('still shows a real 0.0% for a priced but negligible holding', () => {
    fixture.componentRef.setInput('slices', [
      { assetId: 1, symbol: 'AAPL', name: 'Apple Inc.', value: 0.001, percent: 0, unpriced: false },
      { assetId: 4, symbol: 'ETH', name: 'Ethereum', value: 932.625, percent: 100 },
    ] satisfies AllocationSlice[]);
    fixture.detectChanges();

    const aapl = rowTextFor('AAPL');
    expect(aapl).toContain('0.0%');
    expect(aapl).not.toContain('No price yet');
  });

  /**
   * Follow-up on the orphan-leader-line fix: an 8% label threshold removed
   * the orphan lines by hiding small slices' labels entirely (PG at 5.8% on
   * /stocks, ETH on /crypto market value, near-zero slivers on /crypto cost
   * basis). The user wants every slice labelled — no threshold, nothing
   * silently hidden. Overlap is handled by pie's native `avoidLabelOverlap`
   * shifting labels apart (see the `labelLayout`/`minAngle` tests below for
   * how that pass is kept from detaching a line from its label), never by
   * dropping one.
   */
  it('shows a label and a leader line on every slice — no threshold, nothing hidden', () => {
    const many: AllocationSlice[] = [
      { assetId: 1, symbol: 'AMP', name: 'Amp', value: 999.9, percent: 99.98 },
      { assetId: 2, symbol: 'ETH', name: 'Ethereum', value: 0.06, percent: 0.006 },
      { assetId: 3, symbol: 'ANVL', name: 'Anvil', value: 0.04, percent: 0.004 },
    ];
    fixture.componentRef.setInput('slices', many);
    fixture.detectChanges();

    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const series = (fixture.componentInstance.options() as any).series[0];
    expect(series.label.show).toBe(true);
    expect(series.labelLine.show).toBe(true);
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    for (const item of series.data as any[]) {
      // No per-item override quietly turns either off for a small slice.
      expect(item.label?.show).not.toBe(false);
      expect(item.labelLine?.show).not.toBe(false);
    }
  });

  /**
   * Follow-up: the generic `labelLayout.moveOverlap: 'shiftY'` pass shifts a
   * label's y without moving its leader line (verified from ECharts' own
   * source), which measurably overlapped label blocks in the browser (PG/
   * ERIC on /stocks, ANVL/ETH on /crypto cost basis). Overlap must now be
   * resolved entirely by pie's own native `avoidLabelOverlap` pass, which
   * keeps the line glued to the label it moves. `hideOverlap: false` stays
   * — nothing may be silently hidden — but nothing may move a label without
   * its line following.
   */
  it('resolves overlap via native avoidLabelOverlap only — no generic moveOverlap pass that could detach a line from its label', () => {
    fixture.componentRef.setInput('slices', SLICES);
    fixture.detectChanges();

    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const series = (fixture.componentInstance.options() as any).series[0];
    expect(series.avoidLabelOverlap).toBe(true);
    expect(series.labelLayout).toEqual({ hideOverlap: false });
  });

  it('gives every slice a rendered wedge wide enough for its label block to clear its neighbours', () => {
    // The crypto cost-basis worst case, measured in the browser: AMP ~100%,
    // ANVL and ETH both ~0%. At minAngle 4 the two near-zero slices' label
    // blocks still overlapped by ~2px and one grazed the ring. 8 degrees is
    // the value that gave the native overlap pass enough angular room to
    // separate them.
    fixture.componentRef.setInput('slices', [
      { assetId: 1, symbol: 'AMP', name: 'Amp', value: 1000, percent: 99.98 },
      { assetId: 2, symbol: 'ETH', name: 'Ethereum', value: 0.1, percent: 0.01 },
      { assetId: 3, symbol: 'ANVL', name: 'Anvil', value: 0.1, percent: 0.01 },
    ]);
    fixture.detectChanges();

    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const series = (fixture.componentInstance.options() as any).series[0];
    expect(series.minAngle).toBeGreaterThanOrEqual(8);
  });

  /**
   * Follow-up: the label text rendered ABOVE the leader line's end rather
   * than beside it. `alignTo: 'labelLine'` ties the text's horizontal anchor
   * to the line, `verticalAlign: 'middle'` centres the two-line block on the
   * line's y, and `distanceToLabelLine` gives a small deliberate gap. `align`
   * must stay unset so ECharts keeps auto-flipping left/right per side.
   */
  it('positions the label beside the leader line, not above it', () => {
    fixture.componentRef.setInput('slices', SLICES);
    fixture.detectChanges();

    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const series = (fixture.componentInstance.options() as any).series[0];
    expect(series.label.alignTo).toBe('labelLine');
    expect(series.label.verticalAlign).toBe('middle');
    expect(series.label.distanceToLabelLine).toBeGreaterThan(0);
    // Must not hardcode a side — that would fight ECharts' automatic
    // left/right flip between the two halves of the donut.
    expect(series.label.align).toBeUndefined();
  });

  /**
   * Follow-up: a two-tier `length2` (short leader line under 5% share, long
   * otherwise) created a visible discontinuity — a mid-size slice sandwiched
   * between two labels `shiftY` had pushed apart (PG at 5.8% on /stocks
   * market value, between "Other" and ERIC) landed on the short tier exactly
   * where it needed the most room, so its label crowded the donut ring while
   * its neighbours sat farther out. Every slice must share one leader-line
   * geometry so labels sit at a uniform radial distance regardless of size.
   */
  it('gives every slice the same leader-line geometry — no per-slice tiering by size', () => {
    const eightStockLikeSlices: AllocationSlice[] = [
      { assetId: 1, symbol: 'AVGO', name: 'Broadcom', value: 227, percent: 22.7 },
      { assetId: 2, symbol: 'MSFT', name: 'Microsoft', value: 159, percent: 15.9 },
      { assetId: 3, symbol: 'AMZN', name: 'Amazon', value: 136, percent: 13.6 },
      { assetId: 4, symbol: 'SPUS', name: 'SP Funds', value: 130, percent: 13.0 },
      { assetId: 5, symbol: 'HLAL', name: 'Wahed', value: 113, percent: 11.3 },
      { assetId: 6, symbol: 'ERIC', name: 'Ericsson', value: 89, percent: 8.9 },
      { assetId: 7, symbol: 'PG', name: 'Procter & Gamble', value: 58, percent: 5.8 },
      { assetId: 8, symbol: 'OTH', name: 'Other holding', value: 88, percent: 8.8 },
    ];
    fixture.componentRef.setInput('slices', eightStockLikeSlices);
    fixture.detectChanges();

    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const series = (fixture.componentInstance.options() as any).series[0];
    // The series-level geometry is generous and fixed...
    expect(series.labelLine.length).toBeGreaterThanOrEqual(14);
    expect(series.labelLine.length2).toBeGreaterThanOrEqual(20);
    // ...and no data item — regardless of its share, including PG at 5.8% —
    // overrides it with a shorter (or longer) leader line of its own.
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    for (const item of series.data as any[]) {
      expect(item.labelLine).toBeUndefined();
    }
  });

  it('formats a near-zero slice as "<0.1%" instead of a misleading "0.0%"', () => {
    fixture.componentRef.setInput('slices', SLICES);
    fixture.detectChanges();

    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const series = (fixture.componentInstance.options() as any).series[0];
    expect(series.label.formatter({ percent: 0.03, name: 'ANVL' })).toContain('<0.1%');
    expect(series.label.formatter({ percent: 22.7, name: 'AVGO' })).toContain('22.7%');
  });

  it('assigns colour by asset identity, not by current value rank', () => {
    fixture.componentRef.setInput('slices', SLICES);
    fixture.detectChanges();
    const firstPass = new Map(
      fixture.componentInstance.legendRows().map((r) => [r.assetId, r.color]),
    );

    // Same assets, reversed relative size — colour must not repaint.
    const reordered: AllocationSlice[] = [
      { ...SLICES[1], value: 5000 }, // ANVL now the larger slice
      { ...SLICES[0], value: 10 },
    ];
    fixture.componentRef.setInput('slices', reordered);
    fixture.detectChanges();
    const secondPass = new Map(
      fixture.componentInstance.legendRows().map((r) => [r.assetId, r.color]),
    );

    expect(secondPass.get(4)).toBe(firstPass.get(4));
    expect(secondPass.get(6)).toBe(firstPass.get(6));
  });
});
