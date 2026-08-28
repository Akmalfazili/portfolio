import { Pipe, PipeTransform } from '@angular/core';

/**
 * `'total'` (the default) is a genuine monetary amount — a cost basis, a
 * market value, an income figure — where two decimal places is the
 * conventional and sufficient precision once the value clears one cent.
 * `'price'` is a **per-unit rate** (an average cost, a quote, a dividend's
 * amount-per-share) — the same distinction the backend draws between
 * `DisplayRounding.Money` (4 dp, for totals) and `DisplayRounding.Price`
 * (10 dp, for per-unit values): a unit price is not an amount you'd ever sum
 * on its own, and rounding it to 2 dp can silently render a different number
 * than the one underneath (0.103 and 0.100 both read "0.10"), the same
 * defect class `HoldingDto.averageCostUsd`'s own doc comment warns about.
 */
export type MoneyDisplayMode = 'total' | 'price';

/**
 * Formats a money value without ever silently flooring a sub-cent price to
 * `$0.00` — the exact bug `DecimalPipe`'s default 2-decimal format has.
 *
 * In `'total'` mode (the default, unchanged from before): at or above one
 * cent, this renders the conventional two-decimal money format ($333.02) —
 * the difference between that and an underlying `333.019989` is
 * sub-hundredth-of-a-cent and immaterial to read. Below one cent (the ANVL
 * case, ≈$0.0005326), two decimals would round to `$0.00` and hide the value
 * entirely, so precision extends up to 8 decimal places, trimmed to only the
 * digits the value actually needs (no trailing-zero noise) via
 * `Intl.NumberFormat`'s own min/max fraction digit handling.
 *
 * In `'price'` mode, that same trimmed-precision treatment always applies
 * (up to 10 dp, matching the backend's own per-unit precision), never only
 * below one cent — a per-share rate of 0.103 must render as "0.103", not
 * silently coerced to "0.10", which is a different number rendered as if it
 * were the real one. `0.1` still renders as "0.10", not
 * "0.1000000000" — trailing zeros beyond the two-decimal floor are trimmed,
 * never padded out to the full 10 dp.
 *
 * Never rounds a value before it is sent back to the API — this pipe is for
 * display only.
 */
@Pipe({ name: 'money', standalone: true })
export class MoneyPipe implements PipeTransform {
  transform(value: number | string | null | undefined, currency = 'USD', mode: MoneyDisplayMode = 'total'): string {
    if (value === null || value === undefined || value === '') {
      return '—';
    }
    const amount = typeof value === 'string' ? Number(value) : value;
    if (Number.isNaN(amount)) {
      return '—';
    }

    const isSubCent = amount !== 0 && Math.abs(amount) < 0.01;
    const maximumFractionDigits = mode === 'price' ? 10 : isSubCent ? 8 : 2;

    return new Intl.NumberFormat('en-US', {
      style: 'currency',
      currency,
      minimumFractionDigits: 2,
      maximumFractionDigits,
    }).format(amount);
  }
}
