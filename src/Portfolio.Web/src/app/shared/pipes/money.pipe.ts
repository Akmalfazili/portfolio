import { Pipe, PipeTransform } from '@angular/core';

/**
 * Formats a USD money value without ever silently flooring a sub-cent price
 * to `$0.00` — the exact bug `DecimalPipe`'s default 2-decimal format has.
 *
 * At or above one cent, this renders the conventional two-decimal money
 * format ($333.02) — the difference between that and an underlying
 * `333.019989` is sub-hundredth-of-a-cent and immaterial to read. Below one
 * cent (the ANVL case, ≈$0.0005326), two decimals would round to `$0.00` and
 * hide the value entirely, so precision extends up to 8 decimal places,
 * trimmed to only the digits the value actually needs (no trailing-zero
 * noise) via `Intl.NumberFormat`'s own min/max fraction digit handling.
 *
 * Never rounds a value before it is sent back to the API — this pipe is for
 * display only.
 */
@Pipe({ name: 'money', standalone: true })
export class MoneyPipe implements PipeTransform {
  transform(value: number | string | null | undefined, currency = 'USD'): string {
    if (value === null || value === undefined || value === '') {
      return '—';
    }
    const amount = typeof value === 'string' ? Number(value) : value;
    if (Number.isNaN(amount)) {
      return '—';
    }

    const isSubCent = amount !== 0 && Math.abs(amount) < 0.01;

    return new Intl.NumberFormat('en-US', {
      style: 'currency',
      currency,
      minimumFractionDigits: 2,
      maximumFractionDigits: isSubCent ? 8 : 2,
    }).format(amount);
  }
}
