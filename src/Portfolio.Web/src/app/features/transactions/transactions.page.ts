import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { provideNativeDateAdapter } from '@angular/material/core';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSortModule, Sort } from '@angular/material/sort';
import { ScrollingModule } from '@angular/cdk/scrolling';

import { AssetClass, TransactionDto, TransactionType } from '../../core/api/models';
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
import { toDateOnlyString } from '../../shared/util/local-date';
import {
  TransactionFormDialog,
  TransactionFormDialogData,
  TransactionFormDialogResult,
} from './transaction-form.dialog';

type AssetClassFilter = 'All' | AssetClass;
type TypeFilter = 'All' | TransactionType;
type TransactionColumn = 'date' | 'symbol' | 'type' | 'quantity' | 'price' | 'fees';

/**
 * `Date` (possibly `null`, possibly an unparseable "Invalid Date" the native
 * date adapter produces while the user is mid-typing) -> a `YYYY-MM-DD`
 * bound, or `null` for "no bound." Goes through `toDateOnlyString`, never
 * `toISOString()` — see that function's own doc comment and
 * `shared/util/local-date.ts`'s header: `tradeDate` is a wire `DateOnly`, and
 * comparing it against a UTC-shifted string would silently exclude same-day
 * rows at this app's own positive UTC offset (SGT).
 */
function dateOnlyBound(date: Date | null): string | null {
  if (date === null || Number.isNaN(date.getTime())) {
    return null;
  }
  return toDateOnlyString(date);
}

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
 * Four independent filters compose in `filteredTransactions`: asset class
 * (`assetClassFilter`, the pre-existing toggle, renamed from `filter`),
 * transaction type (`typeFilter`), a case-insensitive substring match on
 * symbol (`symbolFilter`), and an inclusive trade-date range
 * (`startDateControl`/`endDateControl`, native `Date`s from
 * `mat-date-range-input`). Every one of them calls `tableState.resetPage()`
 * on change, exactly like the original asset-class `setFilter()` — a page
 * index valid under the old row count can easily be out of range (or just
 * confusing) under a smaller filtered one. The date bounds are converted with
 * `toDateOnlyString`/compared as `YYYY-MM-DD` strings, never `Date` objects
 * or `toISOString()` — see `dateOnlyBound`'s own comment.
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
    ReactiveFormsModule,
    MoneyPipe,
    QuantityPipe,
    StateMessage,
    MatButtonModule,
    MatButtonToggleModule,
    MatDatepickerModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSortModule,
    TablePager,
    ScrollingModule,
    VirtualRowgroup,
  ],
  providers: [provideNativeDateAdapter()],
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

  readonly assetClassFilter = signal<AssetClassFilter>('All');
  readonly typeFilter = signal<TypeFilter>('All');
  readonly symbolFilter = signal('');

  // Two independent FormControls, not a FormGroup — mat-date-range-input's
  // start/end coordination (its built-in "end before start" validation) is
  // wired through the DOM structure of <mat-date-range-input>, not through a
  // shared parent form, so a group here would be pure ceremony.
  readonly startDateControl = new FormControl<Date | null>(null);
  readonly endDateControl = new FormControl<Date | null>(null);
  private readonly startDate = toSignal(this.startDateControl.valueChanges, {
    initialValue: this.startDateControl.value,
  });
  private readonly endDate = toSignal(this.endDateControl.valueChanges, {
    initialValue: this.endDateControl.value,
  });

  readonly isLoading = this.transactionsResource.isLoading;
  readonly hasError = computed(() => this.transactionsResource.error() != null);
  readonly allTransactions = computed(() => this.transactionsResource.value() ?? []);

  readonly filteredTransactions = computed(() => {
    const assetClass = this.assetClassFilter();
    const type = this.typeFilter();
    const symbolQuery = this.symbolFilter().trim().toLowerCase();
    const startBound = dateOnlyBound(this.startDate());
    // An end date before the start date is a broken range — the picker's own
    // validation surfaces that in the UI (matEndDateInvalid), but a filter
    // still has to render *something* rather than silently show zero rows,
    // so the broken bound is dropped and the range is treated as open-ended
    // on that side, never clamped to a value nobody asked for.
    const rawEndBound = dateOnlyBound(this.endDate());
    const endBound = startBound && rawEndBound && rawEndBound < startBound ? null : rawEndBound;

    return this.allTransactions().filter((t) => {
      if (assetClass !== 'All' && t.assetClass !== assetClass) {
        return false;
      }
      if (type !== 'All' && t.type !== type) {
        return false;
      }
      if (symbolQuery && !t.assetSymbol.toLowerCase().includes(symbolQuery)) {
        return false;
      }
      if (startBound && t.tradeDate < startBound) {
        return false;
      }
      if (endBound && t.tradeDate > endBound) {
        return false;
      }
      return true;
    });
  });

  /** Drives the "Clear filters" button and the result-count hint — neither
   *  should show when every filter is at its default. */
  readonly isFilterActive = computed(
    () =>
      this.assetClassFilter() !== 'All' ||
      this.typeFilter() !== 'All' ||
      this.symbolFilter().trim() !== '' ||
      this.startDate() !== null ||
      this.endDate() !== null,
  );

  /** "12 of 140 transactions" — only shown while a filter is active, so it
   *  never sits next to the pager's own range label repeating the same
   *  number for an unfiltered list. Unlike the pager (which only ever knows
   *  the already-filtered total), this is the one place that shows the
   *  filtered count against the true unfiltered total. */
  readonly resultCountLabel = computed(() => {
    if (!this.isFilterActive()) {
      return null;
    }
    const shown = this.filteredTransactions().length;
    const total = this.allTransactions().length;
    return `${shown} of ${total} transaction${total === 1 ? '' : 's'}`;
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
  /** The filter row stays visible in the filtered-empty state (so the user
   *  can loosen a filter that matched nothing) but is pointless — and would
   *  sit above nothing useful — while loading, errored, or genuinely empty. */
  readonly showFilterRow = computed(
    () => !this.isLoading() && !this.hasError() && !this.isTotalEmpty(),
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

  constructor() {
    // The date-range picker has no discrete "setter" of its own the way the
    // toggle/search filters do — it's the user typing or picking in the
    // input — so the page-0 reset is wired here instead, once per control,
    // rather than duplicated at every call site the way it would be if this
    // reached into the FormControl API from the template.
    this.startDateControl.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.tableState.resetPage());
    this.endDateControl.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.tableState.resetPage());
  }

  setFilter(value: AssetClassFilter): void {
    this.assetClassFilter.set(value);
    // Changing a filter resets to page 0 — a page index valid under the old
    // row count can easily be out of range (or just confusing) under the
    // new, smaller one.
    this.tableState.resetPage();
  }

  setTypeFilter(value: TypeFilter): void {
    this.typeFilter.set(value);
    this.tableState.resetPage();
  }

  setSymbolFilter(value: string): void {
    this.symbolFilter.set(value);
    this.tableState.resetPage();
  }

  clearSymbolFilter(): void {
    this.setSymbolFilter('');
  }

  /** Resets all four filters at once — the filter row's own "Clear filters"
   *  button, and the filtered-empty state's action (which used to only clear
   *  the asset-class toggle, back when that was the only filter there was). */
  clearFilters(): void {
    this.assetClassFilter.set('All');
    this.typeFilter.set('All');
    this.symbolFilter.set('');
    this.startDateControl.setValue(null);
    this.endDateControl.setValue(null);
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
