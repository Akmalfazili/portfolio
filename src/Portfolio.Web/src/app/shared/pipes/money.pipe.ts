import { Pipe, PipeTransform } from '@angular/core';

import { MoneyDisplayMode, formatMoney } from '../util/format-money';

export type { MoneyDisplayMode };

/**
 * Template-facing wrapper around `formatMoney` (`shared/util/format-money.ts`)
 * — see that function's doc comment for the sub-cent/price-mode rules this
 * pipe inherits verbatim. Non-template callers (chart formatters, other
 * plain functions) should call `formatMoney` directly rather than
 * constructing this pipe by hand.
 */
@Pipe({ name: 'money', standalone: true })
export class MoneyPipe implements PipeTransform {
  transform(
    value: number | string | null | undefined,
    currency = 'USD',
    mode: MoneyDisplayMode = 'total',
  ): string {
    return formatMoney(value, currency, mode);
  }
}
