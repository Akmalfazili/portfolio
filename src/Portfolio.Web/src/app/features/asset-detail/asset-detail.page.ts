import { ChangeDetectionStrategy, Component, computed, effect, inject, input, untracked } from '@angular/core';
import { httpResource } from '@angular/common/http';
import { Router, RouterLink } from '@angular/router';

import { AssetClass, AssetDto, AssetPerformanceDto, PortfolioSummaryDto, TransactionDto } from '../../core/api/models';
import { API_ROUTES } from '../../core/api/api-routes';
import { PriceStore } from '../../core/prices/price-store';
import { MoneyPipe } from '../../shared/pipes/money.pipe';
import { QuantityPipe } from '../../shared/pipes/quantity.pipe';
import { StateMessage } from '../../shared/state-message/state-message';
import { formatCloseDate } from '../../shared/util/local-date';
import { GainLossCard } from './components/gain-loss-card';
import { CostVsMarketChart } from './components/cost-vs-market-chart';

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
  imports: [RouterLink, MoneyPipe, QuantityPipe, StateMessage, GainLossCard, CostVsMarketChart],
  templateUrl: './asset-detail.page.html',
  styleUrl: './asset-detail.page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AssetDetailPage {
  readonly assetClass = input.required<AssetClass>();
  readonly symbol = input.required<string>();

  private readonly router = inject(Router);
  private readonly priceStore = inject(PriceStore);

  private readonly assetsResource = httpResource<AssetDto[]>(() => API_ROUTES.assets);
  private readonly summaryResource = httpResource<PortfolioSummaryDto>(() =>
    API_ROUTES.portfolioSummary(this.assetClass()),
  );

  readonly isLoading = computed(() => this.assetsResource.isLoading() || this.summaryResource.isLoading());
  readonly hasError = computed(() => this.assetsResource.error() != null || this.summaryResource.error() != null);

  readonly asset = computed(() =>
    (this.assetsResource.value() ?? []).find(
      (candidate) => candidate.symbol === this.symbol() && candidate.assetClass === this.assetClass(),
    ),
  );

  readonly isNotFound = computed(() => !this.isLoading() && !this.hasError() && !this.asset());

  readonly isStock = computed(() => this.assetClass() === 'Stock');

  readonly holding = computed(() => {
    const asset = this.asset();
    if (!asset) {
      return undefined;
    }
    return this.summaryResource.value()?.holdings.find((h) => h.assetId === asset.id);
  });

  readonly hasNoTransactions = computed(
    () => !this.isLoading() && !this.hasError() && !!this.asset() && !this.holding(),
  );

  private readonly performanceResource = httpResource<AssetPerformanceDto | undefined>(() => {
    const asset = this.asset();
    return asset && this.isStock() ? API_ROUTES.assetPerformance(asset.id) : undefined;
  });

  readonly performancePoints = computed(() => this.performanceResource.value()?.points ?? []);

  private readonly transactionsResource = httpResource<TransactionDto[] | undefined>(() => {
    const asset = this.asset();
    return asset ? API_ROUTES.transactionsByAsset(asset.id) : undefined;
  });

  readonly transactions = computed(() => this.transactionsResource.value() ?? []);

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

  private lastAppliedRefreshAt: string | null = null;

  constructor() {
    // Same pattern as PortfolioOverviewPage — react to a completed refresh
    // cycle rather than polling independently (rule #2).
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
        if (this.isStock()) {
          this.performanceResource.reload();
        }
      });
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
