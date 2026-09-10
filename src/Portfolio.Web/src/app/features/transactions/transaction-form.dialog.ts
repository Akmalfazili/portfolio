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
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { provideNativeDateAdapter } from '@angular/material/core';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';

import { TransactionsApi } from '../../core/api/transactions.api';
import {
  AssetDto,
  CreateTransactionRequest,
  TransactionDto,
  TransactionType,
  ValidationProblemDetails,
} from '../../core/api/models';
import {
  decimalPrecisionValidator,
  nonNegativeNumberValidator,
  positiveNumberValidator,
} from '../../shared/validators/decimal-precision.validator';
import { describeValidationError } from '../../shared/forms/describe-error';
import { applyServerErrors } from '../../shared/forms/server-errors';
import { fromDateOnlyString, toDateOnlyString, todayDateOnly } from '../../shared/util/local-date';

export interface TransactionFormDialogData {
  mode: 'create' | 'edit';
  transaction?: TransactionDto;
  assets: AssetDto[];
}

export type TransactionFormDialogResult =
  { kind: 'saved'; transaction: TransactionDto } | { kind: 'deleted-elsewhere' };

interface TransactionFormControls {
  assetId: FormControl<number | null>;
  type: FormControl<TransactionType>;
  tradeDate: FormControl<Date | null>;
  quantity: FormControl<number | null>;
  pricePerUnit: FormControl<number | null>;
  fees: FormControl<number>;
  currency: FormControl<string>;
  notes: FormControl<string | null>;
}

/**
 * Field names in `ValidationProblemDetails.errors` line up 1:1 with the
 * request DTO (and so with this form's control names), which is what makes
 * the direct `errors[controlName]` lookup below safe — see
 * TransactionService.ValidateCommon on the backend.
 */
const SERVER_ERROR_FIELDS: (keyof TransactionFormControls)[] = [
  'assetId',
  'quantity',
  'pricePerUnit',
  'fees',
  'tradeDate',
  'currency',
];

/**
 * Create/edit form for a single transaction. Typed `FormGroup<{...}>`
 * throughout — quantity and price accept up to 10 decimal places via
 * `type="number" step="any"` (never a step that assumes integers or 2dp) plus
 * `decimalPrecisionValidator`, which fails loudly in the UI rather than
 * silently losing precision at the database (see tracker.md D8).
 *
 * Performs its own POST/PUT so field-level server errors (positive quantity,
 * sell exceeds units held, future trade date — all 400
 * ValidationProblemDetails) can be shown inline without a round trip through
 * the parent page. A PUT 404 (the row was deleted elsewhere while this
 * dialog was open) is shown as its own state rather than a generic error,
 * since there is nothing left here to retry.
 */
@Component({
  selector: 'app-transaction-form-dialog',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatDialogModule,
    MatDatepickerModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSelectModule,
  ],
  providers: [provideNativeDateAdapter()],
  templateUrl: './transaction-form.dialog.html',
  styleUrl: './transaction-form.dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TransactionFormDialog {
  readonly data = inject<TransactionFormDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(
    MatDialogRef<TransactionFormDialog, TransactionFormDialogResult>,
  );
  private readonly transactionsApi = inject(TransactionsApi);
  private readonly destroyRef = inject(DestroyRef);

  readonly today = todayDateOnly();
  readonly submitting = signal(false);
  readonly serverError = signal<string | null>(null);
  readonly deletedElsewhere = signal(false);

  readonly assetOptions = computed(() =>
    [...this.data.assets]
      .filter((asset) => asset.isActive)
      .sort((a, b) => a.symbol.localeCompare(b.symbol)),
  );

  readonly isEdit = this.data.mode === 'edit';
  readonly title = this.isEdit ? 'Edit transaction' : 'New transaction';

  readonly form: FormGroup<TransactionFormControls> = new FormGroup<TransactionFormControls>({
    assetId: new FormControl<number | null>(this.data.transaction?.assetId ?? null, {
      validators: [Validators.required],
    }),
    type: new FormControl<TransactionType>(this.data.transaction?.type ?? 'Buy', {
      nonNullable: true,
    }),
    tradeDate: new FormControl<Date | null>(
      this.data.transaction ? fromDateOnlyString(this.data.transaction.tradeDate) : this.today,
      { validators: [Validators.required] },
    ),
    quantity: new FormControl<number | null>(this.data.transaction?.quantity ?? null, {
      validators: [Validators.required, positiveNumberValidator(), decimalPrecisionValidator(10)],
    }),
    pricePerUnit: new FormControl<number | null>(this.data.transaction?.pricePerUnit ?? null, {
      // D36 — zero is a legitimate price (free share / bonus issue / scrip
      // dividend), so this is the one quantity-ish field that does NOT use
      // positiveNumberValidator(). Still required: Validators.required treats
      // 0 as present, so a genuinely blank field is still caught.
      validators: [
        Validators.required,
        nonNegativeNumberValidator(),
        decimalPrecisionValidator(10),
      ],
    }),
    fees: new FormControl<number>(this.data.transaction?.fees ?? 0, {
      nonNullable: true,
      validators: [Validators.min(0), decimalPrecisionValidator(4)],
    }),
    currency: new FormControl<string>(this.data.transaction?.currency ?? '', {
      nonNullable: true,
      validators: [Validators.required],
    }),
    notes: new FormControl<string | null>(this.data.transaction?.notes ?? null),
  });

  constructor() {
    this.form.controls.currency.disable({ emitEvent: false });

    this.form.controls.assetId.valueChanges.subscribe((assetId) => {
      const asset = this.data.assets.find((candidate) => candidate.id === assetId);
      if (asset) {
        this.form.controls.currency.setValue(asset.currency);
      }
    });
  }

  fieldError(name: keyof TransactionFormControls): string | null {
    const control = this.form.controls[name];
    if (!control.touched || !control.errors) {
      return null;
    }
    return this.describeError(name, control.errors);
  }

  /** Only the last-resort string is this dialog's own — the codes and their
   *  precedence are shared (`shared/forms/describe-error.ts`). */
  private describeError(name: keyof TransactionFormControls, errors: ValidationErrors): string {
    return describeValidationError(
      errors,
      name === 'tradeDate' ? 'Invalid date.' : 'Invalid value.',
    );
  }

  submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const raw = this.form.getRawValue();
    const request: CreateTransactionRequest = {
      assetId: raw.assetId!,
      type: raw.type,
      tradeDate: toDateOnlyString(raw.tradeDate!),
      quantity: raw.quantity!,
      pricePerUnit: raw.pricePerUnit!,
      fees: raw.fees,
      currency: raw.currency,
      notes: raw.notes?.trim() ? raw.notes.trim() : null,
    };

    this.submitting.set(true);
    this.serverError.set(null);

    const call =
      this.data.mode === 'create'
        ? this.transactionsApi.create(request)
        : this.transactionsApi.update(this.data.transaction!.id, request);

    call.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (transaction) => {
        this.submitting.set(false);
        this.dialogRef.close({ kind: 'saved', transaction });
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

    this.serverError.set('Could not save this transaction — check your connection and try again.');
  }

  close(): void {
    this.dialogRef.close();
  }

  closeAfterDeletedElsewhere(): void {
    this.dialogRef.close({ kind: 'deleted-elsewhere' });
  }
}
