import { describeValidationError } from './describe-error';

describe('describeValidationError', () => {
  it('renders the shared codes with the wording the dialogs used before it moved', () => {
    expect(describeValidationError({ required: true }, 'Invalid value.')).toBe('Required.');
    expect(describeValidationError({ positive: true }, 'Invalid value.')).toBe(
      'Must be greater than zero.',
    );
    expect(describeValidationError({ negative: true }, 'Invalid value.')).toBe(
      'Cannot be negative.',
    );
    expect(describeValidationError({ min: { min: 0, actual: -1 } }, 'Invalid value.')).toBe(
      'Cannot be negative.',
    );
    expect(describeValidationError({ max: { max: 12, actual: 13 } }, 'Invalid value.')).toBe(
      'Must be 12 or less.',
    );
    expect(describeValidationError({ maxDecimals: { max: 10 } }, 'Invalid value.')).toBe(
      'No more than 10 decimal places.',
    );
    expect(describeValidationError({ maxSignificantDigits: { max: 28 } }, 'Invalid value.')).toBe(
      'Too precise to store reliably — 28 significant digits maximum.',
    );
  });

  it('returns the server message verbatim — the backend wording is more specific than any client string', () => {
    expect(describeValidationError({ server: 'Sell exceeds units held.' }, 'Invalid value.')).toBe(
      'Sell exceeds units held.',
    );
  });

  it('gives the server message top precedence over any client-side code on the same control', () => {
    const errors = { positive: true, maxDecimals: { max: 10 }, server: 'Sell exceeds units held.' };
    expect(describeValidationError(errors, 'Invalid value.')).toBe('Sell exceeds units held.');
  });

  it('gives required precedence over the value-shape codes — a blank field is not "too precise"', () => {
    expect(
      describeValidationError(
        { required: true, maxSignificantDigits: { max: 28 } },
        'Invalid value.',
      ),
    ).toBe('Required.');
  });

  it('prefers positive over the negative/min pair, matching the original chain', () => {
    expect(describeValidationError({ positive: true, min: { min: 0 } }, 'Invalid value.')).toBe(
      'Must be greater than zero.',
    );
  });

  it('uses the caller-supplied fallback for an unrecognised code', () => {
    expect(describeValidationError({ matDatepickerParse: true }, 'Invalid date.')).toBe(
      'Invalid date.',
    );
    expect(describeValidationError({}, 'Invalid date — it cannot be in the future.')).toBe(
      'Invalid date — it cannot be in the future.',
    );
  });

  it('falls through an empty-string server message rather than rendering blank text', () => {
    // Truthiness, exactly as the original `if (errors['server'])` chains did.
    expect(describeValidationError({ server: '', required: true }, 'Invalid value.')).toBe(
      'Required.',
    );
  });

  it('has no shared wording for pattern — it falls through unless the caller supplies one', () => {
    expect(
      describeValidationError({ pattern: { requiredPattern: '^[A-Za-z]{3}$' } }, 'Invalid value.'),
    ).toBe('Invalid value.');
    expect(
      describeValidationError({ pattern: { requiredPattern: '^[A-Za-z]{3}$' } }, 'Invalid value.', {
        pattern: () => 'A 3-letter ISO currency code, e.g. USD.',
      }),
    ).toBe('A 3-letter ISO currency code, e.g. USD.');
  });

  it('lets a caller reword a shared code in its shared precedence position (asset-form.dialog.ts min)', () => {
    const rangeMin = { min: (detail: { min: number }) => `Must be ${detail.min} or greater.` };

    expect(
      describeValidationError({ min: { min: 1, actual: 0 } }, 'Invalid value.', rangeMin),
    ).toBe('Must be 1 or greater.');
    // Still outranked by server and required, exactly as before.
    expect(
      describeValidationError({ min: { min: 1 }, required: true }, 'Invalid value.', rangeMin),
    ).toBe('Required.');
    expect(
      describeValidationError({ min: { min: 1 }, server: 'Nope.' }, 'Invalid value.', rangeMin),
    ).toBe('Nope.');
  });
});
