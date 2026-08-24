import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';

import { TransactionFormDialog, TransactionFormDialogData } from './transaction-form.dialog';
import { API_ROUTES } from '../../core/api/api-routes';
import { AssetDto, TransactionDto } from '../../core/api/models';

const ANVL: AssetDto = {
  id: 6,
  symbol: 'ANVL',
  name: 'Anvil',
  assetClass: 'Crypto',
  exchange: null,
  currency: 'USD',
  quoteProviderKind: 'CoinGecko',
  providerSymbol: null,
  providerCoinId: 'anvil',
  isActive: true,
  createdAt: '2026-07-26T00:00:00+00:00',
  hasEverBeenPriced: true,
  providerHasEverSucceeded: true,
};

const AAPL: AssetDto = {
  id: 1,
  symbol: 'AAPL',
  name: 'Apple Inc.',
  assetClass: 'Stock',
  exchange: 'NASDAQ',
  currency: 'USD',
  quoteProviderKind: 'TwelveData',
  providerSymbol: 'AAPL',
  providerCoinId: null,
  isActive: true,
  createdAt: '2026-07-26T00:00:00+00:00',
  hasEverBeenPriced: true,
  providerHasEverSucceeded: true,
};

const EXISTING: TransactionDto = {
  id: 11,
  assetId: 6,
  assetSymbol: 'ANVL',
  assetClass: 'Crypto',
  type: 'Buy',
  tradeDate: '2026-07-30',
  quantity: 1000000,
  pricePerUnit: 0.0005326,
  fees: 1.5,
  currency: 'USD',
  notes: null,
};

