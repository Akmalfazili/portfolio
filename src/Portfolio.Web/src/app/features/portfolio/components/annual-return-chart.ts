import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { NgxEchartsDirective } from 'ngx-echarts';
import type { EChartsCoreOption } from 'echarts/core';

import { AnnualReturnDto } from '../../../core/api/models';
import {
  MARK,
  axisTooltip,
  categoryAxis,
  gainLossColor,
  readChartTokens,
  valueAxis,
} from '../../../shared/charts/chart-theme';

/**
 * Year-on-year time-weighted return, stocks only (crypto keeps no history —
 * see tracker.md's crypto-scope decision; this component is never rendered
 * for the crypto section, not rendered-and-empty).
 *
 * A single series (one bar per year — "Annual return" is the whole story, so
 * no legend box is needed), diverging around a zero baseline: gain years
 * green, loss years red — the status good/critical steps, never a
 * categorical hue, since this is a valence, not an identity. Colour is never
 * the only cue: every bar is directly labelled with its own signed
 * percentage, so a grayscale render or a colourblind reader still gets the
 * sign from the text and the arrow-shaped position relative to the zero
 * line, not from hue alone.
 */
@Component({
  selector: 'app-annual-return-chart',
  standalone: true,
  imports: [NgxEchartsDirective],
  templateUrl: './annual-return-chart.html',
  styleUrl: './annual-return-chart.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AnnualReturnChart {
  readonly years = input.required<AnnualReturnDto[]>();

  readonly hasData = computed(() => this.years().length > 0);

  readonly options = computed<EChartsCoreOption>(() => {
    const tokens = readChartTokens();
    const sorted = [...this.years()].sort((a, b) => a.year - b.year);
    const categories = sorted.map((y) => String(y.year));
    const values = sorted.map((y) => y.timeWeightedReturnPercent);

    return {
      grid: { left: 48, right: 24, top: 24, bottom: 32, containLabel: true },
      tooltip: {
        ...axisTooltip(tokens),
        formatter: (params: unknown) => {
          const [p] = params as { name: string; value: number }[];
          const sign = p.value > 0 ? '+' : '';
          return `${p.name}: ${sign}${p.value.toFixed(2)}%`;
        },
      },
      xAxis: categoryAxis(tokens, categories),
      yAxis: valueAxis(tokens, {
        name: 'Time-weighted return',
        nameTextStyle: { color: tokens.onSurfaceMuted, fontSize: 11 },
        axisLabel: { formatter: '{value}%', color: tokens.onSurfaceMuted, fontSize: 11 },
      }),
      series: [
        {
          type: 'bar',
          // Value at the cap, per marks-and-anatomy.md — the outer end of
          // the bar away from the zero baseline, so a negative (below-zero)
          // year's label sits under its bar, not overlapping the axis.
          data: values.map((v) => ({
            value: Math.round(v * 100) / 100,
            itemStyle: {
              color: gainLossColor(tokens, v),
              borderRadius: v >= 0 ? MARK.barBorderRadius : [0, 0, 4, 4],
            },
            label: {
              show: true,
              position: (v >= 0 ? 'top' : 'bottom') as 'top' | 'bottom',
              formatter: () => {
                const sign = v > 0 ? '+' : '';
                return `${sign}${v.toFixed(1)}%`;
              },
              color: tokens.onSurfaceSecondary,
              fontFamily: 'var(--ui-font-family-sans)',
              fontSize: 11,
            },
          })),
          barMaxWidth: MARK.barMaxWidth,
          markLine: {
            silent: true,
            symbol: 'none',
            label: { show: false },
            lineStyle: { color: tokens.baseline, width: 1, type: 'solid' as const },
            data: [{ yAxis: 0 }],
          },
        },
      ],
    };
  });
}
