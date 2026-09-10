import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { MatSortModule, Sort } from '@angular/material/sort';
import { ScrollingModule } from '@angular/cdk/scrolling';

import { AssetClass, DividendPaymentDto, TransactionDto } from '../../core/api/models';
import { AssetsApi } from '../../core/api/assets.api';
import { PortfolioApi } from '../../core/api/portfolio.api';
import { TransactionsApi } from '../../core/api/transactions.api';
import { PriceStore } from '../../core/prices/price-store';
import { reloadOnRefreshCycle } from '../../core/prices/reload-on-refresh-cycle';
import { MoneyPipe } from '../../shared/pipes/money.pipe';
import { QuantityPipe } from '../../shared/pipes/quantity.pipe';
import { StateMessage } from '../../shared/state-message/state-message';
import { StatTile } from '../../shared/stat-tile/stat-tile';
import { formatCloseDate } from '../../shared/util/local-date';
import { resourceState } from '../../shared/util/resource-state';
import { TablePager } from '../../shared/table/table-pager/table-pager';
import { ALL_ROWS, TableSort, createTableState } from '../../shared/table/table-state';
import { TRANSACTION_ROW_HEIGHT_PX } from '../../shared/table/table-row-height';
import { VirtualRowgroup } from '../../shared/table/virtual-rowgroup';
import { GainLossCard } from './components/gain-loss-card';
import { CostVsMarketChart } from './components/cost-vs-market-chart';

type TransactionColumn = 'date' | 'type' | 'quantity' | 'price' | 'fees';
type DividendColumn = 'exDate' | 'amount' | 'units' | 'income';

/**
 * Shared by /stocks/:symbol and /crypto/:symbol. `symbol` and `assetClass`
 * both arrive via `withComponentInputBinding()` — the former from the route
 * param, the latter from the route's static `data`.
 *
 * Crypto gets NO cost-vs-market line chart and NO range selector — it keeps
 * no price history at all (tracker.md's crypto-scope decision) — so
 * `performanceResource` is never even requested for it (an `undefined`
 * httpResource url), and `CostVsMarketChart` is omitted from the template
 * entirely rather than rendered empty or flat.
 */
