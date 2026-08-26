import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { httpResource, HttpClient } from '@angular/common/http';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';

import { AssetDto, QuoteProviderKind, TransactionDto, UpdateAssetRequest } from '../../core/api/models';
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

  // D34 — if this asset's provider has never once succeeded for ANY asset in
  // this database, the silence is explained just as well by "the provider has
  // never run" as by "the identifier is wrong", so there is nothing yet to
  // escalate on. A fresh database (or seeded assets whose CreatedAt is a
  // static value) would otherwise make every never-priced asset look days
  // old on day one, regardless of whether its identifier is correct. Stay in
  // the calm "no price yet" state until a sibling on the same provider
  // proves the pipe genuinely works.
  const suspicious = asset.providerHasEverSucceeded && days >= STALE_AFTER_DAYS[asset.quoteProviderKind];
  return { days, suspicious };
}

/**
 * D24 — list / create / deactivate / delete for the assets the app can track.
 * There is no separate deactivate endpoint (`PUT /api/assets/{id}` is a full
 * replace, and `IsActive` is the only field this page ever flips), so
 * "deactivate" and "reactivate" are the same action in both directions here.
 *
 * `DELETE /api/assets/{id}` is a different thing entirely and the only
 * irreversible action on the page: it destroys the asset AND every child row
 * — transactions, price history, quote. Deactivation is offered first and
 * left as the plain-text button; delete is styled as the warn action and its
 * confirmation states the transaction count out loud, because "3 transactions
 * will be permanently deleted" is the fact that changes the answer and the
 * row itself does not show it.
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

  /** The asset currently being deleted — its row's buttons are disabled while the
   *  DELETE is in flight. Deletion is pessimistic, unlike the deactivate toggle:
   *  showing a row vanish and then reappear on failure reads as data loss. */
  readonly deletingId = signal<number | null>(null);

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

  /**
   * Permanent delete, cascading to every child row on the server.
   *
   * The transaction count is fetched first, purely so the confirmation can
   * name it. That costs one extra GET on an explicit click, and it is worth
   * it: the row shows symbol, provider and status but nothing about how much
   * history hangs off it, and "this will also delete 47 transactions" is the
   * only fact that would make someone press Cancel. If that GET fails the
   * delete is still offered — with the vaguer wording, never with a made-up
   * count of zero, which would understate exactly the risk being warned about.
   */
  deleteAsset(asset: AssetDto): void {
    this.actionError.set(null);

    this.http.get<TransactionDto[]>(API_ROUTES.transactionsByAsset(asset.id)).subscribe({
      next: (transactions) => this.confirmDelete(asset, transactions.length),
      error: () => this.confirmDelete(asset, null),
    });
  }

  private confirmDelete(asset: AssetDto, transactionCount: number | null): void {
    const children =
      transactionCount === null
        ? 'Its transactions and price history will be permanently deleted with it.'
        : transactionCount === 0
          ? 'It has no transactions. Its price history will be permanently deleted with it.'
          : `Its ${transactionCount} transaction${transactionCount === 1 ? '' : 's'} and all of its price history will be permanently deleted with it.`;

    const ref = this.dialog.open<ConfirmDialog, ConfirmDialogData, boolean>(ConfirmDialog, {
      data: {
        title: `Delete ${asset.symbol}?`,
        message: `${children} This cannot be undone — deactivate instead if you only want to stop recording new transactions against it.`,
        confirmLabel: 'Delete',
        destructive: true,
      },
    });

    ref.afterClosed().subscribe((confirmed) => {
      if (!confirmed) {
        return;
      }

      this.deletingId.set(asset.id);

      this.http.delete<void>(API_ROUTES.asset(asset.id)).subscribe({
        next: () => {
          this.deletingId.set(null);
          this.assetsResource.update((list) => (list ?? []).filter((a) => a.id !== asset.id));
        },
        error: () => {
          this.deletingId.set(null);
          this.actionError.set(`Couldn't delete ${asset.symbol} — it is unchanged.`);
        },
      });
    });
  }
}
