import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { httpResource } from '@angular/common/http';
import { Router } from '@angular/router';

import { AssetClass, AssetDto } from '../../core/api/models';
import { API_ROUTES } from '../../core/api/api-routes';
import { PriceStore } from '../../core/prices/price-store';
import { MoneyPipe } from '../../shared/pipes/money.pipe';
import { StateMessage } from '../../shared/state-message/state-message';

/**
 * Shared by /stocks/:symbol and /crypto/:symbol. `symbol` and `assetClass`
 * both arrive via `withComponentInputBinding()` — the former from the route
 * param, the latter from the route's static `data`.
 *
 * Phase 9 adds the live price header, the cost-vs-market line chart (stocks
 * only — crypto is gain/loss only, see tracker.md's crypto-scope decision)
 * and per-asset transactions. This phase proves the real lookup against
 * GET /api/assets plus the loading/empty/error scaffold.
 */
@Component({
  selector: 'app-asset-detail-page',
  standalone: true,
  imports: [MoneyPipe, StateMessage],
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

  readonly isLoading = this.assetsResource.isLoading;
  readonly hasError = computed(() => this.assetsResource.error() != null);

  readonly asset = computed(() =>
    (this.assetsResource.value() ?? []).find(
      (candidate) => candidate.symbol === this.symbol() && candidate.assetClass === this.assetClass(),
    ),
  );

  readonly isNotFound = computed(() => !this.isLoading() && !this.hasError() && !this.asset());

  readonly quote = computed(() => {
    const asset = this.asset();
    return asset ? this.priceStore.priceFor(asset.id) : undefined;
  });

  readonly isCrypto = computed(() => this.assetClass() === 'Crypto');

  retry(): void {
    this.assetsResource.reload();
  }

  goBackToOverview(): void {
    const path = this.assetClass() === 'Crypto' ? '/crypto' : '/stocks';
    void this.router.navigate([path]);
  }
}
