import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { NgxEchartsDirective } from 'ngx-echarts';
import type { EChartsCoreOption } from 'echarts/core';

import { PerformancePointDto } from '../../../core/api/models';
import {
  MARK,
  axisTooltip,
  legend,
  readChartTokens,
  seriesColor,
  valueAxis,
} from '../../../shared/charts/chart-theme';
import { formatMoney } from '../../../shared/util/format-money';

export type PerformanceRange = '1M' | '3M' | '1Y' | 'All';

const RANGE_DAYS: Record<Exclude<PerformanceRange, 'All'>, number> = {
  '1M': 30,
  '3M': 90,
  '1Y': 365,
};
const RANGES: PerformanceRange[] = ['1M', '3M', '1Y', 'All'];
const MS_PER_DAY = 86_400_000;

/**
 * D19 — the x-axis tick format must track how much time the visible range
 * actually spans, not a fixed day-of-month format. "20 21 22 23 24" is fine
 * for a 5-day window and meaningless over 1Y/All, where the same day number
 * repeats across different months with nothing distinguishing them.
 *
 * Formatting is deliberately LOCAL, and the reason is easy to get backwards.
 * The series data passes the plain calendar strings ("YYYY-MM-DD") straight
 * to a `type: 'time'` axis, so it is ECharts — not the native `Date` parser —
 * that turns them into the timestamps this formatter receives, and ECharts
 * parses that shape as *local* midnight. Formatting those timestamps in UTC
 * subtracts the browser's offset and renders the whole axis one day early at
 * any positive offset: measured in Asia/Singapore (UTC+8), a series spanning
 * Jul 20–24 labelled itself "Jul 19 … Jul 23" while the tooltip — which reads
 * the raw string — correctly said Jul 20. Local parse, local format: the two
 * ends agree, and the label reads the intended calendar day in every timezone.
 *
 * Note this is invisible at zero or negative offsets (local midnight and UTC
 * fall on the same calendar day there), so it will not reproduce in a UTC CI
 * container — which is exactly how it shipped green the first time.
 */
function formatAxisTick(value: number, spanDays: number): string {
  const options: Intl.DateTimeFormatOptions = {};
  if (spanDays > 370) {
    // Multi-year span (a real "All" range) — day/month ticks would just
    // repeat across years with nothing to tell them apart.
    options.year = 'numeric';
  } else if (spanDays > 45) {
    // Roughly 3M and up, including the 1Y range — month is the meaningful
    // unit; year alongside it disambiguates a span that crosses New Year's.
    options.month = 'short';
    options.year = 'numeric';
  } else {
    // Day-level windows (1M and anything narrower) — month + day.
    options.month = 'short';
    options.day = 'numeric';
  }
  return new Date(value).toLocaleDateString('en-US', options);
}

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

    const spanDays =
      points.length > 1
        ? (new Date(points[points.length - 1].date).getTime() -
            new Date(points[0].date).getTime()) /
          MS_PER_DAY
        : 0;

    // D15 — the two end labels collide into an illegible blob whenever cost
    // basis and market value are close at the right edge, which is the
    // *normal* case for a recently-opened position, not an edge case. Detect
    // it relative to the visible value range (not a fixed dollar amount, so
    // it still works for a $5 ANVL position and a $50,000 stock position
    // alike) and nudge the two labels apart vertically — the greater value's
    // label above its line, the lesser one's below, so the reader still
    // recovers the order without the text overlapping.
    const lastMarket = marketData.at(-1)?.[1] as number | undefined;
    const lastCost = costData.at(-1)?.[1] as number | undefined;
    const allValues = [...marketData, ...costData].map((d) => d[1] as number);
    const valueSpan = allValues.length > 0 ? Math.max(...allValues) - Math.min(...allValues) : 0;
    const labelsCollide =
      lastMarket !== undefined &&
      lastCost !== undefined &&
      Math.abs(lastMarket - lastCost) <= valueSpan * 0.08;
    const marketOnTop = (lastMarket ?? 0) >= (lastCost ?? 0);
    const LABEL_NUDGE = 14;
    const marketLabelOffset: [number, number] = labelsCollide
      ? [0, marketOnTop ? -LABEL_NUDGE : LABEL_NUDGE]
      : [0, 0];
    const costLabelOffset: [number, number] = labelsCollide
      ? [0, marketOnTop ? LABEL_NUDGE : -LABEL_NUDGE]
      : [0, 0];

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
          const date = rows[0]?.value?.[0] ?? '';
          const lines = rows
            .map(
              (r) =>
                `<span style="display:inline-block;width:8px;height:2px;background:${r.color};margin-right:4px;vertical-align:middle"></span>` +
                `${r.seriesName}: <strong>${formatMoney(r.value[1])}</strong>`,
            )
            .join('<br/>');
          return `${date}<br/>${lines}`;
        },
      },
      xAxis: {
        type: 'time' as const,
        axisLine: { lineStyle: { color: tokens.baseline, width: MARK.gridlineWidth } },
        axisTick: { show: false },
        axisLabel: {
          color: tokens.onSurfaceMuted,
          fontSize: 11,
          formatter: (value: number) => formatAxisTick(value, spanDays),
        },
        splitLine: { show: false },
      },
      yAxis: valueAxis(tokens, {
        axisLabel: {
          color: tokens.onSurfaceMuted,
          fontSize: 11,
          formatter: (value: number) => formatMoney(value).replace(/\.00$/, ''),
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
            formatter: (params: unknown) =>
              formatMoney((params as { value: [string, number] }).value[1]),
            color: tokens.onSurface,
            fontFamily: 'var(--ui-font-family-sans)',
            fontSize: 11,
            offset: marketLabelOffset,
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
            formatter: (params: unknown) =>
              formatMoney((params as { value: [string, number] }).value[1]),
            color: tokens.onSurface,
            fontFamily: 'var(--ui-font-family-sans)',
            fontSize: 11,
            offset: costLabelOffset,
          },
        },
      ],
    };
  });
}
