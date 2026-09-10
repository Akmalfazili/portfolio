import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HttpErrorResponse } from '@angular/common/http';
import {
  AbstractControl,
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  ValidationErrors,
  ValidatorFn,
  Validators,
} from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';

import { AssetsApi } from '../../core/api/assets.api';
import {
  AssetClass,
  AssetDto,
  CreateAssetRequest,
  QuoteProviderKind,
  ValidationProblemDetails,
} from '../../core/api/models';
import { describeValidationError } from '../../shared/forms/describe-error';
import { applyServerErrors } from '../../shared/forms/server-errors';

export type AssetFormDialogData = { mode: 'create' } | { mode: 'edit'; asset: AssetDto };

export type AssetFormDialogResult = { kind: 'saved'; asset: AssetDto };

interface AssetFormControls {
  symbol: FormControl<string>;
  name: FormControl<string>;
  assetClass: FormControl<AssetClass>;
  exchange: FormControl<string | null>;
  currency: FormControl<string>;
  quoteProviderKind: FormControl<QuoteProviderKind>;
  providerSymbol: FormControl<string | null>;
  providerCoinId: FormControl<string | null>;
  fiscalYearEndMonth: FormControl<number | null>;
  fiscalYearEndDay: FormControl<number | null>;
}

/**
 * Field names in `ValidationProblemDetails.errors` line up 1:1 with the
 * request DTO (and so with this form's control names) — same convention as
 * `TransactionFormDialog`'s `SERVER_ERROR_FIELDS`. This is deliberately the
 * ONLY place D23's provider-routing rule is enforced: this form guides the
 * user toward the right shape (help text, dynamic identifier field, the
 * credit-cost warning) but never blocks submit on its own copy of the rule —
 * the server's `400` response is what's authoritative, per tracker.md's D24
 * instruction not to reimplement AssetService.CreateAsync client-side.
 */
const SERVER_ERROR_FIELDS: (keyof AssetFormControls)[] = [
  'symbol',
  'name',
  'assetClass',
  'exchange',
  'currency',
  'quoteProviderKind',
  'providerSymbol',
  'providerCoinId',
  'fiscalYearEndMonth',
  'fiscalYearEndDay',
];

/**
 * zakat.md §3.1 — "both set or both null" is enforced client-side too, not
 * only relied on server-side, so the pair mismatch surfaces before a round
 * trip. Reads the counterpart via `control.parent` (set once the control is
 * attached to the FormGroup below) rather than closing over the sibling
 * control directly, since both controls are constructed in the same object
 * literal and neither exists yet when the other's validator is built.
 */
function fiscalYearEndPairValidator(counterpartName: keyof AssetFormControls): ValidatorFn {
  return (control: AbstractControl<number | null>): ValidationErrors | null => {
    const hasValue = (value: unknown): boolean =>
      value !== null && value !== undefined && value !== '';
    if (hasValue(control.value)) {
      return null;
    }
    const counterpart = control.parent?.get(counterpartName as string);
    return counterpart && hasValue(counterpart.value) ? { fiscalYearEndPair: true } : null;
  };
}

/**
 * Create/edit form for a tracked asset (D24 + zakat.md §9). Crypto locks its
 * provider to CoinGecko and its fiscal-year-end fields to null+disabled —
 * crypto has no financial year (zakat.md §2.3/§3.1), so guessing one would
 * produce a confident wrong zakat figure with nothing downstream able to
 * detect it.
 *
 * Edit mode (new — there was previously no way to change a tracked asset
 * after creation) deliberately narrows what can change: identity and
 * provider-routing fields are shown for context but disabled, since altering
 * them has real consequences elsewhere (which provider gets billed a
 * credit, what a historical price series even means) that are out of scope
 * for what this dialog exists to let someone fix. Only `name`, `exchange`
 * and the two fiscal-year-end fields are editable. `PUT /api/assets/{id}` is
 * still a full replace, so submit always resends every field — the disabled
 * ones just carry the asset's existing value through unchanged, the same
 * pattern `AssetManagementPage.toggleActive` already uses for `isActive`.
 */
