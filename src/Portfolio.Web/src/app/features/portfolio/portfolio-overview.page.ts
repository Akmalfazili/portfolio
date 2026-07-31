import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { httpResource } from '@angular/common/http';
import { Router, RouterLink } from '@angular/router';

import { AssetClass, AssetDto, QuoteUpdateNotification } from '../../core/api/models';
import { API_ROUTES } from '../../core/api/api-routes';
import { PriceStore } from '../../core/prices/price-store';
import { MoneyPipe } from '../../shared/pipes/money.pipe';
import { StateMessage } from '../../shared/state-message/state-message';

/**
 * Shared by both /stocks and /crypto, parameterised by `assetClass` (bound
 * from the route's `data` via `withComponentInputBinding()`). The full
 * allocation pie, annual-return bar and summary tiles land in Phase 9 — this
 * phase proves the real wiring: the route parameterisation, the section
 * accent, live prices from PriceStore, and the loading/empty/error scaffold,
 * against the actual GET /api/assets endpoint.
 */
@Component({
  selector: 'app-portfolio-overview-page',
  standalone: true,
  imports: [RouterLink, MoneyPipe, StateMessage],
  templateUrl: './portfolio-overview.page.html',
  styleUrl: './portfolio-overview.page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PortfolioOverviewPage {
  readonly assetClass = input.required<AssetClass>();

  private readonly router = inject(Router);
  private readonly priceStore = inject(PriceStore);

  private readonly assetsResource = httpResource<AssetDto[]>(() => API_ROUTES.assets);

  readonly isLoading = this.assetsResource.isLoading;
  readonly hasError = computed(() => this.assetsResource.error() != null);

  readonly assetsForSection = computed(() =>
    (this.assetsResource.value() ?? []).filter((asset) => asset.assetClass === this.assetClass()),
  );

  readonly isEmpty = computed(
    () => !this.isLoading() && !this.hasError() && this.assetsForSection().length === 0,
  );

  readonly sectionLabel = computed(() => (this.assetClass() === 'Crypto' ? 'Crypto' : 'Stocks'));

  readonly basePath = computed(() => (this.assetClass() === 'Crypto' ? '/crypto' : '/stocks'));

  priceFor(assetId: number): QuoteUpdateNotification | undefined {
    return this.priceStore.priceFor(assetId);
  }

  retry(): void {
    this.assetsResource.reload();
  }

  goToTransactions(): void {
    void this.router.navigate(['/transactions']);
  }
}
