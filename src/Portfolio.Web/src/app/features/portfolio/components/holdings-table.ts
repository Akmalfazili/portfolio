import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatSortModule, Sort } from '@angular/material/sort';
import { MatIconModule } from '@angular/material/icon';

import { DividendCoverageStatus, HoldingDto } from '../../../core/api/models';
import { MoneyPipe } from '../../../shared/pipes/money.pipe';
import { QuantityPipe } from '../../../shared/pipes/quantity.pipe';
import { GainLoss } from '../../../shared/gain-loss/gain-loss';
import { formatCloseDate } from '../../../shared/util/local-date';
import { TablePager } from '../../../shared/table/table-pager/table-pager';
import { SortValue, TableSort, createTableState } from '../../../shared/table/table-state';

type HoldingColumn =
  | 'symbol'
  | 'quantity'
  | 'costBasis'
  | 'avgCost'
  | 'price'
  | 'marketValue'
  | 'unrealized'
  | 'realized'
  | 'dividends';

/**
 * The table-view twin of the allocation pie and summary tiles — every number
 * a reader might want is here in plain text, not gated behind hovering a
 * chart (anti-patterns.md: "no table view / colour-only encoding").
 *
 * A holding with `currentPriceUsd: null` (a position with no live quote AND
 * no stored close — `priceSource: null`) renders "Awaiting first price"
 * rather than the naive -100% the backend's own numbers would otherwise
 * produce (marketValueUsd 0 minus a real cost basis) — see tracker.md and
 * PortfolioSummaryService's own doc comment.
 *
 * D20 — a holding priced from `priceSource: "Close"` (market closed, no live
 * quote yet, falling back to the last stored daily close) renders a distinct
 * "Close · Fri 24 Jul" caption under the price, using the CLOSE's own date
 * from `priceAsOf` — never presented as if it were a fresh, live number.
 *
 * The "Dividends (12m)" column (2026-08-28) only ever renders when the parent
 * sets `[showDividends]` — the overview page passes `isStock()` explicitly,
 * never inferred from the holding rows themselves, since this table is also
 * used unmodified for the crypto overview and crypto's dividend fields are
 * always `null`. Three states, not two: `Covered` shows the real USD figure
 * (a legitimate `$0.00`), `NotYetFetched` shows the same muted "Awaiting ..."
 * idiom as the price column, and `FetchFailed` gets its own distinct
 * treatment so a failed fetch is never mistaken for "no dividend" or "not
 * checked yet".
 *
 * Sorting/pagination: `mat-sort-header`/`MatPaginator` are standalone and
 * work directly on this hand-rolled `<table>` — no `mat-table` involved. The
 * "unrealized" column sorts on `null` (not the raw `unrealizedPnlUsd`, which
 * the backend always populates even with no price, as a -100%-shaped
 * artefact) whenever there is no price yet, so an "Awaiting first price" row
 * — which shows no number at all — sorts last rather than by a number that
 * was never actually displayed. Variable row height (the D20 close caption,
 * an eventual second line) is why "All" here renders every row unvirtualized
 * rather than through `cdk-virtual-scroll-viewport`'s fixed-size strategy —
 * see tracker.md.
 */
@Component({
  selector: 'app-holdings-table',
  standalone: true,
  imports: [
    RouterLink,
    MoneyPipe,
    QuantityPipe,
    GainLoss,
    MatSortModule,
    MatIconModule,
    TablePager,
  ],
  templateUrl: './holdings-table.html',
  styleUrl: './holdings-table.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HoldingsTable {
  readonly holdings = input.required<HoldingDto[]>();
  readonly basePath = input.required<string>();
  /** Set explicitly by the parent (`isStock()`) — never inferred from the
   *  holding rows, since this table is shared unmodified with the crypto
   *  overview, where every dividend field is `null`. */
  readonly showDividends = input(false);

  private readonly rows = computed(() => this.holdings());

  readonly tableState = createTableState<HoldingDto, HoldingColumn>({
    rows: this.rows,
    columns: {
      symbol: (h) => h.symbol,
      quantity: (h) => h.quantityHeld,
      costBasis: (h) => h.costBasisUsd,
      avgCost: (h) => h.averageCostUsd,
      price: (h) => h.currentPriceUsd,
      marketValue: (h) => h.marketValueUsd,
      // The backend always populates unrealizedPnlUsd, even with no price
      // (a -100%-shaped artefact of costBasis-minus-zero) — sort on what the
      // row actually shows, not that fabricated number.
      unrealized: (h): SortValue => (this.hasPrice(h) ? h.unrealizedPnlUsd : null),
      realized: (h) => h.realizedPnlUsd,
      // Same "sort on null (not a fabricated number)" rule as `unrealized` —
      // a NotYetFetched/FetchFailed row shows no figure at all, so it must
      // sort last rather than by a value that is never actually displayed.
      dividends: (h): SortValue =>
        h.dividendCoverageStatus === 'Covered' ? h.dividendsTrailing12MonthUsd : null,
    },
    defaultSort: { active: 'marketValue', direction: 'desc' },
  });

  onSortChange(sort: Sort): void {
    this.tableState.setSort(sort as TableSort<HoldingColumn>);
  }

  hasPrice(holding: HoldingDto): boolean {
    return holding.currentPriceUsd !== null;
  }

  isCloseSourced(holding: HoldingDto): boolean {
    return holding.priceSource === 'Close';
  }

  closeDateLabel(holding: HoldingDto): string {
    return holding.priceAsOf ? formatCloseDate(holding.priceAsOf) : '';
  }

  dividendCoverage(holding: HoldingDto): DividendCoverageStatus | null {
    return holding.dividendCoverageStatus;
  }
}
