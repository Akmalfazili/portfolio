import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MatDialog } from '@angular/material/dialog';
import { of } from 'rxjs';

import { ZakatPage } from './zakat.page';
import { API_ROUTES } from '../../core/api/api-routes';
import { AssetDto, ZakatCryptoLineDto, ZakatPaymentDto, ZakatReportDto, ZakatStockLineDto } from '../../core/api/models';
import { NotificationService } from '../../core/notifications/notification.service';

function stockLine(overrides: Partial<ZakatStockLineDto> = {}): ZakatStockLineDto {
  return {
    assetId: 1,
    symbol: 'AAPL',
    name: 'Apple Inc.',
    currency: 'USD',
    status: 'FiscalYearEndNotConfigured',
    fiscalYearEndDate: null,
    quantityHeld: null,
    closeNative: null,
    closeDateUsed: null,
    closeDateExact: null,
    fxDateUsed: null,
    fxCarriedBack: null,
    fxRateUsed: null,
    valueSgd: null,
    ...overrides,
  };
}

function cryptoLine(overrides: Partial<ZakatCryptoLineDto> = {}): ZakatCryptoLineDto {
  return {
    assetId: 4,
    symbol: 'ETH',
    name: 'Ethereum',
    currency: 'USD',
    status: 'Included',
    quantityHeld: 0.5,
    priceUsd: 2400.46,
    priceSource: 'Live',
    priceAsOf: '2026-09-03T12:18:20+00:00',
    fxDateUsed: '2026-09-03',
    fxCarriedBack: false,
    fxRateUsed: 1.29,
    valueSgd: 1548.2966,
    ...overrides,
  };
}

function report(overrides: Partial<ZakatReportDto> = {}): ZakatReportDto {
  return {
    asOf: '2026-09-03',
    stocks: [],
    crypto: [],
    stockZakatableSgd: 0,
    cryptoZakatableSgd: 0,
    totalZakatableSgd: 0,
    zakatPayableSgd: 0,
    excludedAssetCount: 0,
    ...overrides,
  };
}

const ASSET: AssetDto = {
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
  createdAt: '2026-01-01T00:00:00+00:00',
  hasEverBeenPriced: true,
  providerHasEverSucceeded: true,
  fiscalYearEndMonth: null,
  fiscalYearEndDay: null,
};

const PAYMENT: ZakatPaymentDto = { id: 1, paidOn: '2026-03-01', amountSgd: 250.75 };

