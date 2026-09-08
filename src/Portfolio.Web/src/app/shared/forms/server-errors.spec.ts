import { FormControl, FormGroup } from '@angular/forms';

import { applyServerErrors } from './server-errors';

interface Controls {
  quantity: FormControl<number | null>;
  pricePerUnit: FormControl<number | null>;
  notes: FormControl<string | null>;
}

const ALLOWED: (keyof Controls)[] = ['quantity', 'pricePerUnit'];

function makeForm(): FormGroup<Controls> {
  return new FormGroup<Controls>({
    quantity: new FormControl<number | null>(null),
    pricePerUnit: new FormControl<number | null>(null),
    notes: new FormControl<string | null>(null),
  });
}

describe('applyServerErrors', () => {
  it('sets the server error on a matching control and marks it touched', () => {
    const form = makeForm();

    const unmapped = applyServerErrors(form, ALLOWED, { quantity: ['Sell exceeds units held.'] });

    expect(form.controls.quantity.errors).toEqual({ server: 'Sell exceeds units held.' });
    expect(form.controls.quantity.touched).toBe(true);
    expect(unmapped).toEqual([]);
  });

  it('merges the server error alongside existing client-side errors rather than replacing them', () => {
    const form = makeForm();
    form.controls.quantity.setErrors({ positive: true });

    applyServerErrors(form, ALLOWED, { quantity: ['Must be greater than zero.'] });

    expect(form.controls.quantity.errors).toEqual({ positive: true, server: 'Must be greater than zero.' });
  });

  it('takes only the FIRST message for a field, as the field can show one line', () => {
    const form = makeForm();

    applyServerErrors(form, ALLOWED, { pricePerUnit: ['First problem.', 'Second problem.'] });

    expect(form.controls.pricePerUnit.errors).toEqual({ server: 'First problem.' });
  });

  it('returns messages for fields with no allowed control, leaving the form alone', () => {
    const form = makeForm();

    const unmapped = applyServerErrors(form, ALLOWED, {
      quantity: ['Bad quantity.'],
      // Present on the form but NOT in the allowed list — the list is the
      // dialog's contract with its request DTO, so this must not be mapped.
      notes: ['Bad notes.'],
      // Not a control at all.
      '': ['A general problem.'],
    });

    expect(unmapped).toEqual(['Bad notes.', 'A general problem.']);
    expect(form.controls.notes.errors).toBeNull();
    expect(form.controls.notes.touched).toBe(false);
  });

  it('falls back to a generic message for a field the server sent with no messages', () => {
    const form = makeForm();

    applyServerErrors(form, ALLOWED, { quantity: [] });

    expect(form.controls.quantity.errors).toEqual({ server: 'Invalid value.' });
  });

  it('applies every field in one pass', () => {
    const form = makeForm();

    const unmapped = applyServerErrors(form, ALLOWED, {
      quantity: ['Bad quantity.'],
      pricePerUnit: ['Bad price.'],
    });

    expect(form.controls.quantity.errors).toEqual({ server: 'Bad quantity.' });
    expect(form.controls.pricePerUnit.errors).toEqual({ server: 'Bad price.' });
    expect(form.controls.quantity.touched).toBe(true);
    expect(form.controls.pricePerUnit.touched).toBe(true);
    expect(unmapped).toEqual([]);
  });

  it('returns an empty array, not null, when there is nothing to surface', () => {
    expect(applyServerErrors(makeForm(), ALLOWED, {})).toEqual([]);
  });
});
