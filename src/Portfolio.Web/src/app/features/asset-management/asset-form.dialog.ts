import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { FormControl, FormGroup, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';

import { API_ROUTES } from '../../core/api/api-routes';
import { AssetClass, AssetDto, CreateAssetRequest, QuoteProviderKind, ValidationProblemDetails } from '../../core/api/models';

export interface AssetFormDialogData {
  mode: 'create';
}

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
];

/**
 * Create form for a new tracked asset (D24). Crypto locks its provider to
 * CoinGecko — the app's own decision is that crypto only ever routes through
 * CoinGecko (tracker.md Decisions) — while Stock lets the user choose
 * TwelveData (US equities) or Yahoo (SGX, `.SI` suffix; TwelveData's free
 * tier cannot serve SGX at all). Switching provider swaps which identifier
 * field is shown (`providerSymbol` vs `providerCoinId`) and clears the
 * other, so a stale hidden value never gets submitted alongside the visible
 * one.
 */
@Component({
  selector: 'app-asset-form-dialog',
  standalone: true,
  imports: [ReactiveFormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule],
  templateUrl: './asset-form.dialog.html',
  styleUrl: './asset-form.dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AssetFormDialog {
  readonly data = inject<AssetFormDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<AssetFormDialog, AssetFormDialogResult>);
  private readonly http = inject(HttpClient);

  readonly submitting = signal(false);
  readonly serverError = signal<string | null>(null);

  readonly form: FormGroup<AssetFormControls> = new FormGroup<AssetFormControls>({
    symbol: new FormControl<string>('', { nonNullable: true, validators: [Validators.required] }),
    name: new FormControl<string>('', { nonNullable: true, validators: [Validators.required] }),
    assetClass: new FormControl<AssetClass>('Stock', { nonNullable: true, validators: [Validators.required] }),
    exchange: new FormControl<string | null>(null),
    currency: new FormControl<string>('USD', {
      nonNullable: true,
      validators: [Validators.required, Validators.pattern(/^[A-Za-z]{3}$/)],
    }),
    quoteProviderKind: new FormControl<QuoteProviderKind>('TwelveData', {
      nonNullable: true,
      validators: [Validators.required],
    }),
    providerSymbol: new FormControl<string | null>(null),
    providerCoinId: new FormControl<string | null>(null),
  });

  readonly assetClassValue = signal<AssetClass>('Stock');
  readonly providerValue = signal<QuoteProviderKind>('TwelveData');

  readonly isCrypto = computed(() => this.assetClassValue() === 'Crypto');
  readonly usesCoinId = computed(() => this.providerValue() === 'CoinGecko');
  readonly showsCreditWarning = computed(() => this.providerValue() === 'TwelveData');

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
    this.form.controls.assetClass.valueChanges.subscribe((assetClass) => {
      this.assetClassValue.set(assetClass);
      if (assetClass === 'Crypto') {
        this.form.controls.quoteProviderKind.setValue('CoinGecko');
        this.form.controls.quoteProviderKind.disable({ emitEvent: false });
        this.form.controls.currency.setValue('USD');
      } else {
        this.form.controls.quoteProviderKind.enable({ emitEvent: false });
        if (this.form.controls.quoteProviderKind.value === 'CoinGecko') {
          this.form.controls.quoteProviderKind.setValue('TwelveData');
        }
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
  }

  fieldError(name: keyof AssetFormControls): string | null {
    const control = this.form.controls[name];
    if (!control.touched || !control.errors) {
      return null;
    }
    return this.describeError(name, control.errors);
  }

  private describeError(name: keyof AssetFormControls, errors: ValidationErrors): string {
    if (errors['server']) {
      return errors['server'] as string;
    }
    if (errors['required']) {
      return 'Required.';
    }
    if (errors['pattern']) {
      return name === 'currency' ? 'A 3-letter ISO currency code, e.g. USD.' : 'Invalid format.';
    }
    return 'Invalid value.';
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
    };

    this.submitting.set(true);
    this.serverError.set(null);

    this.http.post<AssetDto>(API_ROUTES.assets, request).subscribe({
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
        this.applyServerErrors(problem.errors);
        return;
      }
    }

    this.serverError.set('Could not save this asset — check your connection and try again.');
  }

  private applyServerErrors(errors: Record<string, string[]>): void {
    const unmapped: string[] = [];

    for (const [field, messages] of Object.entries(errors)) {
      const message = messages[0] ?? 'Invalid value.';
      // Verified live: ValidationProblemDetails keys errors by the camelCase
      // request-body field name ("providerCoinId", "providerSymbol"), which
      // matches this form's control names 1:1 — same convention as
      // TransactionFormDialog's SERVER_ERROR_FIELDS lookup.
      const controlName = SERVER_ERROR_FIELDS.find((name) => name === field);
      if (controlName) {
        const control = this.form.controls[controlName];
        control.setErrors({ ...control.errors, server: message });
        control.markAsTouched();
      } else {
        unmapped.push(message);
      }
    }

    if (unmapped.length > 0) {
      this.serverError.set(unmapped.join(' '));
    }
  }

  close(): void {
    this.dialogRef.close();
  }
}
