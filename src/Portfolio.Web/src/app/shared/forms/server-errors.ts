import { AbstractControl, FormGroup } from '@angular/forms';

/**
 * Maps a `400 ValidationProblemDetails.errors` bag onto a typed form's
 * controls, and hands back whatever could NOT be mapped.
 *
 * Verified live: `ValidationProblemDetails` keys its errors by the camelCase
 * request-body field name ("providerCoinId", "providerSymbol", "quantity"),
 * which matches every dialog's control names 1:1 — that is what makes the
 * direct lookup safe. Each dialog keeps its own `SERVER_ERROR_FIELDS` list and
 * passes it in as `allowedFields`: the list is that dialog's contract with its
 * own request DTO, not shared knowledge, so a field only ever reaches a
 * control the dialog has explicitly vouched for.
 *
 * Returning the unmapped messages rather than surfacing them here is
 * deliberate. All three current callers join them with `' '` into a
 * `serverError` signal, but that is a presentation decision belonging to the
 * dialog; this function's job ends at the form.
 *
 * @returns messages for fields that matched no allowed control, in the order
 *          the server sent them. Empty when everything was mapped.
 */
export function applyServerErrors<TControls extends { [K in keyof TControls]: AbstractControl }>(
  form: FormGroup<TControls>,
  allowedFields: readonly (keyof TControls)[],
  errors: Record<string, string[]>,
): string[] {
  const unmapped: string[] = [];
  // `FormGroup.controls` is declared through Angular's `ɵIsAny` helper, which
  // TypeScript will not let us index with a bare `keyof TControls`. The
  // narrowing is safe: the constraint above already says every property of
  // `TControls` is an `AbstractControl`.
  const controls = form.controls as TControls;

  for (const [field, messages] of Object.entries(errors)) {
    const message = messages[0] ?? 'Invalid value.';
    const controlName = allowedFields.find((name) => name === (field as keyof TControls));
    if (controlName) {
      const control: AbstractControl = controls[controlName];
      // Merged, never replaced — a server error sits alongside whatever
      // client-side errors the control already carries, and
      // `describeValidationError` gives `server` top precedence so the
      // server's own wording is what the user reads.
      control.setErrors({ ...control.errors, server: message });
      control.markAsTouched();
    } else {
      unmapped.push(message);
    }
  }

  return unmapped;
}
