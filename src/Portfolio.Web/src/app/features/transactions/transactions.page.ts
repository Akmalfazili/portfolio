import { ChangeDetectionStrategy, Component, computed } from '@angular/core';
import { httpResource } from '@angular/common/http';

import { TransactionDto } from '../../core/api/models';
import { API_ROUTES } from '../../core/api/api-routes';
import { MoneyPipe } from '../../shared/pipes/money.pipe';
import { QuantityPipe } from '../../shared/pipes/quantity.pipe';
import { StateMessage } from '../../shared/state-message/state-message';

/**
 * Covers both asset classes at once (no `assetClass` route data — see
 * app.routes.ts). Phase 8 adds the asset-class filter, the typed reactive
 * `TransactionFormDialog`, and create/edit/delete. This phase proves the real
 * list against GET /api/transactions plus the loading/empty/error scaffold.
 */
@Component({
  selector: 'app-transactions-page',
  standalone: true,
  imports: [MoneyPipe, QuantityPipe, StateMessage],
  templateUrl: './transactions.page.html',
  styleUrl: './transactions.page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TransactionsPage {
  private readonly transactionsResource = httpResource<TransactionDto[]>(() => API_ROUTES.transactions);

  readonly isLoading = this.transactionsResource.isLoading;
  readonly hasError = computed(() => this.transactionsResource.error() != null);
  readonly transactions = computed(() => this.transactionsResource.value() ?? []);
  readonly isEmpty = computed(() => !this.isLoading() && !this.hasError() && this.transactions().length === 0);

  retry(): void {
    this.transactionsResource.reload();
  }
}
