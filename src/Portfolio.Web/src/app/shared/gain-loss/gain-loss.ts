import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { MoneyPipe } from '../pipes/money.pipe';

/**
 * The three arrow glyphs, as Material Design's own 24dp paths.
 *
 * D18 — these used to be `<mat-icon>` ligatures (`arrow_upward`), where the
 * icon's *name* is literal text in the element and the font substitutes a
 * glyph for it. `aria-hidden` removed that text from the accessibility tree,
 * which fixed screen readers, but it stayed in raw `textContent`: extracting
 * the page's text still produced `arrow_upward+$218.09·+40.95%`. An SVG has no
 * text to leak, so the same markup now reads `+$218.09·+40.95%` however it is
 * scraped — and this component renders on every holdings row, every tile and
 * every bar label, so it was by far the biggest source of the noise.
 */
const ICON_PATHS = {
  up: 'M4 12l1.41 1.41L11 7.83V20h2V7.83l5.58 5.59L20 12l-8-8-8 8z',
  down: 'M20 12l-1.41-1.41L13 16.17V4h-2v12.17l-5.58-5.59L4 12l8 8 8-8z',
  flat: 'M19 13H5v-2h14v2z',
} as const;

/**
 * Gains and losses must be distinguishable without relying on colour alone
 * (CLAUDE.md / tracker.md, both charts and plain UI). This renders a signed
 * value with THREE independent cues: an explicit `+`/`-` sign in the text, an
 * up/down/flat arrow glyph, and the gain/loss colour token — so a colourblind
 * reader, a grayscale screenshot, or a screen reader (which reads the sign
 * and the icon's `aria-label`) all get the same answer.
 *
 * Colours come from `--ui-color-gain` / `--ui-color-loss` — the status
 * good/critical steps, never the categorical palette (see chart-theme.ts).
 */
@Component({
  selector: 'app-gain-loss',
  standalone: true,
  templateUrl: './gain-loss.html',
  styleUrl: './gain-loss.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class GainLoss {
  /** A monetary delta in USD. Pass `null` to show only a percent. */
  readonly amountUsd = input<number | null>(null);
  /** A percentage delta. Pass `null` to show only an amount. */
  readonly percent = input<number | null>(null);

  private readonly moneyPipe = new MoneyPipe();

  /** Sign decision prefers the amount when both are present — they always
   *  agree in sign in this app's data, but the amount is the primary figure. */
  private readonly signValue = computed(() => this.amountUsd() ?? this.percent() ?? 0);

  readonly isGain = computed(() => this.signValue() > 0);
  readonly isLoss = computed(() => this.signValue() < 0);

  /** D18 — an SVG path, not an icon-font ligature name. See ICON_PATHS. */
  readonly iconPath = computed(() => {
    const v = this.signValue();
    return v > 0 ? ICON_PATHS.up : v < 0 ? ICON_PATHS.down : ICON_PATHS.flat;
  });

  readonly iconLabel = computed(() => {
    const v = this.signValue();
    return v > 0 ? 'Up' : v < 0 ? 'Down' : 'Unchanged';
  });

  readonly signedAmount = computed(() => {
    const amount = this.amountUsd();
    if (amount === null) {
      return null;
    }
    const formatted = this.moneyPipe.transform(amount);
    // Intl's currency format already prefixes a negative amount with "-";
    // a positive one gets no sign by default, so add "+" explicitly.
    return amount > 0 ? `+${formatted}` : formatted;
  });

  readonly signedPercent = computed(() => {
    const percent = this.percent();
    if (percent === null) {
      return null;
    }
    const sign = percent > 0 ? '+' : '';
    return `${sign}${percent.toFixed(2)}%`;
  });
}
