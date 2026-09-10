import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { ZakatAssetStatus, ZakatCryptoLineDto, ZakatPaymentDto, ZakatStockLineDto } from '../../core/api/models';
import { AssetsApi } from '../../core/api/assets.api';
import { ZakatApi } from '../../core/api/zakat.api';
import { NotificationService } from '../../core/notifications/notification.service';
import { reloadOnRefreshCycle } from '../../core/prices/reload-on-refresh-cycle';
import { MoneyPipe } from '../../shared/pipes/money.pipe';
import { QuantityPipe } from '../../shared/pipes/quantity.pipe';
import { StateMessage } from '../../shared/state-message/state-message';
import { StatTile } from '../../shared/stat-tile/stat-tile';
import { ConfirmDialog, ConfirmDialogData } from '../../shared/confirm-dialog/confirm-dialog';
import { resourceState } from '../../shared/util/resource-state';
import { formatDateOnlyLong, formatFxAsOf } from '../../shared/util/local-date';
import { AssetFormDialog, AssetFormDialogData, AssetFormDialogResult } from '../asset-management/asset-form.dialog';
import {
  ZakatPaymentFormDialog,
  ZakatPaymentFormDialogData,
  ZakatPaymentFormDialogResult,
} from './zakat-payment-form.dialog';

/** Plain-language label for each of the six statuses (zakat.md §6) — every
 *  one gets its own wording so no two are ever mistakable for each other. */
const STATUS_LABEL: Record<ZakatAssetStatus, string> = {
  Included: 'Included',
  NotHeldAtFiscalYearEnd: 'Not held at year end',
  FiscalYearEndNotConfigured: 'Year end not set',
  NoCloseOnOrBeforeFiscalYearEnd: 'No price on or before year end',
  NoFxRateForCloseDate: 'No FX rate available',
  NoQuote: 'No quote available',
};

/**
 * Visual grouping only — used to pick a badge colour. `included` and
 * `not-held` both count toward the total (the latter as a real zero);
 * `excluded` covers every status that is left OUT of the total, but each
 * still gets its own distinct `STATUS_LABEL` text, per zakat.md §6's rule
 * that no two of the six may ever look the same.
 */
type StatusTone = 'included' | 'not-held' | 'excluded';

function statusTone(status: ZakatAssetStatus): StatusTone {
  if (status === 'Included') {
    return 'included';
  }
  if (status === 'NotHeldAtFiscalYearEnd') {
    return 'not-held';
  }
  return 'excluded';
}

/** Newest `paidOn` first, `id` descending as a stable tiebreak — same shape
 *  as `transactions.page.ts`'s `sortTransactions`. */
function sortPayments(payments: ZakatPaymentDto[]): ZakatPaymentDto[] {
  return [...payments].sort((a, b) => b.paidOn.localeCompare(a.paidOn) || b.id - a.id);
}

function sortLinesBySymbol<T extends { symbol: string }>(lines: T[]): T[] {
  return [...lines].sort((a, b) => a.symbol.localeCompare(b.symbol));
}

/**
 * Zakat on shares (zakat.md). Two clearly separate parts, deliberately never
 * merged into one table: the COMPUTED breakdown (§4) — an estimate derived
 * from market data with caveats attached — and, below it, the payment
 * HISTORY (§3.2) — a fact the user recorded by hand. Never renders a
 * "shortfall"/"you owe" verdict (§2.4): nisab is not encoded here at all,
 * only the zakatable total and 2.5% of it.
 *
 * This is the one page in the app denominated in SGD rather than USD
 * (§2.5) — every money figure below goes through `MoneyPipe` with an
 * explicit `'SGD'` currency, never the app-wide USD default.
 */
