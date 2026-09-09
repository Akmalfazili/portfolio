import { Injectable } from '@angular/core';
import { HttpResourceRef, httpResource } from '@angular/common/http';

import { API_ROUTES } from './api-routes';
import { AnnualReturnsDto, AssetClass, PortfolioAllocationDto, PortfolioSummaryDto } from './models';

/**
 * Thin data-access layer for `/api/portfolio/*` — read-only. There is no
 * portfolio-level mutation endpoint, so unlike `AssetsApi`/`TransactionsApi`/
 * `ZakatApi` this service injects nothing (no `HttpClient` field to carry) —
 * every method here is a resource-factory that `httpResource` builds for
 * itself from an ambient injection context.
 */
@Injectable({ providedIn: 'root' })
export class PortfolioApi {
  /**
   * GET /api/portfolio/{assetClass}/summary. `assetClass` is an accessor so
   * the URL stays reactive across the `/stocks` <-> `/crypto` route
   * parameter — an `input()` signal satisfies this directly, since a signal
   * is itself callable as `() => AssetClass`.
   */
  summary(assetClass: () => AssetClass): HttpResourceRef<PortfolioSummaryDto | undefined> {
    return httpResource<PortfolioSummaryDto>(() => API_ROUTES.portfolioSummary(assetClass()));
  }

  /** GET /api/portfolio/{assetClass}/allocation. */
  allocation(assetClass: () => AssetClass): HttpResourceRef<PortfolioAllocationDto | undefined> {
    return httpResource<PortfolioAllocationDto>(() => API_ROUTES.portfolioAllocation(assetClass()));
  }

  /**
   * GET /api/portfolio/stock/annual-returns — stocks only. Crypto keeps no
   * price history at all (tracker.md's crypto-scope decision), so this
   * resolves to an `undefined` httpResource url for a Crypto `assetClass()`
   * — never requested, not rendered empty. See `PortfolioOverviewPage`'s own
   * doc comment.
   */
  annualReturns(assetClass: () => AssetClass): HttpResourceRef<AnnualReturnsDto | undefined> {
    return httpResource<AnnualReturnsDto | undefined>(() =>
      assetClass() === 'Stock' ? API_ROUTES.stockAnnualReturns : undefined,
    );
  }
}
