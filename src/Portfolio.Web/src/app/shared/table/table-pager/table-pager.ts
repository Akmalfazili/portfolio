import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MatButtonToggleModule, MatButtonToggleChange } from '@angular/material/button-toggle';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';

import { ALL_ROWS, Pageable } from '../table-state';

/**
 * Page-size toggle (25/50/100/All) + `mat-paginator` for prev/next, shared by
 * all four tables. Not `mat-table`-coupled — `MatPaginator` is a standalone
 * component that only needs `length`/`pageIndex`/`pageSize` inputs and a
 * `(page)` output, so it works fine pointed at a hand-rolled `<table>`.
 *
 * `mat-paginator`'s own page-SIZE dropdown has no way to render one option as
 * the word "All" (its template renders each `pageSizeOptions` entry as a bare
 * number) without forking Material's own template, so that control is a
 * separate `mat-button-toggle-group` here instead — a pattern already used
 * elsewhere in this app (the asset-class filter, the allocation-basis
 * toggle) — and `mat-paginator` itself runs with `hidePageSize`, handling
 * only prev/next/first/last and the "X–Y of Z" range label. `ALL_ROWS`
 * (`Infinity`) is translated to the real row count for `mat-paginator`'s own
 * `pageSize` input, which naturally collapses it to a single page with
 * prev/next auto-disabled.
 *
 * Hidden entirely when the table's total row count fits the smallest page
 * size — a pager under six holdings is noise, not a feature.
 */
@Component({
  selector: 'app-table-pager',
  standalone: true,
  imports: [MatButtonToggleModule, MatPaginatorModule],
  templateUrl: './table-pager.html',
  styleUrl: './table-pager.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TablePager {
  readonly pager = input.required<Pageable>();
  /** Plain-English label for what a row is, used in the toggle group's aria-label. */
  readonly itemLabel = input('rows');

  readonly ALL_ROWS = ALL_ROWS;

  readonly sizeOptions = computed<number[]>(() => [...this.pager().pageSizeOptions, ALL_ROWS]);

  readonly hidden = computed(() => this.pager().total() <= this.pager().pageSizeOptions[0]);

  /** `mat-paginator`'s own `pageSize` input — never `Infinity`, which it
   *  cannot render a range label for. */
  readonly effectivePageSize = computed(() => {
    const size = this.pager().pageSize();
    return size === ALL_ROWS ? Math.max(this.pager().total(), 1) : size;
  });

  sizeLabel(size: number): string {
    return size === ALL_ROWS ? 'All' : String(size);
  }

  onSizeChange(event: MatButtonToggleChange): void {
    this.pager().setPageSize(event.value as number);
  }

  onPage(event: PageEvent): void {
    this.pager().setPageIndex(event.pageIndex);
  }
}
