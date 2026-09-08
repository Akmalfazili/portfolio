import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { NgxEchartsDirective } from 'ngx-echarts';
import type { EChartsCoreOption } from 'echarts/core';

import { MoneyPipe } from '../../../shared/pipes/money.pipe';
import { formatMoney } from '../../../shared/util/format-money';
import { foldToOther, itemTooltip, readChartTokens, seriesColor } from '../../../shared/charts/chart-theme';

export interface AllocationSlice {
  assetId: number;
  symbol: string;
  name: string;
  value: number;
  percent: number;

  /**
   * D17 residual — this holding has no price from any source, so `value` is 0
   * because it is *unknown*, not because the position is worthless. The legend
   * says so instead of rendering a `0.0% / $0.00` row that reads identically to
   * a genuinely negligible position.
   *
   * Only ever true in market-value mode: cost basis is known for every holding,
   * priced or not, so the cost pie has nothing to caveat.
   */
  unpriced?: boolean;
}

interface Slot extends AllocationSlice {
  color: string;
}

/**
 * The allocation pie — cost-basis and market-value modes are two different
 * `slices` arrays computed by the parent (from PortfolioSummaryDto.Holdings
 * and PortfolioAllocationDto respectively); this component only draws.
 *
 * Colour is assigned by a STABLE identity order (ascending assetId), not by
 * current rank, so a holding never changes colour just because its relative
 * size shifted between two renders (anti-patterns.md: "recolor-on-filter").
 * Slices are still drawn largest-first for legibility; more than 8 holdings
 * fold the smallest tail into a single muted "Other" slot rather than
 * generating a 9th hue.
 *
 * The HTML legend beneath the chart is not decorative — three of the eight
 * categorical hues (aqua, yellow, magenta) sit below 3:1 contrast on a light
 * surface by design (see the skill's palette.md), and the documented
 * mitigation is visible direct labels/values, which the legend rows provide
 * for every slice regardless of chart-label crowding.
 */
