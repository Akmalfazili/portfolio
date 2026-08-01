import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { NgxEchartsDirective } from 'ngx-echarts';
import type { EChartsCoreOption } from 'echarts/core';

import { MoneyPipe } from '../../../shared/pipes/money.pipe';
import { foldToOther, itemTooltip, legend, readChartTokens, seriesColor } from '../../../shared/charts/chart-theme';

export interface AllocationSlice {
  assetId: number;
  symbol: string;
  name: string;
  value: number;
  percent: number;
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

    return {
      tooltip: {
        ...itemTooltip(tokens),
        formatter: (params: unknown) => {
          const p = params as { name: string; value: number; percent: number };
          const money = new MoneyPipe().transform(p.value);
          return `${p.name}: ${money} (${p.percent.toFixed(1)}%)`;
        },
      },
      legend: { show: false }, // the HTML legend below the chart is the real one
      series: [
        {
          type: 'pie',
          radius: ['45%', '72%'], // donut — part-to-whole reads better with a hole for the total
          center: ['50%', '50%'],
          avoidLabelOverlap: true,
          itemStyle: {
            borderColor: tokens.surface,
            borderWidth: 2, // the surface-gap spacer between adjacent slices
          },
          label: {
            show: true,
            formatter: (params: unknown) => {
              const p = params as { percent: number; name: string };
              // Label selectively — only slices carrying real visual weight;
              // the legend + tooltip carry the rest (marks-and-anatomy.md).
              return p.percent >= 8 ? `${p.name}\n${p.percent.toFixed(0)}%` : '';
            },
            color: tokens.onSurfaceSecondary,
            fontFamily: 'var(--ui-font-family-sans)',
            fontSize: 11,
          },
          labelLine: { show: true, length: 8, length2: 8, lineStyle: { color: tokens.baseline } },
          // `percent` is deliberately NOT set here — ECharts computes its own
          // value-derived share and injects it into tooltip/label formatter
          // params as `percent`. The backend's own `percentageOfTotal` (used
          // for the HTML legend below) is kept separately in `legendRows()`.
          data: slots.map((s) => ({
            name: s.symbol,
            value: Math.round(s.value * 100) / 100,
            itemStyle: { color: s.color },
          })),
        },
      ],
    };
  });
}
