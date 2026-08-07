import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { httpResource } from '@angular/common/http';
import { Router } from '@angular/router';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';

import { AllocationItemDto, AnnualReturnsDto, AssetClass, PortfolioAllocationDto, PortfolioSummaryDto } from '../../core/api/models';
import { API_ROUTES } from '../../core/api/api-routes';
import { PriceStore } from '../../core/prices/price-store';
import { MoneyPipe } from '../../shared/pipes/money.pipe';
import { StateMessage } from '../../shared/state-message/state-message';
import { StatTile } from '../../shared/stat-tile/stat-tile';
import { GainLoss } from '../../shared/gain-loss/gain-loss';
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
  private readonly priceStore = inject(PriceStore);

  private readonly summaryResource = httpResource<PortfolioSummaryDto>(() =>
    API_ROUTES.portfolioSummary(this.assetClass()),
  );
  private readonly allocationResource = httpResource<PortfolioAllocationDto>(() =>
    API_ROUTES.portfolioAllocation(this.assetClass()),
  );
  private readonly annualReturnsResource = httpResource<AnnualReturnsDto | undefined>(() =>
    this.assetClass() === 'Stock' ? API_ROUTES.stockAnnualReturns : undefined,
  );

  readonly isLoading = computed(() => this.summaryResource.isLoading() || this.allocationResource.isLoading());
  readonly hasError = computed(
    () => this.summaryResource.error() != null || this.allocationResource.error() != null,
  );

  readonly summary = computed(() => this.summaryResource.value());
  readonly holdings = computed(() => this.summary()?.holdings ?? []);

  readonly isEmpty = computed(() => !this.isLoading() && !this.hasError() && this.holdings().length === 0);

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

  readonly sectionLabel = computed(() => (this.assetClass() === 'Crypto' ? 'Crypto' : 'Stocks'));
  readonly basePath = computed(() => (this.assetClass() === 'Crypto' ? '/crypto' : '/stocks'));
  readonly isStock = computed(() => this.assetClass() === 'Stock');

  readonly annualReturns = computed(() => this.annualReturnsResource.value()?.years ?? []);

  readonly allocationMode = signal<AllocationMode>('market');

  private readonly marketSlices = computed<AllocationSlice[]>(() =>
    (this.allocationResource.value()?.items ?? []).map((item: AllocationItemDto) => ({
      assetId: item.assetId,
      symbol: item.symbol,
      name: item.name,
      value: item.marketValueUsd,
      percent: item.percentageOfTotal,
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
      }));
  });

  readonly allocationSlices = computed(() =>
    this.allocationMode() === 'market' ? this.marketSlices() : this.costSlices(),
  );
  readonly allocationValueLabel = computed(() => (this.allocationMode() === 'market' ? 'Market value' : 'Cost basis'));

  private lastAppliedRefreshAt: string | null = null;

  constructor() {
    // Reload the calculation endpoints once a scheduled/manual refresh cycle
    // actually completes — reacting to PriceStore's own signal, never polling
    // independently (rule #2). Skips the very first emission so mount doesn't
    // double-fetch on top of the initial httpResource requests.
    effect(() => {
      const at = this.priceStore.lastRefreshedAt();
      if (at === null || at === this.lastAppliedRefreshAt) {
        return;
      }
      const isFirstObservation = this.lastAppliedRefreshAt === null;
      this.lastAppliedRefreshAt = at;
      if (isFirstObservation) {
        return;
      }
      untracked(() => {
        this.summaryResource.reload();
        this.allocationResource.reload();
        if (this.isStock()) {
          this.annualReturnsResource.reload();
        }
      });
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

  goToTransactions(): void {
    void this.router.navigate(['/transactions']);
  }
}
