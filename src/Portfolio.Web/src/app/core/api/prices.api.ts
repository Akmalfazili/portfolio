import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

import { API_ROUTES } from './api-routes';
import { PriceRefreshCycleResult, PriceRefreshStatus } from './models';

/**
 * Thin data-access layer for `/api/prices*` — the two plain HTTP calls
 * `PriceStore` makes itself. Both are one-shot `Observable` methods, not
 * resource factories: prices themselves arrive over `/hubs/prices`, never
 * `httpResource`, and `PriceStore` drives these two calls by hand (a manual
 * refresh command, a status poll) rather than binding them to a template.
 * `PRICES_HUB_URL` stays in `PriceStore` — it is a hub URL, not an HTTP
 * route, so it does not belong behind this service; `API_ROUTES` is imported
 * only from inside `core/api/`, and this is that seam for `PriceStore`.
 */
@Injectable({ providedIn: 'root' })
export class PricesApi {
  private readonly http = inject(HttpClient);

  /**
   * POST /api/prices/refresh — rate-limited server-side with a 30-second
   * cooldown; a 429 carries `secondsRemaining` (`RefreshCooldownProblemDetails`),
   * which the caller handles by counting down, not with an error toast.
   */
  refresh(): Observable<PriceRefreshCycleResult> {
    return this.http.post<PriceRefreshCycleResult>(API_ROUTES.pricesRefresh, {});
  }

  /** GET /api/prices/status — deliberately never costs a provider credit; the safe thing to poll. */
  status(): Observable<PriceRefreshStatus> {
    return this.http.get<PriceRefreshStatus>(API_ROUTES.pricesStatus);
  }
}
