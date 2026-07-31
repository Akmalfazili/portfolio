import { Pipe, PipeTransform } from '@angular/core';

/**
 * Formats a fractional share/token quantity — up to 10 decimal places
 * (matching the backend's `decimal(28,10)` columns), trimmed to only the
 * digits the value actually needs so `1000000.0000000000` reads as
 * `1,000,000`, not `1,000,000.0000000000`. Currency-agnostic: this is a unit
 * count (shares, coins), never a money amount — use `MoneyPipe` for that.
 *
 * Never rounds — `maximumFractionDigits` is a display cap, not a rounding
 * instruction to apply before a value is sent back to the API.
 */
@Pipe({ name: 'quantity', standalone: true })
export class QuantityPipe implements PipeTransform {
  transform(value: number | string | null | undefined, maxDecimals = 10): string {
    if (value === null || value === undefined || value === '') {
      return '—';
    }
    const amount = typeof value === 'string' ? Number(value) : value;
    if (Number.isNaN(amount)) {
      return '—';
    }

    return new Intl.NumberFormat('en-US', {
      minimumFractionDigits: 0,
      maximumFractionDigits: maxDecimals,
    }).format(amount);
  }
}
