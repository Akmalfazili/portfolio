import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSortModule, Sort } from '@angular/material/sort';
import { ScrollingModule } from '@angular/cdk/scrolling';

import { AssetClass, TransactionDto } from '../../core/api/models';
import { AssetsApi } from '../../core/api/assets.api';
import { TransactionsApi } from '../../core/api/transactions.api';
import { NotificationService } from '../../core/notifications/notification.service';
import { MoneyPipe } from '../../shared/pipes/money.pipe';
import { QuantityPipe } from '../../shared/pipes/quantity.pipe';
import { StateMessage } from '../../shared/state-message/state-message';
import { ConfirmDialog, ConfirmDialogData } from '../../shared/confirm-dialog/confirm-dialog';
import { TablePager } from '../../shared/table/table-pager/table-pager';
import { ALL_ROWS, TableSort, createTableState } from '../../shared/table/table-state';
import { TRANSACTION_ROW_HEIGHT_PX } from '../../shared/table/table-row-height';
import { VirtualRowgroup } from '../../shared/table/virtual-rowgroup';
import {
  TransactionFormDialog,
  TransactionFormDialogData,
  TransactionFormDialogResult,
} from './transaction-form.dialog';

type TransactionFilter = 'All' | AssetClass;
type TransactionColumn = 'date' | 'symbol' | 'type' | 'quantity' | 'price' | 'fees';

/** Matches the backend's own ordering — OrderByDescending(TradeDate).ThenByDescending(Id) —
 *  so a locally spliced create/edit lands in the same slot a reload would put it in, and
 *  is reused as the table-state's default-sort tiebreak below. */
function sortTransactions(transactions: TransactionDto[]): TransactionDto[] {
  return [...transactions].sort((a, b) => b.tradeDate.localeCompare(a.tradeDate) || b.id - a.id);
}

/**
 * Covers both asset classes at once with an in-page filter — see
 * app.routes.ts. Owns create/edit/delete via TransactionFormDialog and
 * ConfirmDialog, with optimistic local updates to the transactions
 * `httpResource` (WritableResource.update/set) rather than a full reload on
 * every mutation.
 *
 * Sorting/pagination via the shared table-state helper (`shared/table/table-state.ts`),
 * composed on top of `filteredTransactions` rather than owning the filter
 * itself. Price/fees are the transaction's own NATIVE currency (mixed
 * USD/SGD across rows) — sorting those two columns therefore compares raw
 * magnitudes across currencies; see tracker.md.
 *
 * Rows are uniformly one line tall, so selecting the "All" page size switches
 * the body to a `cdk-virtual-scroll-viewport` — the header row lives OUTSIDE
 * that viewport (not `position: sticky` inside it), because the viewport
 * translates its content with a CSS `transform`, which establishes a new
 * containing block that `position: sticky` cannot escape; a header inside it
 * would not actually stick. Keeping it outside is simpler and correct. The
 * virtualized body is a CSS-grid ARIA table (`role="table"/"row"/"cell"`),
 * not `<tr>`/`<td>` — wrapping real `<tr>` rows in the viewport fights HTML's
 * table layout algorithm, which expects to size every row/column itself.
 */
