import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { httpResource, HttpClient } from '@angular/common/http';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';

import { AssetClass, AssetDto, TransactionDto } from '../../core/api/models';
import { API_ROUTES } from '../../core/api/api-routes';
import { MoneyPipe } from '../../shared/pipes/money.pipe';
import { QuantityPipe } from '../../shared/pipes/quantity.pipe';
import { StateMessage } from '../../shared/state-message/state-message';
import { ConfirmDialog, ConfirmDialogData } from '../../shared/confirm-dialog/confirm-dialog';
import { TransactionFormDialog, TransactionFormDialogData, TransactionFormDialogResult } from './transaction-form.dialog';

type TransactionFilter = 'All' | AssetClass;

/** Matches the backend's own ordering — OrderByDescending(TradeDate).ThenByDescending(Id) —
 *  so a locally spliced create/edit lands in the same slot a reload would put it in. */
function sortTransactions(transactions: TransactionDto[]): TransactionDto[] {
  return [...transactions].sort(
    (a, b) => b.tradeDate.localeCompare(a.tradeDate) || b.id - a.id,
  );
}

/**
 * Covers both asset classes at once with an in-page filter — see
 * app.routes.ts. Owns create/edit/delete via TransactionFormDialog and
 * ConfirmDialog, with optimistic local updates to the transactions
 * `httpResource` (WritableResource.update/set) rather than a full reload on
 * every mutation.
 */
@Component({
  selector: 'app-transactions-page',
  standalone: true,
  imports: [MoneyPipe, QuantityPipe, StateMessage, MatButtonModule, MatButtonToggleModule, MatIconModule],
  templateUrl: './transactions.page.html',
  styleUrl: './transactions.page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TransactionsPage {
  private readonly dialog = inject(MatDialog);
  private readonly http = inject(HttpClient);

  private readonly transactionsResource = httpResource<TransactionDto[]>(() => API_ROUTES.transactions);
  private readonly assetsResource = httpResource<AssetDto[]>(() => API_ROUTES.assets);

  readonly filter = signal<TransactionFilter>('All');

  readonly isLoading = this.transactionsResource.isLoading;
  readonly hasError = computed(() => this.transactionsResource.error() != null);
  readonly allTransactions = computed(() => this.transactionsResource.value() ?? []);

  readonly filteredTransactions = computed(() => {
    const filterValue = this.filter();
    const all = this.allTransactions();
    return filterValue === 'All' ? all : all.filter((t) => t.assetClass === filterValue);
  });

  readonly isTotalEmpty = computed(
    () => !this.isLoading() && !this.hasError() && this.allTransactions().length === 0,
  );
  readonly isFilteredEmpty = computed(
    () => !this.isLoading() && !this.hasError() && !this.isTotalEmpty() && this.filteredTransactions().length === 0,
  );

  readonly assetsErrored = computed(() => this.assetsResource.error() != null);
  /** Guards New/Edit — opening the dialog before this resolves would show an
   *  asset picker with no options, and there is nowhere in that dialog to
   *  fix an assetId that was never set. */
  readonly assetsReady = computed(() => this.assetsResource.hasValue());

  readonly deleteError = signal<string | null>(null);

  retry(): void {
    this.transactionsResource.reload();
  }

  retryAssets(): void {
    this.assetsResource.reload();
  }

  clearFilter(): void {
    this.filter.set('All');
  }

  setFilter(value: TransactionFilter): void {
    this.filter.set(value);
  }

  openCreateDialog(): void {
    if (!this.assetsReady()) {
      return;
    }

    // Sizing is set by the dialog's own template (transaction-form.dialog.scss,
    // via --ui-layout-dialog-width-sm) rather than an inline `width` here, so
    // the measure lives in exactly one token-driven place.
    const ref = this.dialog.open<TransactionFormDialog, TransactionFormDialogData, TransactionFormDialogResult>(
      TransactionFormDialog,
      { data: { mode: 'create', assets: this.assetsResource.value() ?? [] } },
    );

    ref.afterClosed().subscribe((result) => {
      if (result?.kind === 'saved') {
        this.upsertTransaction(result.transaction);
      }
    });
  }

  openEditDialog(transaction: TransactionDto): void {
    if (!this.assetsReady()) {
      return;
    }

    const ref = this.dialog.open<TransactionFormDialog, TransactionFormDialogData, TransactionFormDialogResult>(
      TransactionFormDialog,
      { data: { mode: 'edit', transaction, assets: this.assetsResource.value() ?? [] } },
    );

    ref.afterClosed().subscribe((result) => {
      if (result?.kind === 'saved') {
        this.upsertTransaction(result.transaction);
      } else if (result?.kind === 'deleted-elsewhere') {
        // The row this dialog was editing is already gone server-side —
        // reconcile the local list with the server rather than guessing.
        this.transactionsResource.reload();
      }
    });
  }

  deleteTransaction(transaction: TransactionDto): void {
    const ref = this.dialog.open<ConfirmDialog, ConfirmDialogData, boolean>(ConfirmDialog, {
      data: {
        title: 'Delete transaction?',
        message: `Delete the ${transaction.type.toLowerCase()} of ${transaction.quantity} ${transaction.assetSymbol} on ${transaction.tradeDate}? This cannot be undone.`,
        confirmLabel: 'Delete',
        destructive: true,
      },
    });

    ref.afterClosed().subscribe((confirmed) => {
      if (!confirmed) {
        return;
      }

      this.deleteError.set(null);
      // Optimistic removal — rolled back by reload() below if the DELETE fails.
      this.transactionsResource.update((list) => (list ?? []).filter((t) => t.id !== transaction.id));

      this.http.delete<void>(API_ROUTES.transaction(transaction.id)).subscribe({
        error: () => {
          this.deleteError.set(
            `Couldn't delete the ${transaction.assetSymbol} transaction — it has been restored.`,
          );
          this.transactionsResource.reload();
        },
      });
    });
  }

  dismissDeleteError(): void {
    this.deleteError.set(null);
  }

  private upsertTransaction(transaction: TransactionDto): void {
    this.transactionsResource.update((list) =>
      sortTransactions([...(list ?? []).filter((t) => t.id !== transaction.id), transaction]),
    );
  }
}