@Component({
  selector: 'app-zakat-page',
  standalone: true,
  imports: [MoneyPipe, QuantityPipe, StateMessage, StatTile, MatButtonModule, MatIconModule, MatTooltipModule],
  templateUrl: './zakat.page.html',
  styleUrl: './zakat.page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ZakatPage {
  private readonly dialog = inject(MatDialog);
  private readonly zakatApi = inject(ZakatApi);
  private readonly assetsApi = inject(AssetsApi);
  private readonly notifications = inject(NotificationService);
  private readonly destroyRef = inject(DestroyRef);

  private readonly reportResource = this.zakatApi.report();
  private readonly paymentsResource = this.zakatApi.payments();
  // Only needed so a stock row's "Set year end" button can open
  // AssetFormDialog pre-filled with the matching full AssetDto — the zakat
  // report itself deliberately carries just the fields the calculation
  // needs, not the whole asset record.
  private readonly assetsResource = this.assetsApi.list();

  constructor() {
    // Reload only the computed report on a completed refresh cycle — never
    // `paymentsResource` (hand-entered records) or `assetsResource` (asset
    // metadata), neither of which depends on prices. Same shared hook as
    // PortfolioOverviewPage/AssetDetailPage — see
    // core/prices/reload-on-refresh-cycle.ts. `report()` is read through
    // `resourceState` below (D44's rule), so a background reload cannot blank
    // a report already on screen.
    reloadOnRefreshCycle(() => this.reportResource.reload());
  }

  private readonly reportState = resourceState(this.reportResource);
  readonly report = this.reportState.value;

  readonly isLoading = this.reportState.isLoading;
  readonly hasError = this.reportState.hasError;

  readonly stockLines = computed<ZakatStockLineDto[]>(() => sortLinesBySymbol(this.report()?.stocks ?? []));
  readonly cryptoLines = computed<ZakatCryptoLineDto[]>(() => sortLinesBySymbol(this.report()?.crypto ?? []));

  readonly excludedAssetCount = computed(() => this.report()?.excludedAssetCount ?? 0);
  readonly hasExcluded = computed(() => this.excludedAssetCount() > 0);

  /**
   * Day-one state (zakat.md §8): nothing has a fiscal year end configured
   * yet, so every stock line comes back `FiscalYearEndNotConfigured` and the
   * stock total is a genuine `0` — but a `0` that means "nothing attempted",
   * not "your holdings are worthless". This must read as a setup step, not
   * as a broken or empty page.
   */
  readonly allStocksUnconfigured = computed(() => {
    const stocks = this.stockLines();
    return stocks.length > 0 && stocks.every((s) => s.status === 'FiscalYearEndNotConfigured');
  });

  readonly excludedCaveat = computed(() => {
    const count = this.excludedAssetCount();
    const noun = count === 1 ? 'asset is' : 'assets are';
    return `${count} ${noun} excluded from the totals below — a gap in the data, not a real zero. See each asset's status in the tables below for why.`;
  });

  statusLabel(status: ZakatAssetStatus): string {
    return STATUS_LABEL[status];
  }

  statusTone(status: ZakatAssetStatus): StatusTone {
    return statusTone(status);
  }

  /**
   * The crypto table's single, shared FX rate belongs in the COLUMN HEADER,
   * not repeated per row — every crypto line in one report converts through
   * the same rate. `null` renders the bare header with no sub-label at all:
   * that happens with no included crypto lines, or — defensively — if lines
   * ever disagreed on `fxSource`, which should never happen for a single
   * shared rate but must never be asserted as a provenance we don't actually
   * have (zakat.md §6/§12's three-way-never-collapsed rule, applied to a
   * header instead of a cell).
   */
  readonly cryptoFxHeader = computed<{ text: string; warning: boolean } | null>(() => {
    const sourced = this.cryptoLines().filter((l) => l.fxSource != null);
    if (sourced.length === 0) {
      return null;
    }

    const sources = new Set(sourced.map((l) => l.fxSource));
    if (sources.size > 1) {
      return null;
    }

    const source = sourced[0].fxSource;
    if (source === 'Spot') {
      const fxAsOf = sourced.find((l) => l.fxAsOf)?.fxAsOf;
      return fxAsOf ? { text: `as of ${formatFxAsOf(fxAsOf)}`, warning: false } : null;
    }

    const fxDateUsed = sourced.find((l) => l.fxDateUsed)?.fxDateUsed;
    if (!fxDateUsed) {
      return null;
    }
    const closeLabel = `USD/SGD close · ${formatDateOnlyLong(fxDateUsed)}`;

    if (source === 'DailyCloseSpotUnavailable') {
      return {
        text: `${closeLabel} — live rate unavailable, showing the previous close`,
        warning: true,
      };
    }
    // DailyCloseHistoricalAsOf — correct, expected behaviour for a
    // historical `?asOf=`, not a warning.
    return { text: closeLabel, warning: false };
  });

  // --- Asset lookup, for the "Set/edit fiscal year end" action per row -----

  private readonly assetsById = computed(() => new Map((this.assetsResource.value() ?? []).map((a) => [a.id, a])));
  readonly assetsErrored = computed(() => this.assetsResource.error() != null);
  readonly assetsReady = computed(() => this.assetsResource.hasValue());

  retryAssets(): void {
    this.assetsResource.reload();
  }

  retryReport(): void {
    this.reportResource.reload();
  }

  fiscalYearEndActionLabel(status: ZakatAssetStatus): string {
    return status === 'FiscalYearEndNotConfigured' ? 'Set year end' : 'Edit year end';
  }

  openFiscalYearEndDialog(assetId: number): void {
    const asset = this.assetsById().get(assetId);
    if (!asset) {
      return;
    }

    const ref = this.dialog.open<AssetFormDialog, AssetFormDialogData, AssetFormDialogResult>(AssetFormDialog, {
      data: { mode: 'edit', asset },
    });

    ref.afterClosed().subscribe((result) => {
      if (result?.kind === 'saved') {
        this.assetsResource.update((list) => (list ?? []).map((a) => (a.id === asset.id ? result.asset : a)));
        this.notifications.success(`${result.asset.symbol} updated.`);
        // The report is computed fresh on every read and depends on exactly
        // the field this dialog just changed — reload it so the row moves
        // out of FiscalYearEndNotConfigured without a manual page refresh.
        this.reportResource.reload();
      }
    });
  }

  // --- Payment history (§3.2) — a recorded fact, never derived from the ---
  // --- computed report above. --------------------------------------------

  private readonly paymentsState = resourceState(this.paymentsResource);

  readonly paymentsLoading = this.paymentsState.isLoading;
  readonly paymentsHasError = this.paymentsState.hasError;
  readonly sortedPayments = computed(() => sortPayments(this.paymentsState.value() ?? []));
  readonly paymentsEmpty = computed(
    () => !this.paymentsLoading() && !this.paymentsHasError() && this.sortedPayments().length === 0,
  );

  retryPayments(): void {
    this.paymentsResource.reload();
  }

  openCreatePaymentDialog(): void {
    const ref = this.dialog.open<ZakatPaymentFormDialog, ZakatPaymentFormDialogData, ZakatPaymentFormDialogResult>(
      ZakatPaymentFormDialog,
      { data: { mode: 'create' } },
    );

    ref.afterClosed().subscribe((result) => {
      if (result?.kind === 'saved') {
        this.upsertPayment(result.payment);
        this.notifications.success('Zakat payment recorded.');
      }
    });
  }

  openEditPaymentDialog(payment: ZakatPaymentDto): void {
    const ref = this.dialog.open<ZakatPaymentFormDialog, ZakatPaymentFormDialogData, ZakatPaymentFormDialogResult>(
      ZakatPaymentFormDialog,
      { data: { mode: 'edit', payment } },
    );

    ref.afterClosed().subscribe((result) => {
      if (result?.kind === 'saved') {
        this.upsertPayment(result.payment);
        this.notifications.success('Zakat payment updated.');
      } else if (result?.kind === 'deleted-elsewhere') {
        this.paymentsResource.reload();
        this.notifications.info('That payment no longer exists — the list has been refreshed.');
      }
    });
  }

  deletePayment(payment: ZakatPaymentDto): void {
    const ref = this.dialog.open<ConfirmDialog, ConfirmDialogData, boolean>(ConfirmDialog, {
      data: {
        title: 'Delete this payment record?',
        message: `Delete the SGD ${payment.amountSgd.toFixed(2)} payment recorded on ${payment.paidOn}? This only removes the record — it does not affect the calculation above. This cannot be undone.`,
        confirmLabel: 'Delete',
        destructive: true,
      },
    });

    ref.afterClosed().subscribe((confirmed) => {
      if (!confirmed) {
        return;
      }

      // Optimistic — rolled back via reload() below if the DELETE fails.
      this.paymentsResource.update((list) => (list ?? []).filter((p) => p.id !== payment.id));

      this.zakatApi
        .deletePayment(payment.id)
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: () => this.notifications.success('Payment record deleted.'),
          error: () => {
            this.notifications.error("Couldn't delete that payment record — it has been restored.");
            this.paymentsResource.reload();
          },
        });
    });
  }

  private upsertPayment(payment: ZakatPaymentDto): void {
    this.paymentsResource.update((list) => [...(list ?? []).filter((p) => p.id !== payment.id), payment]);
  }
}
