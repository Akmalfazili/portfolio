import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { Router } from '@angular/router';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';

import { AllocationItemDto, AssetClass } from '../../core/api/models';
import { PortfolioApi } from '../../core/api/portfolio.api';
import { reloadOnRefreshCycle } from '../../core/prices/reload-on-refresh-cycle';
import { MoneyPipe } from '../../shared/pipes/money.pipe';
import { StateMessage } from '../../shared/state-message/state-message';
import { StatTile } from '../../shared/stat-tile/stat-tile';
import { GainLoss } from '../../shared/gain-loss/gain-loss';
import { CostVsMarketChart } from '../../shared/charts/cost-vs-market-chart';
import { formatDateOnlyLong } from '../../shared/util/local-date';
import { resourceState } from '../../shared/util/resource-state';
import { AllocationPieChart, AllocationSlice } from './components/allocation-pie-chart';
import { AnnualReturnChart } from './components/annual-return-chart';
import { HoldingsTable } from './components/holdings-table';

/**
 * "AAPL", "AAPL and MSFT", "AAPL, MSFT and TSLA" — an Oxford-comma-free join
 * matching the prose style used elsewhere in this file's caveats (e.g.
 * `unpricedCaveat`/`dividendsCaveat` above).
 */
function joinSymbols(symbols: string[]): string {
  if (symbols.length <= 1) {
    return symbols.join('');
  }
  return `${symbols.slice(0, -1).join(', ')} and ${symbols.at(-1)}`;
}

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
    CostVsMarketChart,
    HoldingsTable,
  ],
  templateUrl: './portfolio-overview.page.html',
  styleUrl: './portfolio-overview.page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PortfolioOverviewPage {
  readonly assetClass = input.required<AssetClass>();

  private readonly router = inject(Router);
  private readonly portfolioApi = inject(PortfolioApi);

  private readonly summaryResource = this.portfolioApi.summary(this.assetClass);
  private readonly allocationResource = this.portfolioApi.allocation(this.assetClass);
  private readonly annualReturnsResource = this.portfolioApi.annualReturns(this.assetClass);
  private readonly performanceResource = this.portfolioApi.performance(this.assetClass);

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

  readonly isEmpty = computed(
    () => !this.isLoading() && !this.hasError() && this.holdings().length === 0,
  );

  private readonly allocationState = resourceState(this.allocationResource);

  /** Allocation panel's own loading/error state — deliberately NOT folded
   *  into the page-level `isLoading`/`hasError` above; see the comment there. */
  readonly allocationIsLoading = this.allocationState.isLoading;
  readonly allocationHasError = this.allocationState.hasError;

  private readonly annualReturnsState = resourceState(this.annualReturnsResource);

  /** Annual-return chart's own loading/error state — same reasoning as the allocation panel. */
  readonly annualReturnsIsLoading = this.annualReturnsState.isLoading;
  readonly annualReturnsHasError = this.annualReturnsState.hasError;

  private readonly performanceState = resourceState(this.performanceResource);

  /** Cost-vs-market panel's own loading/error state — same reasoning as the
   *  allocation panel and annual-return chart above: never folded into the
   *  page-level `isLoading`/`hasError`, so a slow/failed portfolio-wide
   *  performance call never masks already-loaded summary data. */
  readonly performanceIsLoading = this.performanceState.isLoading;
  readonly performanceHasError = this.performanceState.hasError;

  readonly performancePoints = computed(() => this.performanceState.value()?.points ?? []);

  /** Stocks currently held with no price history at all — absent from both
   *  lines on the chart, not a loss (D17-shaped: absence must be captioned,
   *  never rendered as a silent zero). */
  readonly unchartedSymbols = computed(() => this.performanceState.value()?.unchartedSymbols ?? []);
  readonly hasUnchartedSymbols = computed(() => this.unchartedSymbols().length > 0);
  readonly unchartedCaveat = computed(() => {
    const symbols = this.unchartedSymbols();
    const verb = symbols.length === 1 ? 'has' : 'have';
    return `${joinSymbols(symbols)} ${verb} no price history yet — left out of both lines, not a loss.`;
  });

  /**
   * The chart ends on the last stored CLOSE, while the "Total market value"
   * tile above it reads the LIVE price — the two numbers will differ
   * intraday by design (price honesty: a close is never presented as live).
   * This caption names the reason rather than leaving it to look like a
   * mismatch bug. Formatted with the local-date helper, never
   * `toISOString()` — see `shared/util/local-date.ts`'s header comment.
   * `null` (never rendered) when there are no points to date.
   */
  readonly performanceLastDateLabel = computed(() => {
    const last = this.performancePoints().at(-1);
    return last ? formatDateOnlyLong(last.date) : null;
  });

  /** Portfolio-scoped empty-state copy — the chart's own default text is
   *  position-specific ("once the position has transactions…") and doesn't
   *  apply to this whole-portfolio panel. */
  readonly performanceEmptyMessage =
    'No priced history yet — this chart fills in once your stocks have transactions and daily closes.';

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
  readonly hasUncoveredDividends = computed(
    () => this.isStock() && this.dividendsUncoveredCount() > 0,
  );
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
  readonly allocationValueLabel = computed(() =>
    this.allocationMode() === 'market' ? 'Market value' : 'Cost basis',
  );

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
        this.performanceResource.reload();
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
      this.performanceResource.reload();
    }
  }

  retryAllocation(): void {
    this.allocationResource.reload();
  }

  retryAnnualReturns(): void {
    this.annualReturnsResource.reload();
  }

  retryPerformance(): void {
    this.performanceResource.reload();
  }

  goToTransactions(): void {
    void this.router.navigate(['/transactions']);
  }
}
