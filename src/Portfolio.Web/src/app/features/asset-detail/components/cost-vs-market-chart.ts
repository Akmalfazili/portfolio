import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { NgxEchartsDirective } from 'ngx-echarts';
import type { EChartsCoreOption } from 'echarts/core';

import { PerformancePointDto } from '../../../core/api/models';
import { MARK, axisTooltip, legend, readChartTokens, seriesColor, valueAxis } from '../../../shared/charts/chart-theme';
import { MoneyPipe } from '../../../shared/pipes/money.pipe';

export type PerformanceRange = '1M' | '3M' | '1Y' | 'All';

const RANGE_DAYS: Record<Exclude<PerformanceRange, 'All'>, number> = { '1M': 30, '3M': 90, '1Y': 365 };
const RANGES: PerformanceRange[] = ['1M', '3M', '1Y', 'All'];

/**
 * Cost basis vs market value, stocks only. Cost basis is rendered as a
 * genuine ECharts STEP series (`step: 'end'`) because it only moves on a
 * transaction's own trade date and holds flat in between — drawing it as a
 * smoothed interpolation would invent gradual cost changes on days nothing
 * was bought or sold, which is exactly the misrepresentation the tracker
 * calls out. Market value is a real daily line (`smooth: true`).
 *
 * The range selector (1M/3M/1Y/All) is anchored to the LAST point in the
 * series, not to today's wall-clock date — the series can lag behind "now"
 * (a stock priced a few days ago in a dev/backfill gap should still show a
 * populated "1M" rather than an empty chart because "today" has no data).
 */
@Component({
  selector: 'app-cost-vs-market-chart',
  standalone: true,
  imports: [NgxEchartsDirective],
  templateUrl: './cost-vs-market-chart.html',
  styleUrl: './cost-vs-market-chart.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CostVsMarketChart {
  readonly points = input.required<PerformancePointDto[]>();

  readonly ranges = RANGES;
  readonly range = signal<PerformanceRange>('All');

  readonly hasAnyData = computed(() => this.points().length > 0);

  private readonly sortedPoints = computed(() =>
    [...this.points()].sort((a, b) => a.date.localeCompare(b.date)),
  );

  /** Always includes at least the series' own last point — the range is
   *  anchored on it rather than on wall-clock "today", so no range choice can
   *  ever empty the chart while any point exists. */
  readonly filteredPoints = computed(() => {
    const all = this.sortedPoints();
    const activeRange = this.range();
    if (activeRange === 'All' || all.length === 0) {
      return all;
    }
    const lastDate = new Date(all[all.length - 1].date);
    const cutoff = new Date(lastDate);
    cutoff.setDate(cutoff.getDate() - RANGE_DAYS[activeRange]);
    const cutoffMs = cutoff.getTime();
    return all.filter((p) => new Date(p.date).getTime() >= cutoffMs);
  });

  setRange(next: PerformanceRange): void {
    this.range.set(next);
  }

  readonly options = computed<EChartsCoreOption>(() => {
    const tokens = readChartTokens();
    const points = this.filteredPoints();
    const marketColor = seriesColor(tokens, 0);
    const costColor = seriesColor(tokens, 1);

    const costData = points.map((p) => [p.date, Math.round(p.costBasisUsd * 100) / 100]);
    const marketData = points.map((p) => [p.date, Math.round(p.marketValueUsd * 100) / 100]);

    const endMarker = (color: string) => ({
      symbol: 'circle',
      showSymbol: false,
      symbolSize: (_value: unknown, params: { dataIndex: number }) =>
        params.dataIndex === points.length - 1 ? MARK.markerSize : 0,
      itemStyle: { color, borderColor: tokens.surface, borderWidth: MARK.surfaceGapPx },
    });

    return {
      grid: { left: 64, right: 56, top: 24, bottom: 48, containLabel: true },
      legend: legend(tokens, { top: 0, left: 0 }),
      tooltip: {
        ...axisTooltip(tokens),
        formatter: (params: unknown) => {
          const rows = params as { seriesName: string; value: [string, number]; color: string }[];
          const money = new MoneyPipe();
          const date = rows[0]?.value?.[0] ?? '';
          const lines = rows
            .map(
              (r) =>
                `<span style="display:inline-block;width:8px;height:2px;background:${r.color};margin-right:4px;vertical-align:middle"></span>` +
                `${r.seriesName}: <strong>${money.transform(r.value[1])}</strong>`,
            )
            .join('<br/>');
          return `${date}<br/>${lines}`;
        },
      },
      xAxis: {
        type: 'time' as const,
        axisLine: { lineStyle: { color: tokens.baseline, width: MARK.gridlineWidth } },
        axisTick: { show: false },
        axisLabel: { color: tokens.onSurfaceMuted, fontSize: 11 },
        splitLine: { show: false },
      },
      yAxis: valueAxis(tokens, {
        axisLabel: {
          color: tokens.onSurfaceMuted,
          fontSize: 11,
          formatter: (value: number) => new MoneyPipe().transform(value).replace(/\.00$/, ''),
        },
      }),
      series: [
        {
          name: 'Market value',
          type: 'line',
          smooth: true,
          lineStyle: { width: MARK.lineWidth, color: marketColor },
          areaStyle: { color: marketColor, opacity: MARK.areaOpacity },
          data: marketData,
          ...endMarker(marketColor),
          endLabel: {
            show: true,
            formatter: (params: unknown) => new MoneyPipe().transform((params as { value: [string, number] }).value[1]),
            color: tokens.onSurface,
            fontFamily: 'var(--ui-font-family-sans)',
            fontSize: 11,
          },
        },
        {
          name: 'Cost basis',
          type: 'line',
          // The one non-negotiable: cost basis only moves on a transaction's
          // own trade date, so it is a STEP series, never smoothed.
          step: 'end' as const,
          lineStyle: { width: MARK.lineWidth, color: costColor },
          data: costData,
          ...endMarker(costColor),
          endLabel: {
            show: true,
            formatter: (params: unknown) => new MoneyPipe().transform((params as { value: [string, number] }).value[1]),
            color: tokens.onSurface,
            fontFamily: 'var(--ui-font-family-sans)',
            fontSize: 11,
          },
        },
      ],
    };
  });
}
