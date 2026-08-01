import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

import { MoneyPipe } from '../pipes/money.pipe';

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
  imports: [MatIconModule],
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

  readonly icon = computed(() => {
    const v = this.signValue();
    return v > 0 ? 'arrow_upward' : v < 0 ? 'arrow_downward' : 'remove';
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
