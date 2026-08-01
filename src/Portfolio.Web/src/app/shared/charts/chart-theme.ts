// The single shared setup every ECharts config (pie, bar, line) reads from —
// so the three chart types read as one system rather than three separate
// colour/spacing decisions. Built per the `dataviz` skill's method:
// categorical hues in fixed order, gain/loss on the status good/critical
// steps (never the diverging pair — gain/loss has an inherent positive/
// negative valence, not a neutral polarity), thin marks, hairline recessive
// grid, and a legend/tooltip that never colours text.
//
// ECharts option objects need real colour values, not `var(--ui-…)`
// references — `readChartTokens()` resolves the CSS custom properties off
// the live DOM at chart-build time (so light/dark and the per-section accent
// all resolve correctly), falling back to the light-mode defaults from
// `ui.tokens.scss` when running outside a browser that has the stylesheet
// loaded (e.g. a unit test with no global styles applied). The fallback
// values are kept identical to that file's `:root` block on purpose — the
// source of truth is not duplicated, it is mirrored for the one place that
// cannot read a CSS variable at all.

/** Mirrors `ui.tokens.scss`'s light-mode `:root` block — see file header. */
const FALLBACK_LIGHT = {
  surface: '#fcfcfb',
  surfaceAlt: '#f9f9f7',
  onSurface: '#0b0b0b',
  onSurfaceSecondary: '#52514e',
  onSurfaceMuted: '#898781',
  border: 'rgba(11, 11, 11, 0.1)',
  gridline: '#e1e0d9',
  baseline: '#c3c2b7',
  gain: '#006300',
  loss: '#d03b3b',
  series: ['#2a78d6', '#eb6834', '#1baf7a', '#eda100', '#e87ba4', '#008300', '#4a3aa7', '#e34948'],
} as const;

export interface ChartTokens {
  surface: string;
  surfaceAlt: string;
  onSurface: string;
  onSurfaceSecondary: string;
  onSurfaceMuted: string;
  border: string;
  gridline: string;
  baseline: string;
  /** Status "good" step — gain, never the categorical or diverging palette. */
  gain: string;
  /** Status "critical" step — loss. */
  loss: string;
  /** The 8 fixed categorical hues, in order. Never cycle past index 7 —
   *  fold a 9th+ series into "Other" (see `foldToOther`) instead. */
  series: readonly string[];
}

function readVar(style: CSSStyleDeclaration, name: string, fallback: string): string {
  const value = style.getPropertyValue(name)?.trim();
  return value ? value : fallback;
}

/** Resolves the live design tokens off the DOM — call this at chart-build
 *  time (inside a `computed()`), never cache it module-wide, so a light/dark
 *  toggle or a stock/crypto section change is picked up on the next render. */
export function readChartTokens(root: HTMLElement = document.documentElement): ChartTokens {
  const style = getComputedStyle(root);
  return {
    surface: readVar(style, '--ui-color-surface', FALLBACK_LIGHT.surface),
    surfaceAlt: readVar(style, '--ui-color-surface-alt', FALLBACK_LIGHT.surfaceAlt),
    onSurface: readVar(style, '--ui-color-on-surface', FALLBACK_LIGHT.onSurface),
    onSurfaceSecondary: readVar(style, '--ui-color-on-surface-secondary', FALLBACK_LIGHT.onSurfaceSecondary),
    onSurfaceMuted: readVar(style, '--ui-color-on-surface-muted', FALLBACK_LIGHT.onSurfaceMuted),
    border: readVar(style, '--ui-color-border', FALLBACK_LIGHT.border),
    gridline: readVar(style, '--ui-color-gridline', FALLBACK_LIGHT.gridline),
    baseline: readVar(style, '--ui-color-baseline', FALLBACK_LIGHT.baseline),
    gain: readVar(style, '--ui-color-gain', FALLBACK_LIGHT.gain),
    loss: readVar(style, '--ui-color-loss', FALLBACK_LIGHT.loss),
    series: Array.from({ length: 8 }, (_, i) =>
      readVar(style, `--ui-chart-series-${i + 1}`, FALLBACK_LIGHT.series[i]),
    ),
  };
}

// ---------------------------------------------------------------------------
// Mark specs — fixed across every chart (marks-and-anatomy.md)
// ---------------------------------------------------------------------------
export const MARK = {
  lineWidth: 2,
  barMaxWidth: 24,
  barBorderRadius: [4, 4, 0, 0] as [number, number, number, number],
  markerSize: 8, // r >= 4
  areaOpacity: 0.1,
  surfaceGapPx: 2,
  gridlineWidth: 1,
} as const;