describe('ZakatPage', () => {
  let fixture: ComponentFixture<ZakatPage>;
  let httpMock: HttpTestingController;
  let notifySuccess: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [ZakatPage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations()],
    });
    httpMock = TestBed.inject(HttpTestingController);
    const notifications = TestBed.inject(NotificationService);
    notifySuccess = vi.spyOn(notifications, 'success').mockImplementation(() => {});
    vi.spyOn(notifications, 'error').mockImplementation(() => {});
    vi.spyOn(notifications, 'info').mockImplementation(() => {});
    fixture = TestBed.createComponent(ZakatPage);
  });

  afterEach(() => httpMock.verify());

  function flush(r: ZakatReportDto, payments: ZakatPaymentDto[] = [], assets: AssetDto[] = [ASSET]) {
    httpMock.expectOne(API_ROUTES.zakatReport()).flush(r);
    httpMock.expectOne(API_ROUTES.zakatPayments).flush(payments);
    httpMock.expectOne(API_ROUTES.assets).flush(assets);
  }

  it('shows the loading state before the report resolves', () => {
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Calculating zakat');
    flush(report());
  });

  it('shows the error state and can retry', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.zakatReport()).flush('boom', { status: 500, statusText: 'Server Error' });
    httpMock.expectOne(API_ROUTES.zakatPayments).flush([]);
    httpMock.expectOne(API_ROUTES.assets).flush([ASSET]);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain("Couldn't load the zakat report");

    fixture.componentInstance.retryReport();
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.zakatReport()).flush(report());
  });

  // --- zakat.md §6 — the six statuses must never be conflated -------------

  it('shows distinct labels for NotHeldAtFiscalYearEnd and FiscalYearEndNotConfigured', async () => {
    fixture.detectChanges();
    flush(
      report({
        stocks: [
          stockLine({ assetId: 1, symbol: 'AAPL', status: 'NotHeldAtFiscalYearEnd', fiscalYearEndDate: '2025-09-27', quantityHeld: 0, valueSgd: 0 }),
          stockLine({ assetId: 2, symbol: 'MSFT', status: 'FiscalYearEndNotConfigured' }),
        ],
        excludedAssetCount: 1,
      }),
    );
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Not held at year end');
    expect(text).toContain('Year end not set');
  });

  // --- zakat.md §8 — the day-one "nothing configured yet" state -----------

  it('shows the setup notice, not a broken/blank page, when every stock is unconfigured', async () => {
    fixture.detectChanges();
    flush(
      report({
        stocks: [stockLine({ assetId: 1, symbol: 'AAPL' }), stockLine({ assetId: 2, symbol: 'MSFT' })],
        excludedAssetCount: 2,
      }),
    );
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('No stock has a fiscal year end configured yet');
    expect(text).not.toContain('excluded from the totals below — a gap in the data');
  });

  it('shows the generic excluded-count caveat once at least one — but not every — stock is configured', async () => {
    fixture.detectChanges();
    flush(
      report({
        stocks: [
          stockLine({ assetId: 1, symbol: 'AAPL', status: 'Included', fiscalYearEndDate: '2025-09-27', quantityHeld: 10, closeNative: 200, closeDateUsed: '2025-09-26', closeDateExact: false, fxDateUsed: '2025-09-26', fxCarriedBack: false, fxRateUsed: 1.35, valueSgd: 2600 }),
          stockLine({ assetId: 2, symbol: 'MSFT', status: 'FiscalYearEndNotConfigured' }),
        ],
        stockZakatableSgd: 2600,
        totalZakatableSgd: 2600,
        zakatPayableSgd: 65,
        excludedAssetCount: 1,
      }),
    );
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('1 asset is excluded from the totals below');
    expect(text).toContain('carried forward, not the year-end date itself');
  });

  // --- zakat.md §4.3 — the FX rate column must never conflate "no rate
  // needed" (SGD-native), "not attempted" (excluded status) and a real rate.

  it('renders the FX rate used for a USD stock, with up to 8 dp shown', async () => {
    fixture.detectChanges();
    flush(
      report({
        stocks: [
          stockLine({
            assetId: 1,
            symbol: 'AAPL',
            currency: 'USD',
            status: 'Included',
            fiscalYearEndDate: '2025-09-27',
            quantityHeld: 10,
            closeNative: 200,
            closeDateUsed: '2025-09-26',
            closeDateExact: true,
            fxDateUsed: '2025-09-26',
            fxCarriedBack: false,
            fxRateUsed: 1.29473512,
            valueSgd: 2589.47024,
          }),
        ],
      }),
    );
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('1.29473512');
    expect(text).not.toContain('Already SGD');
  });

  it('shows a no-conversion note, not a bare dash, for an SGD-native stock', async () => {
    fixture.detectChanges();
    flush(
      report({
        stocks: [
          stockLine({
            assetId: 3,
            symbol: 'Z74',
            currency: 'SGD',
            status: 'Included',
            fiscalYearEndDate: '2025-12-31',
            quantityHeld: 1000,
            closeNative: 3.5,
            closeDateUsed: '2025-12-31',
            closeDateExact: true,
            fxDateUsed: null,
            fxCarriedBack: null,
            fxRateUsed: null,
            valueSgd: 3500,
          }),
        ],
      }),
    );
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Already SGD');
    expect(text).toContain('no FX conversion applied');
  });

  it('renders the placeholder dash for a non-Included stock line, never a rate or the SGD note', async () => {
    fixture.detectChanges();
    flush(
      report({
        stocks: [stockLine({ assetId: 2, symbol: 'MSFT', currency: 'USD', status: 'FiscalYearEndNotConfigured' })],
        excludedAssetCount: 1,
      }),
    );
    await fixture.whenStable();
    fixture.detectChanges();

    const cells = Array.from(fixture.nativeElement.querySelectorAll('.zakat__panel table')[0].querySelectorAll('tbody td')) as HTMLElement[];
    // FX rate is the 6th column (Symbol, Status, Fiscal year end, Valued as of, Qty held, Close, FX rate, Value, Actions).
    const fxCell = cells[6];
    expect(fxCell.textContent?.trim()).toBe('—');
  });

  // --- zakat.md §2.4 — never a nisab verdict, never nisab hard-coded -------

  it('never hard-codes a nisab figure or renders a shortfall verdict — only the disclaiming copy mentions "owe", never a computed verdict', async () => {
    fixture.detectChanges();
    flush(report({ totalZakatableSgd: 50000, zakatPayableSgd: 1250 }));
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).not.toContain('15,902');
    expect(text).not.toContain('shortfall');
    // The intro deliberately explains that this page never renders a verdict
    // ("...never shows a 'you owe' verdict.") — that disclaiming sentence is
    // the ONLY place the phrase may appear; it must never appear a second
    // time attached to an actual number, which would mean a verdict got
    // rendered after all.
    const oweMentions = text.match(/you owe/gi) ?? [];
    expect(oweMentions.length).toBe(1);
  });

  // --- zakat.md §2.3 — the crypto convention must be labelled as such ------

  it('labels the crypto subtotal as a convention, not a MUIS ruling', async () => {
    fixture.detectChanges();
    flush(report({ crypto: [cryptoLine()], cryptoZakatableSgd: 1548.2966, totalZakatableSgd: 1548.2966 }));
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Your convention — not a MUIS ruling');
    expect(text).toContain('ETH');
  });

  it('shows a stale-close caveat for a Close-sourced crypto quote, distinct from a live one', async () => {
    fixture.detectChanges();
    flush(report({ crypto: [cryptoLine({ priceSource: 'Close', priceAsOf: '2026-07-24T00:00:00+00:00' })] }));
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('stale, not live');
  });

  // --- "Set year end" — wires into AssetFormDialog in edit mode -----------

  it('opens AssetFormDialog in edit mode for the matching asset, and reloads the report on save', async () => {
    fixture.detectChanges();
    flush(report({ stocks: [stockLine({ assetId: 1, symbol: 'AAPL' })], excludedAssetCount: 1 }), [], [ASSET]);
    await fixture.whenStable();
    fixture.detectChanges();

    const dialog = TestBed.inject(MatDialog);
    const updated: AssetDto = { ...ASSET, fiscalYearEndMonth: 9, fiscalYearEndDay: 27 };
    const open = vi
      .spyOn(dialog, 'open')
      .mockReturnValue({ afterClosed: () => of({ kind: 'saved', asset: updated }) } as ReturnType<MatDialog['open']>);

    fixture.componentInstance.openFiscalYearEndDialog(1);
    fixture.detectChanges();

    expect(open.mock.calls[0][1]?.data).toEqual({ mode: 'edit', asset: ASSET });
    expect(notifySuccess).toHaveBeenCalledWith('AAPL updated.');

    // Reloaded because the field the dialog just changed drives the report.
    httpMock.expectOne(API_ROUTES.zakatReport()).flush(report({ stocks: [stockLine({ assetId: 1, symbol: 'AAPL', status: 'Included' })] }));
  });

  // --- Part 2 — payment history is independent of the report's own state --

  it('renders the payment history even when the report failed to load', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.zakatReport()).flush('boom', { status: 500, statusText: 'Server Error' });
    httpMock.expectOne(API_ROUTES.zakatPayments).flush([PAYMENT]);
    httpMock.expectOne(API_ROUTES.assets).flush([ASSET]);
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain("Couldn't load the zakat report");
    expect(text).toContain('2026-03-01');
  });

  it('shows the empty state with a call to action when there is no payment history', async () => {
    fixture.detectChanges();
    flush(report());
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('No payments recorded yet');
  });

  it('lists payments newest first, sorted client-side after an optimistic insert', async () => {
    fixture.detectChanges();
    flush(report(), [PAYMENT]);
    await fixture.whenStable();
    fixture.detectChanges();

    const dialog = TestBed.inject(MatDialog);
    const newer: ZakatPaymentDto = { id: 2, paidOn: '2026-08-01', amountSgd: 100 };
    vi.spyOn(dialog, 'open').mockReturnValue({
      afterClosed: () => of({ kind: 'saved', payment: newer }),
    } as ReturnType<MatDialog['open']>);

    fixture.componentInstance.openCreatePaymentDialog();
    fixture.detectChanges();

    const rows = Array.from(fixture.nativeElement.querySelectorAll('.zakat__recorded tbody tr')) as HTMLElement[];
    expect(rows[0].textContent).toContain('2026-08-01');
    expect(rows[1].textContent).toContain('2026-03-01');
    expect(notifySuccess).toHaveBeenCalledWith('Zakat payment recorded.');
  });

  it('deletes a payment optimistically and confirms via ConfirmDialog', async () => {
    fixture.detectChanges();
    flush(report(), [PAYMENT]);
    await fixture.whenStable();
    fixture.detectChanges();

    const dialog = TestBed.inject(MatDialog);
    vi.spyOn(dialog, 'open').mockReturnValue({ afterClosed: () => of(true) } as ReturnType<MatDialog['open']>);

    fixture.componentInstance.deletePayment(PAYMENT);
    fixture.detectChanges();

    httpMock.expectOne(API_ROUTES.zakatPayment(PAYMENT.id)).flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('No payments recorded yet');
  });

  it('never writes a payment from the computed report — recording one does not call the report endpoint again', async () => {
    fixture.detectChanges();
    flush(report({ totalZakatableSgd: 4148.2966, zakatPayableSgd: 103.7074 }), []);
    await fixture.whenStable();
    fixture.detectChanges();

    const dialog = TestBed.inject(MatDialog);
    vi.spyOn(dialog, 'open').mockReturnValue({
      afterClosed: () => of({ kind: 'saved', payment: PAYMENT }),
    } as ReturnType<MatDialog['open']>);

    fixture.componentInstance.openCreatePaymentDialog();
    fixture.detectChanges();

    // httpMock.verify() in afterEach proves no extra GET /api/zakat fired.
    expect(fixture.nativeElement.textContent).toContain('250.75');
  });
});
