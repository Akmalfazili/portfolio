import { ValidationErrors } from '@angular/forms';

/**
 * Validator codes this helper knows how to word, in the order they are
 * consulted. The order is the union of the three form dialogs' original
 * hand-written `describeError` chains, and it is load-bearing: `server` must
 * outrank everything (the backend's own wording is more specific than any
 * generic client message), and `required` must outrank the value-shape codes
 * (telling someone a blank field is "too precise" is nonsense).
 */
export type ValidationErrorCode =
  | 'server'
  | 'required'
  | 'pattern'
  | 'positive'
  | 'negative'
  | 'min'
  | 'max'
  | 'maxDecimals'
  | 'maxSignificantDigits';

const PRECEDENCE: readonly ValidationErrorCode[] = [
  'server',
  'required',
  'pattern',
  'positive',
  'negative',
  'min',
  'max',
  'maxDecimals',
  'maxSignificantDigits',
];

/**
 * Renders one validator's error detail into user-facing copy. The detail is
 * whatever the validator put in the errors bag — `true` for `required`,
 * `{ max }` for `maxDecimals`, the message string itself for `server`.
 */
export type ValidationMessage = (detail: never) => string;

/** Per-code copy, overriding or extending {@link SHARED_MESSAGES}. */
export type ValidationMessages = Partial<Record<ValidationErrorCode, ValidationMessage>>;

/**
 * Shared wording. Every string here is byte-identical to what the dialogs
 * rendered before it moved.
 *
 * Note `min` and `max` are deliberately NOT a matching pair. `min` arrives
 * from `Validators.min(0)` as a sign check in the transaction and
 * zakat-payment dialogs, so its shared wording is the same "Cannot be
 * negative." as the `negative` code beside it. `asset-form.dialog.ts` uses
 * `min`/`max` as a genuine range (month 1-12, day 1-31) and therefore
 * overrides `min`; `max` has only that one caller, so the range wording lives
 * here. `pattern` has no shared default at all — its only caller words it per
 * control — and simply falls through to the caller's fallback, exactly as an
 * unhandled code always did.
 */
const SHARED_MESSAGES: ValidationMessages = {
  server: (detail: string) => detail,
  required: () => 'Required.',
  positive: () => 'Must be greater than zero.',
  negative: () => 'Cannot be negative.',
  min: () => 'Cannot be negative.',
  max: (detail: { max: number }) => `Must be ${detail.max} or less.`,
  maxDecimals: (detail: { max: number }) => `No more than ${detail.max} decimal places.`,
  maxSignificantDigits: (detail: { max: number }) =>
    `Too precise to store reliably — ${detail.max} significant digits maximum.`,
};

/**
 * The shared half of the three dialogs' `describeError`. Each dialog keeps its
 * own thin wrapper, because what stays behind is genuinely its own:
 *
 * - `fallback` — the last-resort string, which differs per dialog and per
 *   control ("Invalid date." for a trade date, "Invalid date — it cannot be in
 *   the future." for a zakat payment date, "Invalid value." otherwise).
 * - `overrides` — per-code copy a dialog wants worded differently, evaluated
 *   in the shared precedence position. Overriding rather than unifying is the
 *   point: no user-visible string may change just because the code behind it
 *   was deduplicated.
 */
export function describeValidationError(
  errors: ValidationErrors,
  fallback: string,
  overrides: ValidationMessages = {},
): string {
  for (const code of PRECEDENCE) {
    const detail: unknown = errors[code];
    // Truthiness, not `!= null` — matches the original `if (errors['server'])`
    // chains, so an empty-string server message still falls through to the
    // next code rather than rendering as blank text.
    if (!detail) {
      continue;
    }
    const message = overrides[code] ?? SHARED_MESSAGES[code];
    if (message) {
      return (message as (d: unknown) => string)(detail);
    }
  }

  return fallback;
}