@Component({
  selector: 'app-transactions-page',
  standalone: true,
  imports: [
    MoneyPipe,
    QuantityPipe,
    StateMessage,
    MatButtonModule,
    MatButtonToggleModule,
    MatIconModule,
    MatSortModule,
    TablePager,
    ScrollingModule,
    VirtualRowgroup,
  ],
  templateUrl: './transactions.page.html',
  styleUrl: './transactions.page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TransactionsPage {
  private readonly dialog = inject(MatDialog);
  private readonly transactionsApi = inject(TransactionsApi);
  private readonly assetsApi = inject(AssetsApi);
  private readonly notifications = inject(NotificationService);
  private readonly destroyRef = inject(DestroyRef);

  private readonly transactionsResource = this.transactionsApi.list();
  private readonly assetsResource = this.assetsApi.list();

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
    () =>
      !this.isLoading() &&
      !this.hasError() &&
      !this.isTotalEmpty() &&
      this.filteredTransactions().length === 0,
  );

  readonly tableState = createTableState<TransactionDto, TransactionColumn>({
    rows: this.filteredTransactions,
    columns: {
      date: (t) => t.tradeDate,
      symbol: (t) => t.assetSymbol,
      type: (t) => t.type,
      quantity: (t) => t.quantity,
      price: (t) => t.pricePerUnit,
      fees: (t) => t.fees,
    },
    defaultSort: { active: 'date', direction: 'desc' },
    tiebreak: (a, b) => b.id - a.id,
  });

  readonly isVirtualized = computed(() => this.tableState.pageSize() === ALL_ROWS);
  readonly rowHeightPx = TRANSACTION_ROW_HEIGHT_PX;

  onSortChange(sort: Sort): void {
    this.tableState.setSort(sort as TableSort<TransactionColumn>);
  }

  trackById(_index: number, transaction: TransactionDto): number {
    return transaction.id;
  }

  readonly assetsErrored = computed(() => this.assetsResource.error() != null);
  /** Guards New/Edit — opening the dialog before this resolves would show an
   *  asset picker with no options, and there is nowhere in that dialog to
   *  fix an assetId that was never set. */
  readonly assetsReady = computed(() => this.assetsResource.hasValue());

  retry(): void {
    this.transactionsResource.reload();
  }

  retryAssets(): void {
    this.assetsResource.reload();
  }

  clearFilter(): void {
    this.setFilter('All');
  }

  setFilter(value: TransactionFilter): void {
    this.filter.set(value);
    // Changing the filter resets to page 0 — a page index valid under the
    // old row count can easily be out of range (or just confusing) under
    // the new, smaller one.
    this.tableState.resetPage();
  }

  openCreateDialog(): void {
    if (!this.assetsReady()) {
      return;
    }

    // Sizing is set by the dialog's own template (transaction-form.dialog.scss,
    // via --ui-layout-dialog-width-sm) rather than an inline `width` here, so
    // the measure lives in exactly one token-driven place.
    const ref = this.dialog.open<
      TransactionFormDialog,
      TransactionFormDialogData,
      TransactionFormDialogResult
    >(TransactionFormDialog, {
      data: { mode: 'create', assets: this.assetsResource.value() ?? [] },
    });

    ref.afterClosed().subscribe((result) => {
      if (result?.kind === 'saved') {
        this.upsertTransaction(result.transaction);
        this.notifications.success('Transaction recorded.');
      }
    });
  }

  openEditDialog(transaction: TransactionDto): void {
    if (!this.assetsReady()) {
      return;
    }

    const ref = this.dialog.open<
      TransactionFormDialog,
      TransactionFormDialogData,
      TransactionFormDialogResult
    >(TransactionFormDialog, {
      data: { mode: 'edit', transaction, assets: this.assetsResource.value() ?? [] },
    });

    ref.afterClosed().subscribe((result) => {
      if (result?.kind === 'saved') {
        this.upsertTransaction(result.transaction);
        this.notifications.success('Transaction updated.');
      } else if (result?.kind === 'deleted-elsewhere') {
        // The row this dialog was editing is already gone server-side —
        // reconcile the local list with the server rather than guessing.
        // Not an error: the user didn't do anything wrong, the world just
        // moved out from under this dialog while it was open.
        this.transactionsResource.reload();
        this.notifications.info('That transaction no longer exists — the list has been refreshed.');
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

      // Optimistic removal — rolled back by reload() below if the DELETE fails.
      this.transactionsResource.update((list) =>
        (list ?? []).filter((t) => t.id !== transaction.id),
      );

      this.transactionsApi
        .delete(transaction.id)
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: () => {
            this.notifications.success(`${transaction.assetSymbol} transaction deleted.`);
          },
          error: () => {
            this.notifications.error(
              `Couldn't delete the ${transaction.assetSymbol} transaction — it has been restored.`,
            );
            this.transactionsResource.reload();
          },
        });
    });
  }

  private upsertTransaction(transaction: TransactionDto): void {
    this.transactionsResource.update((list) =>
      sortTransactions([...(list ?? []).filter((t) => t.id !== transaction.id), transaction]),
    );
  }
}
