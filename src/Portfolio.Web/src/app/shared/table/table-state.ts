import { Signal, computed, signal } from '@angular/core';

/**
 * Signal-based sort + pagination state for the app's four hand-rolled
 * `<table>`s (holdings, transactions x2, assets). `MatTableDataSource` is
 * RxJS-based and does not compose with a zoneless signals app, so this is a
 * small purpose-built replacement wired to Angular Material's *standalone*
 * `MatSort`/`mat-sort-header`/`MatPaginator` — none of which require
 * `mat-table` (see each table component for how they're attached to plain
 * markup).
 *
 * Deliberately does NOT own filtering: `rows` is expected to already be the
 * filtered set (e.g. `TransactionsPage.filteredTransactions`), so this
 * composes with an upstream filter signal rather than duplicating it.
 */

export type SortDirection = 'asc' | 'desc';

export interface TableSort<K extends string> {
  readonly active: K;
  readonly direction: SortDirection;
}

/**
 * A column's sortable value — always the underlying typed value, never the
 * formatted display string (`tracker.md`: quantities/prices are
 * `decimal(28,10)`-scale, `tradeDate` is a lexicographically-correct
 * `YYYY-MM-DD` string, never parsed through `Date`).
 *
 * `null` means "unknown" (a null `averageCostUsd`/`currentPriceUsd` is a real,
 * meaningful state — D17/D20's "never coerce null to 0" rule) and always
 * sorts last, in BOTH directions — never treated as the smallest value.
 */
export type SortValue = string | number | null;

/** The subset of `TableState` a generic pager control needs — deliberately
 *  not generic over the row type, so `TablePager` can accept any table's
 *  state without a matching type parameter. */
export interface Pageable {
  readonly total: Signal<number>;
  readonly pageIndex: Signal<number>;
  readonly pageSize: Signal<number>;
  readonly pageSizeOptions: readonly number[];
  setPageIndex(index: number): void;
  setPageSize(size: number): void;
}

export interface TableState<T, K extends string> extends Pageable {
  readonly sort: Signal<TableSort<K>>;
  readonly pageCount: Signal<number>;
  /** The upstream rows, sorted — unpaged. */
  readonly sorted: Signal<T[]>;
  /** `sorted`, sliced to the current page (or all of it when `pageSize` is `ALL_ROWS`). */
  readonly paged: Signal<T[]>;
  setSort(sort: TableSort<K>): void;
  /** Explicit reset, for state that lives outside this helper — e.g. the
   *  transactions page's asset-class filter toggle. */
  resetPage(): void;
}

export interface TableStateConfig<T, K extends string> {
  /** Upstream rows, already filtered by the caller. */
  readonly rows: Signal<readonly T[]>;
  /** One typed value accessor per sortable column key. */
  readonly columns: Readonly<Record<K, (row: T) => SortValue>>;
  readonly defaultSort: TableSort<K>;
  /** Applied only when the primary column's values compare equal, so ties
   *  land in one fixed, predictable order — e.g. `(a, b) => b.id - a.id` —
   *  rather than an accidental one that happens to fall out of the sort
   *  algorithm's stability. */
  readonly tiebreak?: (a: T, b: T) => number;
  /** Ascending, smallest first. `ALL_ROWS` is never listed here — "All" is a
   *  distinct state reached only via `setPageSize(ALL_ROWS)`. */
  readonly pageSizeOptions?: readonly number[];
  readonly initialPageSize?: number;
}

/** Sentinel `pageSize` meaning "show every row on one page." */
export const ALL_ROWS = Infinity;

export const DEFAULT_PAGE_SIZE_OPTIONS: readonly number[] = [25, 50, 100];

/**
 * Nulls sort last regardless of direction — the D17/D20 rule applied to
 * sorting: a null never behaves like a zero. Only a non-null-vs-non-null
 * comparison is flipped by `direction`.
 */
export function compareSortValues(a: SortValue, b: SortValue, direction: SortDirection): number {
  if (a === null && b === null) {
    return 0;
  }
  if (a === null) {
    return 1;
  }
  if (b === null) {
    return -1;
  }

  let cmp: number;
  if (typeof a === 'string' && typeof b === 'string') {
    cmp = a.localeCompare(b);
  } else {
    // Plain numeric comparison — never coerced through a formatted string,
    // so decimal(28,10)-scale values (e.g. ANVL's $0.0005326) compare on
    // their real magnitude, not on a rounded display form.
    cmp = a < b ? -1 : a > b ? 1 : 0;
  }
  return direction === 'desc' ? -cmp : cmp;
}

export function createTableState<T, K extends string>(config: TableStateConfig<T, K>): TableState<T, K> {
  const pageSizeOptions = config.pageSizeOptions ?? DEFAULT_PAGE_SIZE_OPTIONS;

  const sort = signal<TableSort<K>>(config.defaultSort);
  const rawPageIndex = signal(0);
  const pageSize = signal(config.initialPageSize ?? pageSizeOptions[0]);

  const total = computed(() => config.rows().length);

  const sorted = computed<T[]>(() => {
    const { active, direction } = sort();
    const accessor = config.columns[active];
    const copy = config.rows().slice();
    copy.sort((a, b) => {
      const primary = compareSortValues(accessor(a), accessor(b), direction);
      if (primary !== 0) {
        return primary;
      }
      return config.tiebreak ? config.tiebreak(a, b) : 0;
    });
    return copy;
  });

  const pageCount = computed(() => {
    if (pageSize() === ALL_ROWS) {
      return 1;
    }
    return Math.max(1, Math.ceil(total() / pageSize()));
  });

  // Clamped, never the raw requested index — so a delete on the last page
  // (or a filter that shrinks the row count) lands on the new last valid
  // page instead of stranding the user on one that no longer has any rows.
  const pageIndex = computed(() => Math.min(Math.max(rawPageIndex(), 0), pageCount() - 1));

  const paged = computed<T[]>(() => {
    const all = sorted();
    if (pageSize() === ALL_ROWS) {
      return all;
    }
    const start = pageIndex() * pageSize();
    return all.slice(start, start + pageSize());
  });

  return {
    sort,
    pageIndex,
    pageSize,
    pageSizeOptions,
    total,
    pageCount,
    sorted,
    paged,
    setSort(next: TableSort<K>): void {
      sort.set(next);
      rawPageIndex.set(0);
    },
    setPageIndex(index: number): void {
      rawPageIndex.set(Math.max(0, index));
    },
    setPageSize(size: number): void {
      pageSize.set(size);
      rawPageIndex.set(0);
    },
    resetPage(): void {
      rawPageIndex.set(0);
    },
  };
}
