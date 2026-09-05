import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';

import { AssetFormDialog, AssetFormDialogData } from './asset-form.dialog';
import { API_ROUTES } from '../../core/api/api-routes';
import { AssetDto } from '../../core/api/models';

const CREATED: AssetDto = {
  id: 7,
  symbol: 'GOOGL',
  name: 'Alphabet Inc.',
  assetClass: 'Stock',
  exchange: null,
  currency: 'USD',
  quoteProviderKind: 'TwelveData',
  providerSymbol: 'GOOGL',
  providerCoinId: null,
  isActive: true,
  createdAt: '2026-08-08T12:00:00+00:00',
  hasEverBeenPriced: false,
  providerHasEverSucceeded: false,
  fiscalYearEndMonth: null,
  fiscalYearEndDay: null,
};

describe('AssetFormDialog', () => {
  let httpMock: HttpTestingController;
  let dialogRef: { close: ReturnType<typeof vi.fn> };

  function setup(data: AssetFormDialogData = { mode: 'create' }): ComponentFixture<AssetFormDialog> {
    dialogRef = { close: vi.fn() };
    TestBed.configureTestingModule({
      imports: [AssetFormDialog],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
        { provide: MAT_DIALOG_DATA, useValue: data },
        { provide: MatDialogRef, useValue: dialogRef },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(AssetFormDialog);
    fixture.detectChanges();
    return fixture;
  }

  afterEach(() => httpMock.verify());

  it('blocks submit and marks controls touched when required fields are empty', () => {
    const fixture = setup();
    fixture.componentInstance.submit();
    fixture.detectChanges();

    expect(fixture.componentInstance.form.controls.symbol.touched).toBe(true);
    expect(dialogRef.close).not.toHaveBeenCalled();
  });

  it('defaults to Stock + TwelveData with the providerSymbol field shown', () => {
    const fixture = setup();
    const { componentInstance } = fixture;

    expect(componentInstance.form.controls.assetClass.value).toBe('Stock');
    expect(componentInstance.usesCoinId()).toBe(false);
    expect(componentInstance.showsCreditWarning()).toBe(true); // TwelveData is metered
  });

  it('locks the provider to CoinGecko and switches to the coin-ID field when Crypto is chosen', () => {
    const fixture = setup();
    const { componentInstance } = fixture;

    componentInstance.form.controls.assetClass.setValue('Crypto');
    fixture.detectChanges();

    expect(componentInstance.form.controls.quoteProviderKind.value).toBe('CoinGecko');
    expect(componentInstance.form.controls.quoteProviderKind.disabled).toBe(true);
    expect(componentInstance.usesCoinId()).toBe(true);
    expect(componentInstance.showsCreditWarning()).toBe(false);
  });

  it('clears providerSymbol when switching to Crypto (CoinGecko), so a stale value is never submitted', () => {
    const fixture = setup();
    const { componentInstance } = fixture;

    componentInstance.form.controls.providerSymbol.setValue('AAPL');
    componentInstance.form.controls.assetClass.setValue('Crypto');
    fixture.detectChanges();

    expect(componentInstance.form.controls.quoteProviderKind.value).toBe('CoinGecko');
    expect(componentInstance.form.controls.providerSymbol.value).toBeNull();
  });

  it('clears providerCoinId when switching back to Stock (off CoinGecko), so a stale value is never submitted', () => {
    const fixture = setup();
    const { componentInstance } = fixture;

    componentInstance.form.controls.assetClass.setValue('Crypto');
    componentInstance.form.controls.providerCoinId.setValue('ethereum');
    fixture.detectChanges();

    componentInstance.form.controls.assetClass.setValue('Stock');
    fixture.detectChanges();

    expect(componentInstance.form.controls.quoteProviderKind.value).toBe('TwelveData');
    expect(componentInstance.form.controls.providerCoinId.value).toBeNull();
  });

  it('submits a well-formed CreateAssetRequest, uppercasing symbol and currency', () => {
    const fixture = setup();
    const { componentInstance } = fixture;
    const { form } = componentInstance;

    form.controls.symbol.setValue('googl');
    form.controls.name.setValue('Alphabet Inc.');
    form.controls.currency.setValue('usd');
    form.controls.providerSymbol.setValue('GOOGL');
    componentInstance.submit();

    const req = httpMock.expectOne(API_ROUTES.assets);
    expect(req.request.body).toEqual({
      symbol: 'GOOGL',
      name: 'Alphabet Inc.',
      assetClass: 'Stock',
      exchange: null,
      currency: 'USD',
      quoteProviderKind: 'TwelveData',
      providerSymbol: 'GOOGL',
      providerCoinId: null,
      fiscalYearEndMonth: null,
      fiscalYearEndDay: null,
    });
    req.flush(CREATED);

    expect(dialogRef.close).toHaveBeenCalledWith({ kind: 'saved', asset: CREATED });
  });

  it('maps the D23 providerCoinId 400 onto its own field, not a generic error', () => {
    const fixture = setup();
    const { componentInstance } = fixture;
    const { form } = componentInstance;

    form.controls.assetClass.setValue('Crypto');
    // Mounts the CoinGecko coin-ID field BEFORE submit — matching real usage,
    // where the user picks Crypto (re-rendering the identifier field) and
    // only then fills it in and saves. Skipping this and calling
    // detectChanges() for the first time only after the server error is set
    // would let Angular's `setUpControl` (which runs when the previously-@if
    // -hidden control is linked for the first time) wipe the manually-set
    // error the instant it mounts — an artefact of test ordering, not a real
    // app defect, but worth getting right rather than papering over.
    fixture.detectChanges();
    form.controls.symbol.setValue('ETH2');
    form.controls.name.setValue('Ethereum 2');
    componentInstance.submit();

    const req = httpMock.expectOne(API_ROUTES.assets);
    req.flush(
      {
        errors: {
          providerCoinId: [
            'ProviderCoinId is required when QuoteProviderKind is CoinGecko (the coin id, e.g. "ethereum" — not the ticker).',
          ],
        },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    fixture.detectChanges();

    expect(componentInstance.fieldError('providerCoinId')).toContain('coin id');
    expect(componentInstance.serverError()).toBeNull();
    expect(dialogRef.close).not.toHaveBeenCalled();
  });

  it('maps the D23 providerSymbol 400 onto its own field', () => {
    const fixture = setup();
    const { componentInstance } = fixture;
    const { form } = componentInstance;

    form.controls.symbol.setValue('AAPL2');
    form.controls.name.setValue('Apple Inc. 2');
    componentInstance.submit();

    const req = httpMock.expectOne(API_ROUTES.assets);
    req.flush(
      { errors: { providerSymbol: ['ProviderSymbol is required when QuoteProviderKind is TwelveData.'] } },
      { status: 400, statusText: 'Bad Request' },
    );
    fixture.detectChanges();

    expect(componentInstance.fieldError('providerSymbol')).toContain('ProviderSymbol is required');
  });

  // --- zakat.md §3.1/§9 — fiscal year end fields --------------------------

  it('clears and disables the fiscal year end fields when Crypto is chosen', () => {
    const fixture = setup();
    const { componentInstance } = fixture;

    componentInstance.form.controls.fiscalYearEndMonth.setValue(6);
    componentInstance.form.controls.fiscalYearEndDay.setValue(30);
    componentInstance.form.controls.assetClass.setValue('Crypto');
    fixture.detectChanges();

    expect(componentInstance.form.controls.fiscalYearEndMonth.value).toBeNull();
    expect(componentInstance.form.controls.fiscalYearEndDay.value).toBeNull();
    expect(componentInstance.form.controls.fiscalYearEndMonth.disabled).toBe(true);
    expect(componentInstance.form.controls.fiscalYearEndDay.disabled).toBe(true);
  });

  it('re-enables the fiscal year end fields when switching back to Stock', () => {
    const fixture = setup();
    const { componentInstance } = fixture;

    componentInstance.form.controls.assetClass.setValue('Crypto');
    fixture.detectChanges();
    componentInstance.form.controls.assetClass.setValue('Stock');
    fixture.detectChanges();

    expect(componentInstance.form.controls.fiscalYearEndMonth.disabled).toBe(false);
    expect(componentInstance.form.controls.fiscalYearEndDay.disabled).toBe(false);
  });

  it('flags a lone month with no day as invalid — both or neither', () => {
    const fixture = setup();
    const { componentInstance } = fixture;

    componentInstance.form.controls.fiscalYearEndMonth.setValue(6);
    componentInstance.form.controls.fiscalYearEndMonth.markAsTouched();
    fixture.detectChanges();

    expect(componentInstance.form.controls.fiscalYearEndMonth.valid).toBe(true);
    expect(componentInstance.form.controls.fiscalYearEndDay.valid).toBe(false);
  });

  it('accepts both month and day set together, or both left blank', () => {
    const fixture = setup();
    const { componentInstance } = fixture;

    expect(componentInstance.form.controls.fiscalYearEndMonth.valid).toBe(true);
    expect(componentInstance.form.controls.fiscalYearEndDay.valid).toBe(true);

    componentInstance.form.controls.fiscalYearEndMonth.setValue(6);
    componentInstance.form.controls.fiscalYearEndDay.setValue(30);

    expect(componentInstance.form.controls.fiscalYearEndMonth.valid).toBe(true);
    expect(componentInstance.form.controls.fiscalYearEndDay.valid).toBe(true);
  });

  it('submits fiscalYearEndMonth/Day alongside the rest of a well-formed request', () => {
    const fixture = setup();
    const { componentInstance } = fixture;
    const { form } = componentInstance;

    form.controls.symbol.setValue('googl');
    form.controls.name.setValue('Alphabet Inc.');
    form.controls.providerSymbol.setValue('GOOGL');
    form.controls.fiscalYearEndMonth.setValue(12);
    form.controls.fiscalYearEndDay.setValue(31);
    componentInstance.submit();

    const req = httpMock.expectOne(API_ROUTES.assets);
    expect(req.request.body).toEqual(
      expect.objectContaining({ fiscalYearEndMonth: 12, fiscalYearEndDay: 31 }),
    );
    req.flush(CREATED);
  });

  // --- Edit mode (new — previously assets could only be created) ----------

  describe('edit mode', () => {
    const EXISTING: AssetDto = {
      id: 3,
      symbol: 'MSFT',
      name: 'Microsoft Corporation',
      assetClass: 'Stock',
      exchange: 'NASDAQ',
      currency: 'USD',
      quoteProviderKind: 'TwelveData',
      providerSymbol: 'MSFT',
      providerCoinId: null,
      isActive: true,
      createdAt: '2026-01-01T00:00:00+00:00',
      hasEverBeenPriced: true,
      providerHasEverSucceeded: true,
      fiscalYearEndMonth: null,
      fiscalYearEndDay: null,
    };

    it('pre-fills from the existing asset and disables identity/provider fields', () => {
      const fixture = setup({ mode: 'edit', asset: EXISTING });
      const { componentInstance } = fixture;

      expect(componentInstance.form.controls.symbol.value).toBe('MSFT');
      expect(componentInstance.form.controls.symbol.disabled).toBe(true);
      expect(componentInstance.form.controls.assetClass.disabled).toBe(true);
      expect(componentInstance.form.controls.currency.disabled).toBe(true);
      expect(componentInstance.form.controls.quoteProviderKind.disabled).toBe(true);
      expect(componentInstance.form.controls.name.disabled).toBe(false);
      expect(componentInstance.form.controls.fiscalYearEndMonth.disabled).toBe(false);
    });

    it('locks the fiscal year end fields for an existing Crypto asset', () => {
      const cryptoAsset: AssetDto = { ...EXISTING, assetClass: 'Crypto', quoteProviderKind: 'CoinGecko', currency: 'USD', providerSymbol: null, providerCoinId: 'ethereum' };
      const fixture = setup({ mode: 'edit', asset: cryptoAsset });
      const { componentInstance } = fixture;

      expect(componentInstance.form.controls.fiscalYearEndMonth.disabled).toBe(true);
      expect(componentInstance.form.controls.fiscalYearEndDay.disabled).toBe(true);
    });

    it('PUTs a full replace with the new fiscal year end, carrying isActive through unchanged', () => {
      const fixture = setup({ mode: 'edit', asset: EXISTING });
      const { componentInstance } = fixture;

      componentInstance.form.controls.fiscalYearEndMonth.setValue(6);
      componentInstance.form.controls.fiscalYearEndDay.setValue(30);
      componentInstance.submit();

      const req = httpMock.expectOne(API_ROUTES.asset(EXISTING.id));
      expect(req.request.method).toBe('PUT');
      expect(req.request.body).toEqual({
        symbol: 'MSFT',
        name: 'Microsoft Corporation',
        assetClass: 'Stock',
        exchange: 'NASDAQ',
        currency: 'USD',
        quoteProviderKind: 'TwelveData',
        providerSymbol: 'MSFT',
        providerCoinId: null,
        fiscalYearEndMonth: 6,
        fiscalYearEndDay: 30,
        isActive: true,
      });
      req.flush({ ...EXISTING, fiscalYearEndMonth: 6, fiscalYearEndDay: 30 });

      expect(dialogRef.close).toHaveBeenCalledWith({
        kind: 'saved',
        asset: { ...EXISTING, fiscalYearEndMonth: 6, fiscalYearEndDay: 30 },
      });
    });
  });
});
