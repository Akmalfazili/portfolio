import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { FormControl, FormGroup, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { provideNativeDateAdapter } from '@angular/material/core';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';

import { ZakatApi } from '../../core/api/zakat.api';
import { CreateZakatPaymentRequest, ValidationProblemDetails, ZakatPaymentDto } from '../../core/api/models';
import { decimalPrecisionValidator, positiveNumberValidator } from '../../shared/validators/decimal-precision.validator';
import { describeValidationError } from '../../shared/forms/describe-error';
import { applyServerErrors } from '../../shared/forms/server-errors';
import { fromDateOnlyString, toDateOnlyString, todayDateOnly } from '../../shared/util/local-date';

export interface ZakatPaymentFormDialogData {
  mode: 'create' | 'edit';
  payment?: ZakatPaymentDto;
}

export type ZakatPaymentFormDialogResult =
  | { kind: 'saved'; payment: ZakatPaymentDto }
  | { kind: 'deleted-elsewhere' };

interface ZakatPaymentFormControls {
  paidOn: FormControl<Date | null>;
  amountSgd: FormControl<number | null>;
}

/**
 * Field names line up 1:1 with `CreateZakatPaymentRequest`/`UpdateZakatPaymentRequest` — same
 * convention as `TransactionFormDialog.SERVER_ERROR_FIELDS`.
 */
const SERVER_ERROR_FIELDS: (keyof ZakatPaymentFormControls)[] = ['paidOn', 'amountSgd'];

/**
 * Create/edit form for one entry in the zakat payment ledger (zakat.md §3.2)
 * — a RECORDED FACT, amount and date only, never derived from the computed
 * report on the page above it. `amountSgd` is ordinary dollars and cents
 * (`decimal(19,4)`, but entered like any other money amount) — this is the
 * one zakat-page input that is NOT a fractional-quantity trap, unlike the
 * asset-detail quantity/price fields.
 *
 * `paidOn` follows the exact same `DateOnly`/local-date convention as
 * `TransactionFormDialog.tradeDate` — never round-tripped through
 * `toISOString()`, which shifts a day at SGT's positive UTC offset.
 */
@Component({
  selector: 'app-zakat-payment-form-dialog',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatDialogModule,
    MatDatepickerModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
  ],
  providers: [provideNativeDateAdapter()],
  templateUrl: './zakat-payment-form.dialog.html',
  styleUrl: './zakat-payment-form.dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ZakatPaymentFormDialog {
  readonly data = inject<ZakatPaymentFormDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<ZakatPaymentFormDialog, ZakatPaymentFormDialogResult>);
  private readonly zakatApi = inject(ZakatApi);

  readonly today = todayDateOnly();
  readonly submitting = signal(false);
  readonly serverError = signal<string | null>(null);
  readonly deletedElsewhere = signal(false);

  readonly isEdit = this.data.mode === 'edit';
  readonly title = this.isEdit ? 'Edit zakat payment' : 'Record a zakat payment';

  readonly form: FormGroup<ZakatPaymentFormControls> = new FormGroup<ZakatPaymentFormControls>({
    paidOn: new FormControl<Date | null>(
      this.data.payment ? fromDateOnlyString(this.data.payment.paidOn) : this.today,
      { validators: [Validators.required] },
    ),
    amountSgd: new FormControl<number | null>(this.data.payment?.amountSgd ?? null, {
      validators: [Validators.required, positiveNumberValidator(), decimalPrecisionValidator(4)],
    }),
  });

  fieldError(name: keyof ZakatPaymentFormControls): string | null {
    const control = this.form.controls[name];
    if (!control.touched || !control.errors) {
      return null;
    }
    return this.describeError(name, control.errors);
  }

  /** Only the last-resort string is this dialog's own — the codes and their
   *  precedence are shared (`shared/forms/describe-error.ts`). `paidOn`'s
   *  fallback names the future-date rule specifically, which is the one thing
   *  a bare "Invalid date." would leave the reader guessing at. */
  private describeError(name: keyof ZakatPaymentFormControls, errors: ValidationErrors): string {
    return describeValidationError(
      errors,
      name === 'paidOn' ? 'Invalid date — it cannot be in the future.' : 'Invalid value.',
    );
  }

  submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const raw = this.form.getRawValue();
    const request: CreateZakatPaymentRequest = {
      paidOn: toDateOnlyString(raw.paidOn!),
      amountSgd: raw.amountSgd!,
    };

    this.submitting.set(true);
    this.serverError.set(null);

    const call =
      this.data.mode === 'create'
        ? this.zakatApi.createPayment(request)
        : this.zakatApi.updatePayment(this.data.payment!.id, request);

    call.subscribe({
      next: (payment) => {
        this.submitting.set(false);
        this.dialogRef.close({ kind: 'saved', payment });
      },
      error: (error: unknown) => this.handleError(error),
    });
  }

  private handleError(error: unknown): void {
    this.submitting.set(false);

    if (error instanceof HttpErrorResponse) {
      if (error.status === 404 && this.data.mode === 'edit') {
        this.deletedElsewhere.set(true);
        return;
      }

      if (error.status === 400) {
        const problem = error.error as ValidationProblemDetails | undefined;
        if (problem?.errors) {
          // Anything the server complained about that has no control of its
          // own still has to reach the user — as this dialog's banner.
          const unmapped = applyServerErrors(this.form, SERVER_ERROR_FIELDS, problem.errors);
          if (unmapped.length > 0) {
            this.serverError.set(unmapped.join(' '));
          }
          return;
        }
      }
    }

    this.serverError.set('Could not save this payment — check your connection and try again.');
  }

  close(): void {
    this.dialogRef.close();
  }

  closeAfterDeletedElsewhere(): void {
    this.dialogRef.close({ kind: 'deleted-elsewhere' });
  }
}