describe('TransactionFormDialog', () => {
  let httpMock: HttpTestingController;
  let dialogRef: { close: ReturnType<typeof vi.fn> };

  function setup(data: TransactionFormDialogData): ComponentFixture<TransactionFormDialog> {
    dialogRef = { close: vi.fn() };
    TestBed.configureTestingModule({
      imports: [TransactionFormDialog],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
        { provide: MAT_DIALOG_DATA, useValue: data },
        { provide: MatDialogRef, useValue: dialogRef },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(TransactionFormDialog);
    fixture.detectChanges();
    return fixture;
  }

  afterEach(() => httpMock.verify());

  // D22: the JIT-compile warm-up rationale in confirm-dialog.spec.ts applies
  // here too, more so — this dialog additionally pulls in
  // `MatDatepickerModule`/`MatSelectModule`, and it was the file actually
  // observed timing out (always on this first test, never a later one).
  // `setup()` here doesn't dispatch a request on its own (only `submit()`
  // does), so there's nothing for the `afterEach` `httpMock.verify()` above
  // to trip over before the first real test overwrites `httpMock`.
  beforeAll(() => {
    setup({ mode: 'create', assets: [ANVL, AAPL] });
    // TestBed forbids a second `configureTestingModule()` once instantiated,
    // and nothing resets it between `beforeAll` and the first real `it()` —
    // that reset normally happens in TestBed's own `afterEach`, which hasn't
    // run yet. Reset explicitly so `setup()` inside the first test can
    // configure a fresh module of its own.
    TestBed.resetTestingModule();
  });

  it('blocks submit and marks controls touched when required fields are empty', () => {
    const fixture = setup({ mode: 'create', assets: [ANVL, AAPL] });
    fixture.componentInstance.submit();
    fixture.detectChanges();

    expect(fixture.componentInstance.form.controls.assetId.touched).toBe(true);
    expect(dialogRef.close).not.toHaveBeenCalled();
  });

  it('excludes deactivated assets from the picker — GET /api/assets returns them too, but they must not be selectable here', () => {
    // D24 — the asset-management page's deactivate action is a full-replace
    // PUT with isActive: false, and GET /api/assets keeps returning the
    // deactivated row (confirmed live). Nothing about this dialog's own data
    // shape changes when that happens, so the exclusion has to happen here.
    const deactivatedMsft = { ...AAPL, id: 2, symbol: 'MSFT', name: 'Microsoft Corporation', isActive: false };
    const fixture = setup({ mode: 'create', assets: [ANVL, AAPL, deactivatedMsft] });

    const options = fixture.componentInstance.assetOptions();
    expect(options.map((a) => a.symbol)).toEqual(['AAPL', 'ANVL']);
    expect(options.some((a) => a.symbol === 'MSFT')).toBe(false);
  });

  it('rejects a quantity with more than 10 decimal places at the form layer, per D8', () => {
    const fixture = setup({ mode: 'create', assets: [ANVL, AAPL] });
    const quantity = fixture.componentInstance.form.controls.quantity;
    quantity.setValue(0.00000000012); // 11dp
    quantity.markAsTouched();
    fixture.detectChanges();

    expect(fixture.componentInstance.fieldError('quantity')).toContain('10 decimal places');
  });

  it('rejects a zero or negative quantity', () => {
    const fixture = setup({ mode: 'create', assets: [ANVL, AAPL] });
    const quantity = fixture.componentInstance.form.controls.quantity;
    quantity.setValue(0);
    quantity.markAsTouched();
    fixture.detectChanges();

    expect(fixture.componentInstance.fieldError('quantity')).toContain('greater than zero');
  });

  it('rejects a negative price per unit', () => {
    const fixture = setup({ mode: 'create', assets: [ANVL, AAPL] });
    const pricePerUnit = fixture.componentInstance.form.controls.pricePerUnit;
    pricePerUnit.setValue(-0.01);
    pricePerUnit.markAsTouched();
    fixture.detectChanges();

    expect(fixture.componentInstance.fieldError('pricePerUnit')).toBe('Cannot be negative.');
  });

  it('D36 — permits a zero price (free share / bonus issue / scrip dividend) without an error', () => {
    const fixture = setup({ mode: 'create', assets: [ANVL, AAPL] });
    const pricePerUnit = fixture.componentInstance.form.controls.pricePerUnit;
    pricePerUnit.setValue(0);
    pricePerUnit.markAsTouched();
    fixture.detectChanges();

    expect(pricePerUnit.valid).toBe(true);
    expect(fixture.componentInstance.fieldError('pricePerUnit')).toBeNull();
  });

  it('D36 — still requires pricePerUnit to be present: Validators.required treats 0 as present, not a blank field masquerading as free', () => {
    const fixture = setup({ mode: 'create', assets: [ANVL, AAPL] });
    const pricePerUnit = fixture.componentInstance.form.controls.pricePerUnit;

    // A genuinely blank field (never touched by the user) is still required=true.
    pricePerUnit.setValue(null);
    pricePerUnit.markAsTouched();
    fixture.detectChanges();
    expect(fixture.componentInstance.fieldError('pricePerUnit')).toBe('Required.');

    // Explicitly typing 0 clears the required error — 0 is a present value.
    pricePerUnit.setValue(0);
    fixture.detectChanges();
    expect(pricePerUnit.errors?.['required']).toBeFalsy();
    expect(pricePerUnit.valid).toBe(true);
  });

  it('submits a zero-price transaction (D36 — Z74 id 2002 is the real-world case this retires a DB workaround for)', () => {
    const fixture = setup({ mode: 'create', assets: [ANVL, AAPL] });
    const { form } = fixture.componentInstance;

    form.controls.assetId.setValue(1); // AAPL
    form.controls.tradeDate.setValue(new Date(2026, 6, 30));
    form.controls.quantity.setValue(10);
    form.controls.pricePerUnit.setValue(0);
    fixture.componentInstance.submit();

    const req = httpMock.expectOne(API_ROUTES.transactions);
    expect(req.request.body.pricePerUnit).toBe(0);
    req.flush({ ...EXISTING, id: 100, pricePerUnit: 0 });

    expect(dialogRef.close).toHaveBeenCalledWith({
      kind: 'saved',
      transaction: { ...EXISTING, id: 100, pricePerUnit: 0 },
    });
  });

  it('auto-sets currency from the selected asset and sends it, even though the control is disabled', () => {
    const fixture = setup({ mode: 'create', assets: [ANVL, AAPL] });
    const { form } = fixture.componentInstance;

    form.controls.assetId.setValue(6); // ANVL, USD
    form.controls.tradeDate.setValue(new Date(2026, 6, 30));
    form.controls.quantity.setValue(1_000_000);
    form.controls.pricePerUnit.setValue(0.0005326);
    fixture.componentInstance.submit();

    const req = httpMock.expectOne(API_ROUTES.transactions);
    expect(req.request.body.currency).toBe('USD');
    expect(req.request.body.assetId).toBe(6);
    // Local date parts, not toISOString — see shared/util/local-date.ts.
    expect(req.request.body.tradeDate).toBe('2026-07-30');
    req.flush({ ...EXISTING, id: 99 });

    expect(dialogRef.close).toHaveBeenCalledWith({
      kind: 'saved',
      transaction: { ...EXISTING, id: 99 },
    });
  });

  it('pre-fills an edit form from the existing transaction, parsing tradeDate at local midnight', () => {
    const fixture = setup({ mode: 'edit', transaction: EXISTING, assets: [ANVL, AAPL] });
    const { form } = fixture.componentInstance;

    expect(form.controls.assetId.value).toBe(6);
    expect(form.controls.quantity.value).toBe(1_000_000);
    expect(form.controls.tradeDate.value?.getFullYear()).toBe(2026);
    expect(form.controls.tradeDate.value?.getMonth()).toBe(6);
    expect(form.controls.tradeDate.value?.getDate()).toBe(30);
  });

  it('maps a 400 ValidationProblemDetails ("sell exceeds units held") onto the quantity field', () => {
    const fixture = setup({ mode: 'create', assets: [ANVL, AAPL] });
    const { form } = fixture.componentInstance;

    form.controls.assetId.setValue(6);
    form.controls.type.setValue('Sell');
    form.controls.tradeDate.setValue(new Date(2026, 6, 30));
    form.controls.quantity.setValue(5);
    form.controls.pricePerUnit.setValue(1);
    fixture.componentInstance.submit();

    const req = httpMock.expectOne(API_ROUTES.transactions);
    req.flush(
      { errors: { quantity: ['Sell quantity 5 exceeds the 0 units currently held.'] } },
      { status: 400, statusText: 'Bad Request' },
    );
    fixture.detectChanges();

    expect(fixture.componentInstance.fieldError('quantity')).toContain('exceeds the 0 units');
    expect(dialogRef.close).not.toHaveBeenCalled();
  });

  it('shows a distinct "deleted elsewhere" state on a bare 404 from PUT, rather than a generic error', () => {
    const fixture = setup({ mode: 'edit', transaction: EXISTING, assets: [ANVL, AAPL] });
    fixture.componentInstance.submit();

    const req = httpMock.expectOne(API_ROUTES.transaction(EXISTING.id));
    expect(req.request.method).toBe('PUT');
    req.flush(null, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    expect(fixture.componentInstance.deletedElsewhere()).toBe(true);
    expect(fixture.nativeElement.textContent).toContain('no longer exists');

    fixture.componentInstance.closeAfterDeletedElsewhere();
    expect(dialogRef.close).toHaveBeenCalledWith({ kind: 'deleted-elsewhere' });
  });
});