/** The fixed categorical hue for slot `index` (0-based), never past 7 —
 *  callers must fold a 9th+ series into "Other" before reaching this. */
export function seriesColor(tokens: ChartTokens, index: number): string {
  return tokens.series[index % tokens.series.length];
}

/** Gain is green, loss is red — the status good/critical steps, never the
 *  categorical or diverging palette (gain/loss is a valence, not identity or
 *  a neutral two-pole scale). Zero counts as a (non-loss) flat result. */
export function gainLossColor(tokens: ChartTokens, value: number): string {
  return value < 0 ? tokens.loss : tokens.gain;
}

/** Caps a categorical series at 8 by folding the tail into a single "Other"
 *  bucket — never generates a 9th hue (anti-patterns.md: "cycling / generating
 *  hues past 8"). Assumes `items` is already sorted by descending value. */
export function foldToOther<T extends { value: number }>(
  items: readonly T[],
  makeOther: (rest: readonly T[]) => T,
  max = 8,
): T[] {
  if (items.length <= max) {
    return [...items];
  }
  const head = items.slice(0, max - 1);
  const tail = items.slice(max - 1);
  return [...head, makeOther(tail)];
}

/** Text tokens only — labels, axis text and legend never wear a series
 *  colour (marks-and-anatomy.md: "text never wears the data color"). */
export function textStyle(tokens: ChartTokens, role: 'primary' | 'secondary' | 'muted' = 'secondary') {
  const color =
    role === 'primary' ? tokens.onSurface : role === 'muted' ? tokens.onSurfaceMuted : tokens.onSurfaceSecondary;
  return { color, fontFamily: 'var(--ui-font-family-sans)' };
}

/** Shared value-axis spec: hairline solid gridlines, recessive axis line,
 *  muted tick labels — never dashed (anti-patterns.md). */
export function valueAxis(tokens: ChartTokens, extra: Record<string, unknown> = {}) {
  return {
    type: 'value',
    axisLine: { show: false },
    axisTick: { show: false },
    axisLabel: { ...textStyle(tokens, 'muted'), fontSize: 11 },
    splitLine: { lineStyle: { color: tokens.gridline, width: MARK.gridlineWidth, type: 'solid' } },
    ...extra,
  };
}

/** Shared category-axis spec: recessive hairline axis line, no gridlines
 *  (categories don't need a horizontal grid), muted tick labels. */
export function categoryAxis(tokens: ChartTokens, data: string[], extra: Record<string, unknown> = {}) {
  return {
    type: 'category',
    data,
    axisLine: { lineStyle: { color: tokens.baseline, width: MARK.gridlineWidth } },
    axisTick: { show: false },
    axisLabel: { ...textStyle(tokens, 'muted'), fontSize: 11 },
    splitLine: { show: false },
    ...extra,
  };
}

/** Axis-triggered tooltip with a crosshair — for line/bar-over-time charts
 *  where the reader aims at an X position, not a single mark. */
export function axisTooltip(tokens: ChartTokens) {
  return {
    trigger: 'axis' as const,
    axisPointer: { type: 'line' as const, lineStyle: { color: tokens.baseline, type: 'solid' as const } },
    backgroundColor: tokens.surface,
    borderColor: tokens.border,
    borderWidth: 1,
    textStyle: { color: tokens.onSurface, fontFamily: 'var(--ui-font-family-sans)' },
    extraCssText: 'box-shadow: var(--ui-elevation-2);',
  };
}

/** Item-triggered tooltip — for bar/pie/cell charts where the mark itself is
 *  the hit target (no crosshair, per interaction.md). */
export function itemTooltip(tokens: ChartTokens) {
  return {
    trigger: 'item' as const,
    backgroundColor: tokens.surface,
    borderColor: tokens.border,
    borderWidth: 1,
    textStyle: { color: tokens.onSurface, fontFamily: 'var(--ui-font-family-sans)' },
    extraCssText: 'box-shadow: var(--ui-elevation-2);',
  };
}

/** Legend text always in a text token, never a series colour — identity
 *  comes from the coloured mark beside it. `icon` is deliberately left to
 *  ECharts' own per-series default (a short line stroke for a line series, a
 *  filled rect for a bar/pie) — "legends mirror the mark: rect for
 *  bars/areas, line for lines" (marks-and-anatomy.md). Pass `icon` in
 *  `extra` only if a chart genuinely needs to override that. */
export function legend(tokens: ChartTokens, extra: Record<string, unknown> = {}) {
  return {
    textStyle: { ...textStyle(tokens, 'secondary'), fontSize: 12 },
    itemWidth: 18,
    itemHeight: 10,
    ...extra,
  };
}