@Component({
  selector: 'app-allocation-pie-chart',
  standalone: true,
  imports: [NgxEchartsDirective, MoneyPipe, DecimalPipe],
  templateUrl: './allocation-pie-chart.html',
  styleUrl: './allocation-pie-chart.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AllocationPieChart {
  readonly slices = input.required<AllocationSlice[]>();
  readonly valueLabel = input<string>('Market value');

  private readonly slots = computed<Slot[]>(() => {
    const tokens = readChartTokens();
    // Stable identity order for colour assignment — ascending assetId, not
    // current value rank.
    const byIdentity = [...this.slices()].sort((a, b) => a.assetId - b.assetId);
    const colorByAssetId = new Map(byIdentity.map((s, i) => [s.assetId, seriesColor(tokens, i)]));

    // Display order — largest first, easiest to read; folds the smallest
    // tail into "Other" past 8 total slots.
    const byValue = [...this.slices()].sort((a, b) => b.value - a.value);
    const folded = foldToOther<AllocationSlice>(
      byValue,
      (rest) => ({
        assetId: -1,
        symbol: 'Other',
        name: `${rest.length} smaller holdings`,
        value: rest.reduce((sum, r) => sum + r.value, 0),
        percent: rest.reduce((sum, r) => sum + r.percent, 0),
        // An aggregate of several holdings is not itself "the unpriced one",
        // even if some of what it folded in was unpriced.
        unpriced: false,
      }),
      8,
    );

    return folded.map((slice) => ({
      ...slice,
      color: slice.assetId === -1 ? tokens.onSurfaceMuted : (colorByAssetId.get(slice.assetId) ?? tokens.onSurfaceMuted),
    }));
  });

  readonly hasData = computed(() => this.slices().some((s) => s.value > 0));

  readonly legendRows = computed(() => this.slots());

  readonly options = computed<EChartsCoreOption>(() => {
    const tokens = readChartTokens();
    const slots = this.slots();

    // Values as handed to the pie series (rounded to money precision).
    const roundedValues = slots.map((s) => Math.round(s.value * 100) / 100);
    const formatPercent = (percent: number) => (percent > 0 && percent < 0.1 ? '<0.1%' : `${percent.toFixed(1)}%`);

    return {
      tooltip: {
        ...itemTooltip(tokens),
        formatter: (params: unknown) => {
          const p = params as { name: string; value: number; percent: number };
          const money = formatMoney(p.value);
          return `${p.name}: ${money} (${formatPercent(p.percent)})`;
        },
      },
      legend: { show: false }, // the HTML legend below the chart is the real one
      series: [
        {
          type: 'pie',
          // Shrunk from ['45%', '72%'] to leave uniform radial room for every
          // slice's leader line now that all 8 are labelled at once (the
          // tightest case: 8 stock holdings, each with a label competing for
          // the same ring of space) — the donut has slack around it in the
          // card at either size, so give the labels the room instead of the
          // ring.
          radius: ['38%', '58%'],
          center: ['50%', '50%'],
          avoidLabelOverlap: true,
          // Every slice gets a rendered wedge with a distinguishable start/end
          // angle, however small its true value share — without this, two
          // near-zero slivers next to a ~100% slice collapse to the *same*
          // angle and no amount of label-shifting can tell their leader lines
          // apart (the crypto cost-basis case: one holding ~100%, two others
          // essentially zero). This changes only the rendered wedge width,
          // never the value/percent shown in the label, tooltip or legend.
          //
          // Measured in the browser (SVG geometry via getBoundingClientRect):
          // at 4 degrees, ANVL's and ETH's label blocks (both near-zero cost
          // basis, next to a ~100% AMP slice) still overlapped by ~2px and
          // ETH's text grazed the ring's outer edge by ~2px. 8 degrees lifts
          // that clearance to ~7px, which is the improvement this buys —
          // and no more. Re-measured, the cluster is still not clean: ANVL
          // and ETH's label blocks stack at 10/12/10px line spacing, so the
          // gap *between* the two labels is barely wider than the gap
          // *within* each, and ETH's leader line degenerates to a 2-point
          // stub where every other line in the chart is a 3-point elbow.
          //
          // Pushing `minAngle` higher would separate them further at a real
          // cost: ANVL and ETH have a cost basis of exactly $0, so at 8
          // degrees they already draw the same wedge a genuine 2.2% holding
          // would get. The geometry overstates zero-value positions; the
          // label, tooltip and legend all still read 0.0%. That trade is why
          // this stops at 8 — a deliberate accepted limit, not an oversight.
          minAngle: 8,
          itemStyle: {
            borderColor: tokens.surface,
            borderWidth: 2, // the surface-gap spacer between adjacent slices
          },
          // Every slice is labelled — no threshold, no silently-hidden
          // labels. `label` and `labelLine` are always shown TOGETHER: a
          // formatter that goes silent for some slices while the leader line
          // still draws is exactly how an orphan leader line (a line pointing
          // at nothing) happened before.
          label: {
            show: true,
            formatter: (params: unknown) => {
              const p = params as { percent: number; name: string };
              return `${p.name}\n${formatPercent(p.percent)}`;
            },
            color: tokens.onSurfaceSecondary,
            fontFamily: 'var(--ui-font-family-sans)',
            fontSize: 11,
            // Positions the text beside the leader line's end rather than
            // above it. `alignTo: 'labelLine'` ties the text's horizontal
            // anchor to the line's second segment (ECharts also aligns same-
            // side labels into a column under this mode — an intentional
            // built-in effect, not a reintroduction of the per-slice size
            // tiering removed above: the *configured* `length`/`length2`
            // stay fixed, only the *rendered* length varies to line the
            // column up). `verticalAlign: 'middle'` centres the two-line
            // `ticker\npercent` block on the line's y so the line points at
            // the text, not at its top edge. `align` is deliberately left
            // unset — ECharts already auto-flips it left/right per side, and
            // hardcoding it here would fight that (left-side labels would
            // stop being right-aligned toward the ring, and vice versa).
            alignTo: 'labelLine',
            verticalAlign: 'middle',
            // A small deliberate gap between the line's end and the text —
            // slightly more than ECharts' own default (5) so it reads as an
            // intentional gap rather than the text touching the line.
            distanceToLabelLine: 6,
          },
          // One fixed geometry for every slice — no per-slice tiering. A
          // two-tier `length2` (short for "big" slices, long for "small"
          // ones) used to create a visible discontinuity: a mid-size slice
          // sandwiched between two others that `shiftY` had pushed apart (PG
          // at 5.8%, between "Other" and ERIC on /stocks market value) landed
          // on the *short* tier exactly where it needed the most room, so its
          // label crowded the ring while its neighbours sat comfortably
          // farther out. Uniform, generous values mean every label sits at
          // the same radial distance regardless of that slice's own size.
          labelLine: { show: true, length: 14, length2: 20, lineStyle: { color: tokens.baseline } },
          // Overlap is resolved by pie's OWN native pass (`avoidLabelOverlap`
          // above), never by dropping a label: `hideOverlap: false` here
          // opts out of ECharts' generic default (which would silently hide
          // the losing label of an overlapping pair) without turning on the
          // generic `moveOverlap` shift.
          //
          // `moveOverlap: 'shiftY'` was here and has been REMOVED. Read from
          // ECharts' own source (`chart/pie/labelLayout.js`,
          // `label/labelLayoutHelper.js`): pie's native `avoidLabelOverlap`
          // shifts a label AND recomputes `linePoints[1][1]`/`[2][1]` so the
          // guide line stays glued to it. The generic `labelLayout` pass
          // that `moveOverlap: 'shiftY'` triggers runs afterwards and only
          // mutates the label's y — it never touches the associated line.
          // Measured in the browser (SVG `getBoundingClientRect()`), this
          // was live damage, not a theoretical risk: on /crypto cost basis
          // (AMP ~100%, ANVL and ETH both ~0%) the two near-zero slices'
          // label blocks overlapped by ~2px, and on /stocks market value PG
          // and ERIC's label blocks overlapped by ~3px. (Both figures are
          // bounding-box measurements and overstate the damage: the two lines
          // *within* a single label overlap by ~4px by the same measure, so
          // nothing was ever colliding glyph-on-glyph — the labels were
          // crowded, not illegible.)
          //
          // Re-measured after the change: on /stocks all eight leader lines
          // render as proper 3-point elbows with 50-82px of clearance from
          // the ring, and each label block sits further from its neighbour
          // than from its own second line. The /crypto cost-basis corner is
          // improved but NOT clean — see the `minAngle` note above.
          labelLayout: { hideOverlap: false },
          // `percent` is deliberately NOT set here — ECharts computes its own
          // value-derived share and injects it into tooltip/label formatter
          // params as `percent`. The backend's own `percentageOfTotal` (used
          // for the HTML legend below) is kept separately in `legendRows()`.
          data: slots.map((s, i) => ({
            name: s.symbol,
            value: roundedValues[i],
            itemStyle: { color: s.color },
          })),
        },
      ],
    };
  });
}
