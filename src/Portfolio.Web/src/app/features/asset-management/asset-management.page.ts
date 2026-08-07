import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { httpResource, HttpClient } from '@angular/common/http';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';

import { AssetDto, UpdateAssetRequest } from '../../core/api/models';
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
