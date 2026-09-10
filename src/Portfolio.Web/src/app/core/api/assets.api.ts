import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpResourceRef, httpResource } from '@angular/common/http';
import { Observable } from 'rxjs';

import { API_ROUTES } from './api-routes';
import {
  AssetDividendHistoryDto,
  AssetDto,
  AssetPerformanceDto,
  CreateAssetRequest,
  UpdateAssetRequest,
} from './models';

/**
 * Thin data-access layer for `/api/assets*` (F1, frontend-solid.md). Owns
 * only the URL, the verb and the request shape — `httpResource` itself stays
 * in the calling component (it needs an injection context and a reactive
 * URL), so every read below is a factory METHOD that builds and returns a
 * fresh resource, not a resource this service holds and shares.
 */
@Injectable({ providedIn: 'root' })
export class AssetsApi {
  private readonly http = inject(HttpClient);

  /**
   * GET /api/assets — includes inactive assets too (confirmed live; see
   * `AssetManagementPage`). `HttpResourceRef<AssetDto[] | undefined>`, not
   * `HttpResourceRef<AssetDto[]>` — `httpResource` without a `defaultValue`
   * always types its value as possibly `undefined` while loading, even for a
   * URL that is never itself conditional; every caller already reads through
   * `?? []` or `resourceState`, which is what actually handles that.
   */
  list(): HttpResourceRef<AssetDto[] | undefined> {
    return httpResource<AssetDto[]>(() => API_ROUTES.assets);
  }

  /**
   * GET /api/assets/{id}/performance — stocks only. `assetId` is an ACCESSOR,
   * not a value, so the resource's URL stays reactive as the asset resolves;
   * an accessor that returns `undefined` (as `AssetDetailPage` does before
   * the asset has resolved, or for a crypto asset) makes this an `undefined`
   * httpResource url — never requested, not called and discarded. Load-bearing
   * for the asset-class segregation rule: crypto must not request this.
   */
  performance(assetId: () => number | undefined): HttpResourceRef<AssetPerformanceDto | undefined> {
    return httpResource<AssetPerformanceDto | undefined>(() => {
      const id = assetId();
      return id !== undefined ? API_ROUTES.assetPerformance(id) : undefined;
    });
  }

  /** GET /api/assets/{id}/dividends — stocks only; same conditional-URL shape as `performance`. */
  dividends(
    assetId: () => number | undefined,
  ): HttpResourceRef<AssetDividendHistoryDto | undefined> {
    return httpResource<AssetDividendHistoryDto | undefined>(() => {
      const id = assetId();
      return id !== undefined ? API_ROUTES.assetDividends(id) : undefined;
    });
  }

  /** POST /api/assets. */
  create(request: CreateAssetRequest): Observable<AssetDto> {
    return this.http.post<AssetDto>(API_ROUTES.assets, request);
  }

  /**
   * PUT /api/assets/{id} is a FULL REPLACE — this is the ONE place that fact
   * is encoded (F1's own text names this as duplicated and drifting).
   * `changes` is spread over every field of `asset` resent as an
   * `UpdateAssetRequest`, so a caller that only means to flip one field
   * (`AssetManagementPage.toggleActive`'s `isActive`) never has to hand-build
   * the other ten, and a caller replacing the whole record
   * (`AssetFormDialog`'s edit submit) can still carry through the one field
   * its own form never shows (`isActive`) without duplicating this shape.
   */
  replace(asset: AssetDto, changes: Partial<UpdateAssetRequest>): Observable<AssetDto> {
    return this.http.put<AssetDto>(API_ROUTES.asset(asset.id), {
      ...this.toUpdateRequest(asset),
      ...changes,
    });
  }

  /** DELETE /api/assets/{id} — cascades server-side to transactions, price history and quote. */
  delete(id: number): Observable<void> {
    return this.http.delete<void>(API_ROUTES.asset(id));
  }

  private toUpdateRequest(asset: AssetDto): UpdateAssetRequest {
    return {
      symbol: asset.symbol,
      name: asset.name,
      assetClass: asset.assetClass,
      exchange: asset.exchange,
      currency: asset.currency,
      quoteProviderKind: asset.quoteProviderKind,
      providerSymbol: asset.providerSymbol,
      providerCoinId: asset.providerCoinId,
      fiscalYearEndMonth: asset.fiscalYearEndMonth,
      fiscalYearEndDay: asset.fiscalYearEndDay,
      isActive: asset.isActive,
    };
  }
}
