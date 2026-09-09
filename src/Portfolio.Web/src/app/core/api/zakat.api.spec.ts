import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { ZakatApi } from './zakat.api';
import { API_ROUTES } from './api-routes';
import { ZakatPaymentDto } from './models';

const PAYMENT: ZakatPaymentDto = { id: 1, paidOn: '2026-01-01', amountSgd: 100 };

describe('ZakatApi', () => {
  let api: ZakatApi;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(ZakatApi);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('report() issues a GET to /api/zakat with no ?asOf=', () => {
    TestBed.runInInjectionContext(() => api.report());
    TestBed.tick();
    const req = httpMock.expectOne(API_ROUTES.zakatReport());
    expect(req.request.method).toBe('GET');
  });

  it('payments() issues a GET to /api/zakat/payments', () => {
    TestBed.runInInjectionContext(() => api.payments());
    TestBed.tick();
    httpMock.expectOne(API_ROUTES.zakatPayments);
  });

  it('createPayment() POSTs the request as-is', () => {
    api.createPayment({ paidOn: '2026-01-01', amountSgd: 100 }).subscribe();
    const req = httpMock.expectOne(API_ROUTES.zakatPayments);
    expect(req.request.method).toBe('POST');
    req.flush(PAYMENT);
  });

  it('updatePayment() PUTs the full request unchanged — same no-partial-replace shape as TransactionsApi.update', () => {
    const request = { paidOn: '2026-02-01', amountSgd: 250 };
    api.updatePayment(1, request).subscribe();

    const req = httpMock.expectOne(API_ROUTES.zakatPayment(1));
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual(request);
    req.flush({ ...PAYMENT, ...request });
  });

  it('deletePayment() DELETEs /api/zakat/payments/{id}', () => {
    api.deletePayment(1).subscribe();
    const req = httpMock.expectOne(API_ROUTES.zakatPayment(1));
    expect(req.request.method).toBe('DELETE');
    req.flush(null, { status: 204, statusText: 'No Content' });
  });
});
