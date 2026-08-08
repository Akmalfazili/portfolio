import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { httpResource, HttpClient } from '@angular/common/http';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';

import { AssetDto, QuoteProviderKind, UpdateAssetRequest } from '../../core/api/models';
import { API_ROUTES } from '../../core/api/api-routes';
import { StateMessage } from '../../shared/state-message/state-message';
import { ConfirmDialog, ConfirmDialogData } from '../../shared/confirm-dialog/confirm-dialog';
import { AssetFormDialog, AssetFormDialogData, AssetFormDialogResult } from './asset-form.dialog';

/** Symbol ascending — stable regardless of active/inactive status, so
 *  toggling one doesn't jump it around the list. */
function sortAssets(assets: AssetDto[]): AssetDto[] {
  return [...assets].sort((a, b) => a.symbol.localeCompare(b.symbol));
}

/**
 * D27 — how long total price silence has to last before it stops being normal
 * and starts suggesting the identifier is wrong.
 *
 * These are not arbitrary. A US or SGX stock added on a Friday evening cannot
 * be priced until the exchange reopens on Monday, because the refresh service
 * deliberately never polls a closed market — about 65 hours of entirely correct
 * silence. A threshold under that would fire on every stock added over a
 * weekend, which is precisely the false alarm that teaches someone to ignore
 * the warning. Crypto has no such excuse: CoinGecko is unmetered, never gated,
 * and polled every two minutes, so a day without a single price is already
 * evidence of a bad coin id.
 *
 * Note the backfill cannot rescue a new asset either — it only runs for assets
 * that have transactions, so a newly tracked holding with no trades yet depends
 * entirely on the refresh cycle.
 */
const STALE_AFTER_DAYS: Record<QuoteProviderKind, number> = {
  TwelveData: 3,
  Yahoo: 3,
  CoinGecko: 1,
};

const MS_PER_DAY = 24 * 60 * 60 * 1000;

/** D27 view state for one row: null when there is nothing to say. */
export interface UnpricedState {
  /** Whole days since the asset was added. */
  readonly days: number;
  /** Past this provider's threshold — worth pointing at the identifier. */
  readonly suspicious: boolean;
}

export interface AssetRow {
  readonly asset: AssetDto;
  readonly unpriced: UnpricedState | null;
}

function unpricedState(asset: AssetDto, now: number): UnpricedState | null {
  // An inactive asset is excluded from every refresh cycle, so its silence is
  // expected and says nothing about whether its identifier is correct.
  if (asset.hasEverBeenPriced || !asset.isActive) {
    return null;
  }

  const days = Math.floor((now - new Date(asset.createdAt).getTime()) / MS_PER_DAY);
  return { days, suspicious: days >= STALE_AFTER_DAYS[asset.quoteProviderKind] };
}

/**
 * D24 — list / create / deactivate for the assets the app can track. There
 * is no separate deactivate endpoint (`PUT /api/assets/{id}` is a full
 * replace, and `IsActive` is the only field this page ever flips), and no
 * DELETE at all, so "deactivate" and "reactivate" are the same action in
 * both directions here.
 *
 * `GET /api/assets` returns inactive assets too (confirmed live) — this is
 * the ONE place they should still be visible, with a clear inactive
 * treatment, since it's also where you'd go to reactivate one. The
 * transaction form's own asset picker filters them out
 * (`TransactionFormDialog.assetOptions`) — a deactivated asset must not be
 * selectable there.
 */
@Component({
  selector: 'app-asset-management-page',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, StateMessage],
  templateUrl: './asset-management.page.html',
  styleUrl: './asset-management.page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AssetManagementPage {
  private readonly dialog = inject(MatDialog);
  private readonly http = inject(HttpClient);

  private readonly assetsResource = httpResource<AssetDto[]>(() => API_ROUTES.assets);

  readonly isLoading = this.assetsResource.isLoading;
  readonly hasError = computed(() => this.assetsResource.error() != null);
  readonly assets = computed(() => sortAssets(this.assetsResource.value() ?? []));
  readonly isEmpty = computed(() => !this.isLoading() && !this.hasError() && this.assets().length === 0);

  /**
   * D27 — rows decorated with their unpriced state. `Date.now()` is read once
   * per recomputation rather than tracked as a signal: this is a page, not a
   * live clock, and the distinction it draws is measured in days, so a value
   * that refreshes when the list does is precise enough by three orders of
   * magnitude.
   */
  readonly rows = computed<AssetRow[]>(() => {
    const now = Date.now();
    return this.assets().map((asset) => ({ asset, unpriced: unpricedState(asset, now) }));
  });

  readonly actionError = signal<string | null>(null);

  retry(): void {
    this.assetsResource.reload();
  }

  dismissActionError(): void {
    this.actionError.set(null);
  }

  openCreateDialog(): void {
    const ref = this.dialog.open<AssetFormDialog, AssetFormDialogData, AssetFormDialogResult>(AssetFormDialog, {
      data: { mode: 'create' },
    });

    ref.afterClosed().subscribe((result) => {
      if (result?.kind === 'saved') {
        this.assetsResource.update((list) => sortAssets([...(list ?? []), result.asset]));
      }
    });
  }

  toggleActive(asset: AssetDto): void {
    const activating = !asset.isActive;
    const ref = this.dialog.open<ConfirmDialog, ConfirmDialogData, boolean>(ConfirmDialog, {
      data: {
        title: activating ? 'Reactivate asset?' : 'Deactivate asset?',
        message: activating
          ? `${asset.symbol} will be selectable again when recording a new transaction.`
          : `${asset.symbol} will no longer be selectable when recording a new transaction. Its existing holdings and transaction history are unaffected.`,
        confirmLabel: activating ? 'Reactivate' : 'Deactivate',
        destructive: !activating,
      },
    });

    ref.afterClosed().subscribe((confirmed) => {
      if (!confirmed) {
        return;
      }

      this.actionError.set(null);
      const previous = asset;
      const next: AssetDto = { ...asset, isActive: activating };
      // Optimistic — rolled back below if the PUT fails.
      this.assetsResource.update((list) => (list ?? []).map((a) => (a.id === asset.id ? next : a)));

      const request: UpdateAssetRequest = {
        symbol: asset.symbol,
        name: asset.name,
        assetClass: asset.assetClass,
        exchange: asset.exchange,
        currency: asset.currency,
        quoteProviderKind: asset.quoteProviderKind,
        providerSymbol: asset.providerSymbol,
        providerCoinId: asset.providerCoinId,
        isActive: activating,
      };

      this.http.put<AssetDto>(API_ROUTES.asset(asset.id), request).subscribe({
        error: () => {
          this.actionError.set(
            `Couldn't ${activating ? 'reactivate' : 'deactivate'} ${asset.symbol} — it has been restored.`,
          );
          this.assetsResource.update((list) => (list ?? []).map((a) => (a.id === asset.id ? previous : a)));
        },
      });
    });
  }
}
