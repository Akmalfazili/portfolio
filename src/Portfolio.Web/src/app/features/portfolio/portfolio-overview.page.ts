import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { httpResource } from '@angular/common/http';
import { Router } from '@angular/router';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';

import { AllocationItemDto, AnnualReturnsDto, AssetClass, PortfolioAllocationDto, PortfolioSummaryDto } from '../../core/api/models';
import { API_ROUTES } from '../../core/api/api-routes';
import { reloadOnRefreshCycle } from '../../core/prices/reload-on-refresh-cycle';
import { MoneyPipe } from '../../shared/pipes/money.pipe';
import { StateMessage } from '../../shared/state-message/state-message';
import { StatTile } from '../../shared/stat-tile/stat-tile';
import { GainLoss } from '../../shared/gain-loss/gain-loss';
import { resourceState } from '../../shared/util/resource-state';
import { AllocationPieChart, AllocationSlice } from './components/allocation-pie-chart';
import { AnnualReturnChart } from './components/annual-return-chart';
import { HoldingsTable } from './components/holdings-table';

type AllocationMode = 'market' | 'cost';

/**
 * Shared by /stocks and /crypto, parameterised by `assetClass` from the
 * route's `data`. Stocks additionally get the annual-return bar chart —
 * crypto keeps no price history at all, so that resource is simply never
 * requested for it (an `undefined` httpResource url, not an empty chart —
 * see tracker.md's crypto-scope decision).
 */
