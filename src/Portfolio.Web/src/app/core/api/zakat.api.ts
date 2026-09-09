import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpResourceRef, httpResource } from '@angular/common/http';
import { Observable } from 'rxjs';

import { API_ROUTES } from './api-routes';
import { CreateZakatPaymentRequest, UpdateZakatPaymentRequest, ZakatPaymentDto, ZakatReportDto } from './models';

/**
 * Thin data-access layer for `/api/zakat*` — same shape as `AssetsApi`, see
 * its header comment. Covers both the computed report (zakat.md §4) and the
 * hand-recorded payment ledger (zakat.md §3.2), which are never derived from
 * one another.
 */
@Injectable({ providedIn: 'root' })
export class ZakatApi {
  private readonly http = inject(HttpClient);

  /**
   * GET /api/zakat. `asOf` is optional server-side (defaults to today) and no
   * current caller ever passes one, so this takes no parameter — add one if a
   * caller ever needs a historical `?asOf=`.
   */
  report(): HttpResourceRef<ZakatReportDto | undefined> {
    return httpResource<ZakatReportDto>(() => API_ROUTES.zakatReport());
  }

  /** GET /api/zakat/payments. */
  payments(): HttpResourceRef<ZakatPaymentDto[] | undefined> {
    return httpResource<ZakatPaymentDto[]>(() => API_ROUTES.zakatPayments);
  }

  /** POST /api/zakat/payments. */
  createPayment(request: CreateZakatPaymentRequest): Observable<ZakatPaymentDto> {
    return this.http.post<ZakatPaymentDto>(API_ROUTES.zakatPayments, request);
  }

  /**
   * PUT /api/zakat/payments/{id}. Same reasoning as `TransactionsApi.update`
   * — `UpdateZakatPaymentRequest` is exactly `CreateZakatPaymentRequest`, and
   * `ZakatPaymentFormDialog` always sends the full two-field request on both
   * create and edit, so there is no partial-replace fact worth encoding here
   * the way there is for `AssetsApi.replace`.
   */
  updatePayment(id: number, request: UpdateZakatPaymentRequest): Observable<ZakatPaymentDto> {
    return this.http.put<ZakatPaymentDto>(API_ROUTES.zakatPayment(id), request);
  }

  /** DELETE /api/zakat/payments/{id} — removes only the recorded payment, never affects the computed report. */
  deletePayment(id: number): Observable<void> {
    return this.http.delete<void>(API_ROUTES.zakatPayment(id));
  }
}
