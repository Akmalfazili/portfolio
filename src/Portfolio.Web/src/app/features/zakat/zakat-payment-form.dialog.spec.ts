import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';

import { ZakatPaymentFormDialog, ZakatPaymentFormDialogData } from './zakat-payment-form.dialog';
import { API_ROUTES } from '../../core/api/api-routes';
import { ZakatPaymentDto } from '../../core/api/models';

const EXISTING: ZakatPaymentDto = {
  id: 1,
  paidOn: '2026-03-01',
  amountSgd: 250.75,
};

describe('ZakatPaymentFormDialog', () => {
  let httpMock: HttpTestingController;
  let dialogRef: { close: ReturnType<typeof vi.fn> };

  function setup(
    data: ZakatPaymentFormDialogData = { mode: 'create' },
  ): ComponentFixture<ZakatPaymentFormDialog> {
    dialogRef = { close: vi.fn() };
    TestBed.configureTestingModule({
      imports: [ZakatPaymentFormDialog],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
        { provide: MAT_DIALOG_DATA, useValue: data },
        { provide: MatDialogRef, useValue: dialogRef },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(ZakatPaymentFormDialog);
    fixture.detectChanges();
    return fixture;
  }

  afterEach(() => httpMock.verify());

  // D22 — the same MatDatepickerModule JIT-compile warm-up rationale as
  // transaction-form.dialog.spec.ts: this dialog pulls the datepicker in too,
  // and the first test to mount it pays that one-time cost.
  beforeAll(() => {
    setup();
    TestBed.resetTestingModule();
  });

  it('blocks submit and marks controls touched when required fields are empty', () => {
    const fixture = setup();
    fixture.componentInstance.form.controls.amountSgd.setValue(null);
    fixture.componentInstance.submit();
    fixture.detectChanges();

    expect(fixture.componentInstance.form.controls.amountSgd.touched).toBe(true);
    expect(dialogRef.close).not.toHaveBeenCalled();
  });

  it('rejects a zero or negative amount', () => {
    const fixture = setup();
    fixture.componentInstance.form.controls.amountSgd.setValue(0);
    fixture.componentInstance.form.controls.amountSgd.markAsTouched();
    fixture.detectChanges();

    expect(fixture.componentInstance.fieldError('amountSgd')).toContain('greater than zero');
  });

  it('defaults paidOn to today for a new payment', () => {
    const fixture = setup();
    expect(fixture.componentInstance.form.controls.paidOn.value).toEqual(
      fixture.componentInstance.today,
    );
  });

  it('submits a well-formed CreateZakatPaymentRequest with paidOn as a plain YYYY-MM-DD string', () => {
    const fixture = setup();
    const { form } = fixture.componentInstance;

    form.controls.paidOn.setValue(new Date(2026, 2, 1)); // month is 0-based: March
    form.controls.amountSgd.setValue(250.75);
    fixture.componentInstance.submit();

    const req = httpMock.expectOne(API_ROUTES.zakatPayments);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ paidOn: '2026-03-01', amountSgd: 250.75 });
    req.flush(EXISTING);

    expect(dialogRef.close).toHaveBeenCalledWith({ kind: 'saved', payment: EXISTING });
  });

  it('pre-fills from the existing payment and PUTs on submit in edit mode', () => {
    const fixture = setup({ mode: 'edit', payment: EXISTING });
    const { componentInstance } = fixture;

    expect(componentInstance.form.controls.amountSgd.value).toBe(250.75);

    componentInstance.form.controls.amountSgd.setValue(300);
    componentInstance.submit();

    const req = httpMock.expectOne(API_ROUTES.zakatPayment(EXISTING.id));
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ paidOn: '2026-03-01', amountSgd: 300 });
    req.flush({ ...EXISTING, amountSgd: 300 });

    expect(dialogRef.close).toHaveBeenCalledWith({
      kind: 'saved',
      payment: { ...EXISTING, amountSgd: 300 },
    });
  });

  it('shows a distinct "no longer exists" state on a 404 while editing, rather than a generic error', () => {
    const fixture = setup({ mode: 'edit', payment: EXISTING });
    const { componentInstance } = fixture;

    componentInstance.submit();
    const req = httpMock.expectOne(API_ROUTES.zakatPayment(EXISTING.id));
    req.flush(null, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    expect(componentInstance.deletedElsewhere()).toBe(true);
    expect(componentInstance.serverError()).toBeNull();
  });

  it('closes with a deleted-elsewhere result rather than a saved one', () => {
    const fixture = setup({ mode: 'edit', payment: EXISTING });
    const { componentInstance } = fixture;

    componentInstance.submit();
    httpMock
      .expectOne(API_ROUTES.zakatPayment(EXISTING.id))
      .flush(null, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    componentInstance.closeAfterDeletedElsewhere();
    expect(dialogRef.close).toHaveBeenCalledWith({ kind: 'deleted-elsewhere' });
  });

  it('maps a 400 amountSgd validation error onto its own field', () => {
    const fixture = setup();
    const {
      componentInstance,
      componentInstance: { form },
    } = fixture;

    form.controls.amountSgd.setValue(5);
    componentInstance.submit();

    const req = httpMock.expectOne(API_ROUTES.zakatPayments);
    req.flush(
      { errors: { amountSgd: ['AmountSgd must be greater than zero.'] } },
      { status: 400, statusText: 'Bad Request' },
    );
    fixture.detectChanges();

    expect(componentInstance.fieldError('amountSgd')).toContain('must be greater than zero');
    expect(dialogRef.close).not.toHaveBeenCalled();
  });
});
