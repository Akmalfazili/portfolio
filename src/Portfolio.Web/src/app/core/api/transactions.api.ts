import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpResourceRef, httpResource } from '@angular/common/http';
import { Observable } from 'rxjs';

import { API_ROUTES } from './api-routes';
import { CreateTransactionRequest, TransactionDto, UpdateTransactionRequest } from './models';

/**
 * Thin data-access layer for `/api/transactions*` — same shape as `AssetsApi`,
 * see its header comment.
 */
@Injectable({ providedIn: 'root' })
export class TransactionsApi {
  private readonly http = inject(HttpClient);

  /**
   * GET /api/transactions — every transaction across both asset classes;
   * `TransactionsPage` filters client-side. Typed `| undefined` for the same
   * `httpResource`-without-`defaultValue` reason as `AssetsApi.list`.
   */
  list(): HttpResourceRef<TransactionDto[] | undefined> {
    return httpResource<TransactionDto[]>(() => API_ROUTES.transactions);
  }

  /**
   * GET /api/transactions?assetId={id} as a reactive resource. `assetId` is
   * an accessor so the URL stays reactive — same conditional-URL shape as
   * `AssetsApi.performance`. `AssetDetailPage`'s `transactionsResource`.
   */
  byAsset(assetId: () => number | undefined): HttpResourceRef<TransactionDto[] | undefined> {
    return httpResource<TransactionDto[] | undefined>(() => {
      const id = assetId();
      return id !== undefined ? API_ROUTES.transactionsByAsset(id) : undefined;
    });
  }

  /**
   * GET /api/transactions?assetId={id} as a one-shot `Observable` rather than
   * a resource — the delete-confirm transaction-count preflight on
   * `AssetManagementPage.deleteAsset`, fired once per click, not tracking a
   * reactive URL.
   */
  byAssetOnce(assetId: number): Observable<TransactionDto[]> {
    return this.http.get<TransactionDto[]>(API_ROUTES.transactionsByAsset(assetId));
  }

  /** POST /api/transactions. */
  create(request: CreateTransactionRequest): Observable<TransactionDto> {
    return this.http.post<TransactionDto>(API_ROUTES.transactions, request);
  }

  /**
   * PUT /api/transactions/{id}. Unlike `AssetsApi.replace`, this takes the
   * FULL request rather than a `Partial` — `UpdateTransactionRequest` is
   * exactly `CreateTransactionRequest` (`core/api/models.ts`), and
   * `TransactionFormDialog`'s create and edit submit both always build and
   * send every field, never a partial patch. There is no "PUT is a full
   * replace, resend every field" fact to encode here the way there is for
   * assets — inventing a `replace(transaction, changes)` symmetry would be
   * ceremony with nothing behind it.
   */
  update(id: number, request: UpdateTransactionRequest): Observable<TransactionDto> {
    return this.http.put<TransactionDto>(API_ROUTES.transaction(id), request);
  }

  /** DELETE /api/transactions/{id}. */
  delete(id: number): Observable<void> {
    return this.http.delete<void>(API_ROUTES.transaction(id));
  }
}