@Component({
  selector: 'app-portfolio-overview-page',
  standalone: true,
  imports: [
    MoneyPipe,
    StateMessage,
    StatTile,
    GainLoss,
    MatButtonToggleModule,
    MatIconModule,
    AllocationPieChart,
    AnnualReturnChart,
    HoldingsTable,
  ],
  templateUrl: './portfolio-overview.page.html',
  styleUrl: './portfolio-overview.page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PortfolioOverviewPage {
  readonly assetClass = input.required<AssetClass>();

  private readonly router = inject(Router);

  private readonly summaryResource = httpResource<PortfolioSummaryDto>(() =>
    API_ROUTES.portfolioSummary(this.assetClass()),
  );
  private readonly allocationResource = httpResource<PortfolioAllocationDto>(() =>
    API_ROUTES.portfolioAllocation(this.assetClass()),
  );
  private readonly annualReturnsResource = httpResource<AnnualReturnsDto | undefined>(() =>
    this.assetClass() === 'Stock' ? API_ROUTES.stockAnnualReturns : undefined,
  );

  /**
   * `resourceState`'s cached value (`shared/util/resource-state.ts`, built on
   * `lastGoodValue`) — NOT the resource's own `.value()` — is what the page
   * renders from. Two distinct reasons, both from `httpResource.reload()`'s
   * behaviour (fired every time `PriceStore` completes a refresh cycle — see
   * the constructor effect below):
   *
   * 1. A reload that is merely IN FLIGHT preserves the previous value while
   *    `isLoading()` flips back to `true` (`_resource-chunk.mjs`'s `state`
   *    carries the prior `stream` forward). Gating content on `isLoading()`
   *    alone therefore unmounted and rebuilt 340-670 DOM nodes, both ECharts
   *    instances, and the holdings table's sort/page state every 2 minutes
   *    even though nothing was actually missing.
   * 2. A reload that FAILS does NOT preserve the previous value — the resolved
   *    stream is replaced wholesale with `{ error }`, so `hasValue()` alone
   *    is not enough to keep showing good data across a failed background
   *    reload. `lastGoodValue` (`shared/util/last-good-value.ts`) covers both.
   *
   * See tracker.md's "reload must not blank the page" entry.
   *
   * Only `summaryResource` gates the page shell: it is the one resource the
   * tiles and holdings table directly depend on. The allocation pie and
   * annual-return chart reload independently and own their own loading/error
   * state below — see `allocationIsLoading`/`annualReturnsIsLoading` — so a
   * slow/failed allocation call never masks summary data that already loaded,
   * and vice versa. Three separate `resourceState`s, deliberately not one.
   */
  private readonly summaryState = resourceState(this.summaryResource);
  readonly summary = this.summaryState.value;

  readonly isLoading = this.summaryState.isLoading;
  /**
   * A *reload* that fails while good data is already on screen must not
   * blow the page away — `PriceStore`'s toolbar refresh indicator already
   * surfaces refresh health (rule #3), so a background reload failure here
   * is silently retried on the next cycle rather than replacing working
   * content with a full-page error. Only a failure with no value EVER
   * loaded blocks the page — a deliberate choice, not an oversight, and the
   * reason `resourceState.hasError` gates on the cached value.
   */
  readonly hasError = this.summaryState.hasError;

  readonly holdings = computed(() => this.summary()?.holdings ?? []);

  readonly isEmpty = computed(() => !this.isLoading() && !this.hasError() && this.holdings().length === 0);

  private readonly allocationState = resourceState(this.allocationResource);

  /** Allocation panel's own loading/error state — deliberately NOT folded
   *  into the page-level `isLoading`/`hasError` above; see the comment there. */
  readonly allocationIsLoading = this.allocationState.isLoading;
  readonly allocationHasError = this.allocationState.hasError;

  private readonly annualReturnsState = resourceState(this.annualReturnsResource);

  /** Annual-return chart's own loading/error state — same reasoning as the allocation panel. */
  readonly annualReturnsIsLoading = this.annualReturnsState.isLoading;
  readonly annualReturnsHasError = this.annualReturnsState.hasError;

  /**
   * D17 — a holding with no price yet contributes `0` to every total, so a
   * *partly* priced portfolio otherwise reads exactly like a genuine crash
   * (the AAPL-only case: cost basis real, market value 0, so the headline
   * shows −82.76%). `unpricedHoldingsCount > 0` means the totals above are
   * partial and must say so, rather than presenting a complete number.
   */
  readonly unpricedHoldingsCount = computed(() => this.summary()?.unpricedHoldingsCount ?? 0);
  readonly hasUnpricedHoldings = computed(() => this.unpricedHoldingsCount() > 0);
  readonly unpricedCaveat = computed(() => {
    const count = this.unpricedHoldingsCount();
    const noun = count === 1 ? 'holding has' : 'holdings have';
    return `${count} ${noun} no price yet — the totals below don't include ${count === 1 ? 'its' : 'their'} market value, not a loss.`;
  });

  /**
   * Same D17 shape applied to dividend income instead of market value:
   * `dividendsUncoveredCount` is how many Stock holdings are NOT `Covered`
   * (`NotYetFetched` or `FetchFailed`), so the dividend total tile below is
   * understated for exactly that many stocks — never presented as complete.
   * Always 0 for Crypto (the summary's field is `0`, not `null`, per contract).
   */
  readonly dividendsUncoveredCount = computed(() => this.summary()?.dividendsUncoveredCount ?? 0);
  readonly hasUncoveredDividends = computed(() => this.isStock() && this.dividendsUncoveredCount() > 0);
  readonly dividendsCaveat = computed(() => {
    const count = this.dividendsUncoveredCount();
    const noun = count === 1 ? 'stock has' : 'stocks have';
    return `${count} ${noun} incomplete dividend data — the total below may understate income, not a real shortfall.`;
  });

  readonly sectionLabel = computed(() => (this.assetClass() === 'Crypto' ? 'Crypto' : 'Stocks'));
  readonly basePath = computed(() => (this.assetClass() === 'Crypto' ? '/crypto' : '/stocks'));
  readonly isStock = computed(() => this.assetClass() === 'Stock');

  readonly annualReturns = computed(() => this.annualReturnsState.value()?.years ?? []);

  readonly allocationMode = signal<AllocationMode>('market');

  private readonly marketSlices = computed<AllocationSlice[]>(() =>
    (this.allocationState.value()?.items ?? []).map((item: AllocationItemDto) => ({
      assetId: item.assetId,
      symbol: item.symbol,
      name: item.name,
      value: item.marketValueUsd,
      percent: item.percentageOfTotal,
      // D17 residual — a 0 here can mean "worth nothing" or "we don't know".
      unpriced: !item.hasPrice,
    })),
  );

  private readonly costSlices = computed<AllocationSlice[]>(() => {
    const summary = this.summary();
    if (!summary) {
      return [];
    }
    const totalCost = summary.totalCostBasisUsd;
    return summary.holdings
      .filter((h) => h.quantityHeld > 0)
      .map((h) => ({
        assetId: h.assetId,
        symbol: h.symbol,
        name: h.name,
        value: h.costBasisUsd,
        percent: totalCost > 0 ? (h.costBasisUsd / totalCost) * 100 : 0,
        // Never unpriced in cost mode: cost basis comes from the transactions,
        // which are known for every holding whether or not a quote ever arrived.
        // This is why the D17 residual only ever affected the market-value pie.
        unpriced: false,
      }));
  });

  readonly allocationSlices = computed(() =>
    this.allocationMode() === 'market' ? this.marketSlices() : this.costSlices(),
  );
  readonly allocationValueLabel = computed(() => (this.allocationMode() === 'market' ? 'Market value' : 'Cost basis'));

  constructor() {
    // Reload the calculation endpoints once a scheduled/manual refresh cycle
    // actually completes — reacting to PriceStore's own signal, never polling
    // independently (rule #2). See core/prices/reload-on-refresh-cycle.ts for
    // the shared skip-first/skip-unchanged semantics.
    reloadOnRefreshCycle(() => {
      this.summaryResource.reload();
      this.allocationResource.reload();
      if (this.isStock()) {
        this.annualReturnsResource.reload();
      }
    });
  }

  setAllocationMode(mode: AllocationMode): void {
    this.allocationMode.set(mode);
  }

  retry(): void {
    this.summaryResource.reload();
    this.allocationResource.reload();
    if (this.isStock()) {
      this.annualReturnsResource.reload();
    }
  }

  retryAllocation(): void {
    this.allocationResource.reload();
  }

  retryAnnualReturns(): void {
    this.annualReturnsResource.reload();
  }

  goToTransactions(): void {
    void this.router.navigate(['/transactions']);
  }
}