@Component({
  selector: 'app-asset-detail-page',
  standalone: true,
  imports: [
    RouterLink,
    MoneyPipe,
    QuantityPipe,
    StateMessage,
    StatTile,
    GainLossCard,
    CostVsMarketChart,
    MatSortModule,
    TablePager,
    ScrollingModule,
    VirtualRowgroup,
  ],
  templateUrl: './asset-detail.page.html',
  styleUrl: './asset-detail.page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AssetDetailPage {
  readonly assetClass = input.required<AssetClass>();
  readonly symbol = input.required<string>();

  private readonly router = inject(Router);
  private readonly priceStore = inject(PriceStore);
  private readonly assetsApi = inject(AssetsApi);
  private readonly portfolioApi = inject(PortfolioApi);
  private readonly transactionsApi = inject(TransactionsApi);

  private readonly assetsResource = this.assetsApi.list();
  private readonly summaryResource = this.portfolioApi.summary(this.assetClass);

  /**
   * `resourceState` (`shared/util/resource-state.ts`, built on
   * `lastGoodValue`) — NOT the resource's own `.value()` — is what the page
   * renders from. Two distinct reasons, both from `httpResource.reload()`'s
   * behaviour (fired every time `PriceStore` completes a refresh cycle — see
   * the constructor effect below, and `retry()`, which reloads
   * `assetsResource` too):
   *
   * 1. A reload that is merely IN FLIGHT preserves the previous value while
   *    `isLoading()` flips back to `true`. Gating content on `isLoading()`
   *    alone unmounted and rebuilt the whole page every 2 minutes even
   *    though nothing was actually missing.
   * 2. A reload that FAILS does NOT preserve the previous value — the
   *    resolved stream is replaced wholesale with `{ error }`, so
   *    `hasValue()` alone is not enough to keep showing good data across a
   *    failed background reload.
   *
   * See tracker.md's "reload must not blank the page" entry. EVERY resource
   * on this page goes through it — `performanceResource` used to be the
   * exception, and a single failed cycle reload blanked the cost-vs-market
   * chart while its siblings on the same page rode the failure out.
   */
  private readonly assetsState = resourceState(this.assetsResource);
  private readonly summaryState = resourceState(this.summaryResource);

  /** The page shell needs BOTH the asset list (to resolve the symbol) and the
   *  summary (for the holding), so the two states compose with `||` — each
   *  resource still judges itself by its own cached value. */
  readonly isLoading = computed(
    () => this.assetsState.isLoading() || this.summaryState.isLoading(),
  );
  /**
   * A *reload* that fails while good data is already on screen must not
   * blow the page away — `PriceStore`'s toolbar refresh indicator already
   * surfaces refresh health (rule #3). Only a failure with no value EVER
   * loaded blocks the page — a deliberate choice, not an oversight, and the
   * reason `resourceState.hasError` gates on the cached value.
   */
  readonly hasError = computed(() => this.assetsState.hasError() || this.summaryState.hasError());

  readonly asset = computed(() =>
    (this.assetsState.value() ?? []).find(
      (candidate) =>
        candidate.symbol === this.symbol() && candidate.assetClass === this.assetClass(),
    ),
  );

  readonly isNotFound = computed(() => !this.isLoading() && !this.hasError() && !this.asset());

  readonly isStock = computed(() => this.assetClass() === 'Stock');

  /**
   * `averageCostUsd` is always USD, while the hero price above it is in the
   * asset's native currency (`q.currency` / `found.currency`). For a USD
   * asset those are the same unit and labelling both would just be noise.
   * For Z74 (SGD) they are NOT the same unit — same class of silent
   * unit-mismatch mistake as D4/D20 — so the avg cost label spells out
   * "(USD)" whenever the asset's native currency isn't USD, rather than
   * letting two dollar-shaped numbers sit side by side unlabelled.
   */
  readonly isUsdNative = computed(() => this.asset()?.currency === 'USD');

  readonly holding = computed(() => {
    const asset = this.asset();
    if (!asset) {
      return undefined;
    }
    return this.summaryState.value()?.holdings.find((h) => h.assetId === asset.id);
  });

  readonly hasNoTransactions = computed(
    () => !this.isLoading() && !this.hasError() && !!this.asset() && !this.holding(),
  );

  private readonly performanceResource = this.assetsApi.performance(() => {
    const asset = this.asset();
    return asset && this.isStock() ? asset.id : undefined;
  });

  /**
   * Through `resourceState` for the same reason as everything else on this
   * page, and more urgently: this resource is reloaded on EVERY completed
   * refresh cycle (see the constructor), so it is the one most exposed to a
   * transient failure. Reading `performanceResource.value()` raw — which is
   * what this did — meant a single failed ~5-minute reload replaced the
   * resolved stream with `{ error }`, while the summary and dividends panels
   * beside it rode the same failure out untouched.
   *
   * Measured, not assumed, and WORSE than the "chart goes blank" the review
   * predicted: Angular's `ResourceImpl.value` computed THROWS
   * ("Resource is currently in an error state") once the resource errors, so
   * the raw read blew up inside change detection rather than quietly
   * returning `undefined`. `lastGoodValue` never hits that path — its source
   * guards on `hasValue()` first. See the regression test in
   * `asset-detail.page.spec.ts`, which reproduces the throw against the
   * pre-fix component.
   */
  private readonly performanceState = resourceState(this.performanceResource);

  readonly performancePoints = computed(() => this.performanceState.value()?.points ?? []);

  /**
   * Dividend income tracking (2026-08-28) — stocks only, same
   * asset-must-resolve-first shape as `performanceResource`: the endpoint
   * 400s for a crypto asset id, so this is never even requested for one (an
   * `undefined` httpResource url), not called and discarded.
   */
  private readonly dividendsResource = this.assetsApi.dividends(() => {
    const asset = this.asset();
    return asset && this.isStock() ? asset.id : undefined;
  });

  /** Same `resourceState` rule as the page-level `isLoading`/`hasError`
   *  above — `retryDividends()` reloads this resource, and neither an
   *  in-flight reload nor a FAILED one may blank a payment table that's
   *  already on screen (see the long comment on `assetsState` for why a
   *  failed reload needs the cache and `hasValue()` alone isn't enough). */
  private readonly dividendsState = resourceState(this.dividendsResource);

  readonly isDividendsLoading = this.dividendsState.isLoading;
  readonly dividendHistory = this.dividendsState.value;
  readonly dividendCoverageStatus = computed(() => this.dividendHistory()?.coverageStatus ?? null);
  readonly dividendPayments = computed(() => this.dividendHistory()?.payments ?? []);
  readonly hasDividendPayments = computed(() => this.dividendPayments().length > 0);

  /**
   * `FetchFailed` (the backend's own honest classification of the last
   * background attempt) and a genuine HTTP failure on THIS request are both
   * "we don't have reliable dividend data right now" from the reader's point
   * of view, so they share one error treatment with a retry — retrying just
   * re-fetches the current (possibly since-recovered) state, it does not
   * force a new Yahoo call itself.
   */
  readonly dividendsUnavailable = computed(
    () => this.dividendsState.hasError() || this.dividendCoverageStatus() === 'FetchFailed',
  );

  readonly dividendTableState = createTableState<DividendPaymentDto, DividendColumn>({
    rows: this.dividendPayments,
    columns: {
      exDate: (p) => p.exDate,
      amount: (p) => p.amountPerShareNative,
      units: (p) => p.unitsHeldAtExDate,
      income: (p) => p.incomeUsd,
    },
    // Newest ex-date first, matching the backend's own ordering.
    defaultSort: { active: 'exDate', direction: 'desc' },
  });

  onDividendSortChange(sort: Sort): void {
    this.dividendTableState.setSort(sort as TableSort<DividendColumn>);
  }

  retryDividends(): void {
    this.dividendsResource.reload();
  }

  private readonly transactionsResource = this.transactionsApi.byAsset(() => {
    const asset = this.asset();
    return asset ? asset.id : undefined;
  });

  /** Latent rather than live today — nothing reloads this resource on the
   *  refresh cycle — but it had the identical unprotected shape
   *  `performanceResource` did, so it gets the same treatment rather than
   *  waiting to become a second instance of the same defect. */
  private readonly transactionsState = resourceState(this.transactionsResource);

  readonly transactions = computed(() => this.transactionsState.value() ?? []);

  /** Sorting/pagination — see `shared/table/table-state.ts`. Same default
   *  sort (trade date descending, stable id tiebreak) as the main
   *  transactions page, since both read the same backend ordering. Price/fees
   *  are the transaction's own native currency — see tracker.md's mixed-
   *  currency sort caveat, same as the main transactions table. */
  readonly tableState = createTableState<TransactionDto, TransactionColumn>({
    rows: this.transactions,
    columns: {
      date: (t) => t.tradeDate,
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

  readonly quote = computed(() => {
    const asset = this.asset();
    return asset ? this.priceStore.priceFor(asset.id) : undefined;
  });

  /** Falls back to the last persisted quote (from the summary snapshot) before any live SignalR push has arrived. */
  readonly fallbackPrice = computed(() => {
    const holding = this.holding();
    return holding?.currentPriceNative ?? null;
  });

  /**
   * D20 — a price that is really the last stored CLOSE must render distinctly,
   * labelled with the close's own date, never presented as if it were fresh.
   *
   * D4 corrected the original version of this, which read `quote() === undefined
   * && ...` on the stated reasoning that "a genuine SignalR push is always live".
   * That was false, and false in exactly the case D4 is about: on an unmodelled
   * SGX lunar holiday the refresh service polls anyway, Yahoo returns the
   * previous session's close, and it is pushed over the hub like any other tick.
   * A push therefore *overrode* the honest label rather than confirming it —
   * making this the one place the stale price still read as live.
   *
   * Both sources now answer the same question the same way, and whichever is
   * being displayed is the one asked.
   */
  readonly isCloseSourced = computed(() => {
    const pushed = this.quote();
    return pushed ? pushed.source === 'Close' : this.holding()?.priceSource === 'Close';
  });

  /** The as-of instant belonging to whichever price is actually on screen. */
  readonly closeDateLabel = computed(() => {
    const asOf = this.quote()?.asOf ?? this.holding()?.priceAsOf;
    return asOf ? formatCloseDate(asOf) : '';
  });

  constructor() {
    // Same pattern as PortfolioOverviewPage — react to a completed refresh
    // cycle rather than polling independently (rule #2). See
    // core/prices/reload-on-refresh-cycle.ts for the shared semantics.
    reloadOnRefreshCycle(() => {
      this.summaryResource.reload();
      if (this.isStock()) {
        this.performanceResource.reload();
      }
    });
  }

  retry(): void {
    this.assetsResource.reload();
    this.summaryResource.reload();
  }

  goBackToOverview(): void {
    const path = this.assetClass() === 'Crypto' ? '/crypto' : '/stocks';
    void this.router.navigate([path]);
  }
}
