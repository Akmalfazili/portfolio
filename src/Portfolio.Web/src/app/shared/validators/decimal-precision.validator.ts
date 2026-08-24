import { AbstractControl, ValidationErrors, ValidatorFn } from '@angular/forms';

/**
 * Fractional quantities and unit prices go up to 10 decimal places (matching
 * the backend's `decimal(28,10)` columns) and must stay inside IEEE-754
 * double's ~15-17 significant-digit envelope — see tracker.md D8. Past that
 * envelope, precision loss is *silent*: `12345678.1234567891` becomes
 * `12345678.12345679` before the value even leaves the browser, with no
 * error at any layer. This validator fails loudly at the input instead,
 * rather than leaving that to chance.
 *
 * Runs against the control's own numeric value — for a `type="number"` input
 * with `step="any"` that is already the post-parse double, so it cannot
 * recover digits the browser has already dropped, but it still catches every
 * case that matters here: a value that *still* carries more decimals or
 * significant digits than the backend column can distinguish gets flagged,
 * rather than silently rounding twice (once in the browser, once in SQL
 * Server).
 *
 * Deliberately does NOT use `Number.prototype.toString()`/`String(value)` —
 * that switches to exponential notation below 1e-6 (`String(0.0000000001)`
 * is `"1e-10"`, not `"0.0000000001"`), and a naive check for `"e"` in that
 * string would reject exactly the value the D8 probe proved the *backend*
 * accepts correctly: `0.0000000001` serialises to the wire as `1e-10` and
 * `System.Text.Json` binds it losslessly to `decimal`. Exponential notation
 * on the wire is a transport detail, not a reason to block the user from
 * typing a legitimate crypto-dust quantity. `toLocaleString` with a generous
 * `maximumFractionDigits` gives the same shortest round-trip digits without
 * ever switching to exponential form, at any magnitude this form cares about.
 */
const PRECISION_FORMAT_OPTIONS: Intl.NumberFormatOptions = { useGrouping: false, maximumFractionDigits: 20 };

export function decimalPrecisionValidator(maxDecimals: number, maxSignificantDigits = 15): ValidatorFn {
  return (control: AbstractControl<number | null>): ValidationErrors | null => {
    const value = control.value;
    if (value === null || value === undefined || (value as unknown) === '') {
      return null;
    }
    if (typeof value !== 'number' || !Number.isFinite(value)) {
      return null;
    }

    const raw = Math.abs(value).toLocaleString('en-US', PRECISION_FORMAT_OPTIONS);
    const [integerPart, fractionPart = ''] = raw.split('.');

    if (fractionPart.length > maxDecimals) {
      return { maxDecimals: { max: maxDecimals, actual: fractionPart.length } };
    }

    const significantDigits = `${integerPart}${fractionPart}`.replace(/^0+/, '').length || 1;
    if (significantDigits > maxSignificantDigits) {
      return { maxSignificantDigits: { max: maxSignificantDigits, actual: significantDigits } };
    }

    return null;
  };
}

/** Strictly greater than zero — `Validators.min(0)` would let a zero quantity through. */
export function positiveNumberValidator(): ValidatorFn {
  return (control: AbstractControl<number | null>): ValidationErrors | null => {
    const value = control.value;
    if (value === null || value === undefined || (value as unknown) === '') {
      return null;
    }
    return typeof value === 'number' && value > 0 ? null : { positive: true };
  };
}

/**
 * Zero or greater — for `pricePerUnit` only (D36). A free share, bonus issue
 * or scrip dividend legitimately prices at exactly 0, which the backend now
 * accepts (`TransactionService.ValidateCommon` moved from `<= 0` to `< 0`).
 * `quantity` must keep rejecting 0 and stays on `positiveNumberValidator()`
 * — do not swap this in there.
 *
 * `Validators.required` alone still guards against a genuinely blank field:
 * `Validators.required`'s `isEmptyInputValue` only checks null/undefined/
 * length, so a `0` value is "present" and passes it — required and
 * zero-permitting are not in tension here.
 */
export function nonNegativeNumberValidator(): ValidatorFn {
  return (control: AbstractControl<number | null>): ValidationErrors | null => {
    const value = control.value;
    if (value === null || value === undefined || (value as unknown) === '') {
      return null;
    }
    return typeof value === 'number' && value >= 0 ? null : { negative: true };
  };
}