@Component({
  selector: 'app-asset-form-dialog',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatDialogModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSelectModule,
  ],
  templateUrl: './asset-form.dialog.html',
  styleUrl: './asset-form.dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AssetFormDialog {
  readonly data = inject<AssetFormDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<AssetFormDialog, AssetFormDialogResult>);
  private readonly assetsApi = inject(AssetsApi);
  private readonly destroyRef = inject(DestroyRef);

  readonly isEdit = this.data.mode === 'edit';

  private readonly existing: AssetDto | null = this.data.mode === 'edit' ? this.data.asset : null;

  readonly title = this.existing ? `Edit ${this.existing.symbol}` : 'Track a new asset';

  readonly submitting = signal(false);
  readonly serverError = signal<string | null>(null);

  readonly form: FormGroup<AssetFormControls> = new FormGroup<AssetFormControls>({
    symbol: new FormControl<string>(this.existing?.symbol ?? '', {
      nonNullable: true,
      validators: [Validators.required],
    }),
    name: new FormControl<string>(this.existing?.name ?? '', {
      nonNullable: true,
      validators: [Validators.required],
    }),
    assetClass: new FormControl<AssetClass>(this.existing?.assetClass ?? 'Stock', {
      nonNullable: true,
      validators: [Validators.required],
    }),
    exchange: new FormControl<string | null>(this.existing?.exchange ?? null),
    currency: new FormControl<string>(this.existing?.currency ?? 'USD', {
      nonNullable: true,
      validators: [Validators.required, Validators.pattern(/^[A-Za-z]{3}$/)],
    }),
    quoteProviderKind: new FormControl<QuoteProviderKind>(
      this.existing?.quoteProviderKind ?? 'TwelveData',
      {
        nonNullable: true,
        validators: [Validators.required],
      },
    ),
    providerSymbol: new FormControl<string | null>(this.existing?.providerSymbol ?? null),
    providerCoinId: new FormControl<string | null>(this.existing?.providerCoinId ?? null),
    // zakat.md §3.1 — recurring month/day, not a stored date. Range-validated
    // here (1-12 / 1-31); the exact "which day/month combinations are a real
    // fiscal year end" rule (29 Feb allowed, 2/30 and 4/31 rejected) is left
    // to the server, same division of labour as D23's provider-routing rule.
    fiscalYearEndMonth: new FormControl<number | null>(this.existing?.fiscalYearEndMonth ?? null, {
      validators: [
        Validators.min(1),
        Validators.max(12),
        fiscalYearEndPairValidator('fiscalYearEndDay'),
      ],
    }),
    fiscalYearEndDay: new FormControl<number | null>(this.existing?.fiscalYearEndDay ?? null, {
      validators: [
        Validators.min(1),
        Validators.max(31),
        fiscalYearEndPairValidator('fiscalYearEndMonth'),
      ],
    }),
  });

  readonly assetClassValue = signal<AssetClass>(this.existing?.assetClass ?? 'Stock');
  readonly providerValue = signal<QuoteProviderKind>(
    this.existing?.quoteProviderKind ?? 'TwelveData',
  );

  readonly isCrypto = computed(() => this.assetClassValue() === 'Crypto');
  readonly usesCoinId = computed(() => this.providerValue() === 'CoinGecko');
  readonly showsCreditWarning = computed(
    () => !this.isEdit && this.providerValue() === 'TwelveData',
  );

  readonly identifierHint = computed(() => {
    switch (this.providerValue()) {
      case 'TwelveData':
        return "Plain ticker, e.g. AAPL. Twelve Data's free tier cannot serve SGX at all.";
      case 'Yahoo':
        return 'Ticker with the .SI suffix for SGX, e.g. Z74.SI.';
      case 'CoinGecko':
        return 'CoinGecko\'s own coin ID, not the ticker — e.g. "ethereum", not "ETH".';
    }
  });

  constructor() {
    if (this.isEdit) {
      // Identity and provider-routing stay visible for context but are not
      // editable here — see the class doc comment for why.
      this.form.controls.symbol.disable({ emitEvent: false });
      this.form.controls.assetClass.disable({ emitEvent: false });
      this.form.controls.currency.disable({ emitEvent: false });
      this.form.controls.quoteProviderKind.disable({ emitEvent: false });
      this.form.controls.providerSymbol.disable({ emitEvent: false });
      this.form.controls.providerCoinId.disable({ emitEvent: false });
    }

    this.form.controls.assetClass.valueChanges.subscribe((assetClass) => {
      this.assetClassValue.set(assetClass);
      if (assetClass === 'Crypto') {
        this.form.controls.quoteProviderKind.setValue('CoinGecko');
        this.form.controls.quoteProviderKind.disable({ emitEvent: false });
        this.form.controls.currency.setValue('USD');
        // Crypto has no financial year (zakat.md §2.3) — cleared AND
        // disabled, same "the field that no longer applies must be cleared,
        // not just hidden" rule as providerSymbol/providerCoinId below, so a
        // stale value from a previous Stock selection is never submitted.
        this.form.controls.fiscalYearEndMonth.setValue(null);
        this.form.controls.fiscalYearEndDay.setValue(null);
        this.form.controls.fiscalYearEndMonth.disable({ emitEvent: false });
        this.form.controls.fiscalYearEndDay.disable({ emitEvent: false });
      } else {
        this.form.controls.quoteProviderKind.enable({ emitEvent: false });
        if (this.form.controls.quoteProviderKind.value === 'CoinGecko') {
          this.form.controls.quoteProviderKind.setValue('TwelveData');
        }
        this.form.controls.fiscalYearEndMonth.enable({ emitEvent: false });
        this.form.controls.fiscalYearEndDay.enable({ emitEvent: false });
      }
    });

    this.form.controls.quoteProviderKind.valueChanges.subscribe((kind) => {
      this.providerValue.set(kind);
      // The field that no longer applies must be cleared, not just hidden —
      // otherwise a stale value from a previous provider choice would still
      // be sent on submit alongside the newly-visible one.
      if (kind === 'CoinGecko') {
        this.form.controls.providerSymbol.setValue(null);
      } else {
        this.form.controls.providerCoinId.setValue(null);
      }
    });

    // Cross-field "both or neither" validity depends on the SIBLING control's
    // value, which Angular does not automatically re-check when only the
    // sibling changes — nudge each one to revalidate when the other moves.
    this.form.controls.fiscalYearEndMonth.valueChanges.subscribe(() =>
      this.form.controls.fiscalYearEndDay.updateValueAndValidity({
        onlySelf: true,
        emitEvent: false,
      }),
    );
    this.form.controls.fiscalYearEndDay.valueChanges.subscribe(() =>
      this.form.controls.fiscalYearEndMonth.updateValueAndValidity({
        onlySelf: true,
        emitEvent: false,
      }),
    );

    if (this.isEdit) {
      // Crypto locked to no fiscal year end from the start in edit mode too —
      // matches the create-mode branch above without needing an assetClass
      // valueChanges event to fire (the control is disabled and starts at
      // its existing value, so no change event occurs on mount).
      if (this.assetClassValue() === 'Crypto') {
        this.form.controls.fiscalYearEndMonth.disable({ emitEvent: false });
        this.form.controls.fiscalYearEndDay.disable({ emitEvent: false });
      }
    }
  }

  fieldError(name: keyof AssetFormControls): string | null {
    const control = this.form.controls[name];
    if (!control.touched || !control.errors) {
      return null;
    }
    return this.describeError(name, control.errors);
  }

  /**
   * Shares the codes and their precedence with the other form dialogs
   * (`shared/forms/describe-error.ts`), but keeps three pieces of copy that
   * are genuinely this form's own and must not drift into the shared wording:
   *
   * - `pattern` — only `currency` has one, and naming the expected shape is
   *   far more useful there than a generic "Invalid format."
   * - `min` — here it is a RANGE bound (month 1-12, day 1-31), not the sign
   *   check `Validators.min(0)` performs in the transaction and zakat-payment
   *   dialogs, so "Cannot be negative." would be wrong. `max` needs no
   *   override: its shared wording is already this range wording, because
   *   this form is its only caller.
   * - `fiscalYearEndPair` — zakat.md §3.1's "both set or both null" rule,
   *   passed as the fallback because it was, and stays, the last check before
   *   the generic one. No other code it can co-occur with reaches this point:
   *   the pair validator only fires on an EMPTY control, where min/max are
   *   silent by definition.
   */
  private describeError(name: keyof AssetFormControls, errors: ValidationErrors): string {
    return describeValidationError(
      errors,
      errors['fiscalYearEndPair']
        ? 'Set both month and day, or leave both blank.'
        : 'Invalid value.',
      {
        pattern: () =>
          name === 'currency' ? 'A 3-letter ISO currency code, e.g. USD.' : 'Invalid format.',
        min: (detail: { min: number }) => `Must be ${detail.min} or greater.`,
      },
    );
  }

  submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const raw = this.form.getRawValue();
    const request: CreateAssetRequest = {
      symbol: raw.symbol.trim().toUpperCase(),
      name: raw.name.trim(),
      assetClass: raw.assetClass,
      exchange: raw.exchange?.trim() ? raw.exchange.trim() : null,
      currency: raw.currency.trim().toUpperCase(),
      quoteProviderKind: raw.quoteProviderKind,
      providerSymbol: raw.providerSymbol?.trim() ? raw.providerSymbol.trim() : null,
      providerCoinId: raw.providerCoinId?.trim() ? raw.providerCoinId.trim() : null,
      fiscalYearEndMonth: raw.fiscalYearEndMonth,
      fiscalYearEndDay: raw.fiscalYearEndDay,
    };

    this.submitting.set(true);
    this.serverError.set(null);

    const call = this.existing
      ? this.assetsApi.replace(this.existing, request)
      : this.assetsApi.create(request);

    call.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (asset) => {
        this.submitting.set(false);
        this.dialogRef.close({ kind: 'saved', asset });
      },
      error: (error: unknown) => this.handleError(error),
    });
  }

  private handleError(error: unknown): void {
    this.submitting.set(false);

    if (error instanceof HttpErrorResponse && error.status === 400) {
      const problem = error.error as ValidationProblemDetails | undefined;
      if (problem?.errors) {
        // Verified live: ValidationProblemDetails keys errors by the camelCase
        // request-body field name ("providerCoinId", "providerSymbol"), which
        // matches this form's control names 1:1 — see
        // shared/forms/server-errors.ts. Anything it could not place still has
        // to reach the user, as this dialog's banner.
        const unmapped = applyServerErrors(this.form, SERVER_ERROR_FIELDS, problem.errors);
        if (unmapped.length > 0) {
          this.serverError.set(unmapped.join(' '));
        }
        return;
      }
    }

    this.serverError.set('Could not save this asset — check your connection and try again.');
  }

  close(): void {
    this.dialogRef.close();
  }
}
