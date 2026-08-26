import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MatDialog } from '@angular/material/dialog';
import { of } from 'rxjs';

import { AssetManagementPage } from './asset-management.page';
import { API_ROUTES } from '../../core/api/api-routes';
import { AssetDto } from '../../core/api/models';

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

// D24 real gap — GET /api/assets returns inactive assets too (confirmed
// live); this page is where they should still be visible, with a clear
// inactive treatment, since it's also where you'd go to reactivate one.
const MSFT_INACTIVE: AssetDto = {
  id: 2,
  symbol: 'MSFT',
  name: 'Microsoft Corporation',
  assetClass: 'Stock',
  exchange: 'NASDAQ',
  currency: 'USD',
  quoteProviderKind: 'TwelveData',
  providerSymbol: 'MSFT',
  providerCoinId: null,
  isActive: false,
  createdAt: '2026-07-26T00:00:00+00:00',
  hasEverBeenPriced: true,
  providerHasEverSucceeded: true,
};

describe('AssetManagementPage', () => {
  let fixture: ComponentFixture<AssetManagementPage>;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [AssetManagementPage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations()],
    });
    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(AssetManagementPage);
  });

  afterEach(() => httpMock.verify());

  it('shows the loading state before the request resolves', () => {
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Loading assets');
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
  });

  it('shows the empty state with a call to action when there are no assets', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([]);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('No assets tracked yet');
  });

  it('shows the error state and can retry', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush('boom', { status: 500, statusText: 'Server Error' });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain("Couldn't load assets");

    fixture.componentInstance.retry();
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
  });

  it('lists both active and inactive assets, with a distinct badge for each', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL, MSFT_INACTIVE]);
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('AAPL');
    expect(text).toContain('MSFT');
    expect(text).toContain('Active');
    expect(text).toContain('Inactive');
  });

  // --- D27: an asset that no source has ever priced. A well-formed but WRONG
  // provider identifier ("APPL" for "AAPL") is accepted by the API — D23 only
  // rejects a malformed record — and then reads as "Awaiting price" forever,
  // indistinguishable from a closed market. These pin the hint that separates
  // the two, and the thresholds that stop it crying wolf.

  /** Days before "now", as an ISO instant, so these tests do not rot. */
  function daysAgo(days: number): string {
    return new Date(Date.now() - days * 24 * 60 * 60 * 1000).toISOString();
  }

  function unpricedAsset(overrides: Partial<AssetDto>): AssetDto {
    return { ...AAPL, id: 99, symbol: 'APPL', hasEverBeenPriced: false, ...overrides };
  }

  it('flags a long-unpriced asset and points at the identifier (D27)', async () => {
    fixture.detectChanges();
    httpMock
      .expectOne(API_ROUTES.assets)
      .flush([unpricedAsset({ createdAt: daysAgo(9), providerSymbol: 'APPL' })]);
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('No price in 9 days');
    expect(text).toContain('check this identifier');
  });

  it('does not cry wolf over a just-added asset that no cycle has reached yet', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([unpricedAsset({ createdAt: daysAgo(0) })]);
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('waiting for the next refresh');
    expect(text).not.toContain('check this identifier');
  });

  /**
   * The case that set the stock threshold at 3 days rather than 1. A US stock
   * added on a Friday evening cannot be priced until Monday, because the refresh
   * service deliberately never polls a closed market — about 65 hours of
   * completely correct silence. Warning then would fire on every stock added
   * over a weekend, and a warning that is usually wrong gets ignored when it is
   * finally right.
   */
  it('stays quiet for a stock added over a weekend, when no poll could have happened', async () => {
    fixture.detectChanges();
    httpMock
      .expectOne(API_ROUTES.assets)
      .flush([unpricedAsset({ createdAt: daysAgo(2), quoteProviderKind: 'TwelveData' })]);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).not.toContain('check this identifier');
  });

  /** Crypto has no such excuse — CoinGecko is unmetered, ungated, polled every 2 minutes. */
  it('flags an unpriced crypto asset after a single day', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([
      unpricedAsset({
        symbol: 'ETHEREUM',
        createdAt: daysAgo(2),
        assetClass: 'Crypto',
        quoteProviderKind: 'CoinGecko',
        providerSymbol: null,
        providerCoinId: 'etherium',
      }),
    ]);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('check this identifier');
  });

  // --- D34: the escalation above is only trustworthy when the provider it
  // blames has actually had a chance to prove itself. A fresh database (or
  // one whose seeded assets carry a static CreatedAt — exactly what happened
  // in the live container DB) makes every never-priced asset look days old on
  // day one, regardless of whether its identifier is right. This is the exact
  // case that caused it: old by CreatedAt, never priced, and the provider has
  // never once succeeded for anything in this database.

  it('stays in the gentle state for a long-unpriced asset whose provider has never once succeeded (D34)', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([
      unpricedAsset({
        createdAt: daysAgo(14),
        providerSymbol: 'AAPL',
        providerHasEverSucceeded: false,
      }),
    ]);
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('waiting for the next refresh');
    expect(text).not.toContain('check this identifier');
  });

  it('escalates again once a sibling on the same provider proves the pipe works (D34)', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([
      unpricedAsset({
        createdAt: daysAgo(14),
        providerSymbol: 'AAPL',
        providerHasEverSucceeded: true,
      }),
    ]);
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('No price in 14 days');
    expect(text).toContain('check this identifier');
  });

  it('says nothing about a deactivated asset, which is excluded from refresh cycles anyway', async () => {
    fixture.detectChanges();
    httpMock
      .expectOne(API_ROUTES.assets)
      .flush([unpricedAsset({ createdAt: daysAgo(30), isActive: false })]);
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).not.toContain('check this identifier');
    expect(text).not.toContain('waiting for the next refresh');
  });

  it('says nothing about an asset that has been priced', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).not.toContain('No price');
  });

  it('adds a newly-created asset to the list without a full reload', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
    await fixture.whenStable();
    fixture.detectChanges();

    const dialog = TestBed.inject(MatDialog);
    const created: AssetDto = {
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
    };
    vi.spyOn(dialog, 'open').mockReturnValue({
      afterClosed: () => of({ kind: 'saved', asset: created }),
    } as ReturnType<MatDialog['open']>);

    fixture.componentInstance.openCreateDialog();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('GOOGL');
    httpMock.verify();
  });

  it('optimistically deactivates via a full-replace PUT, and rolls back if it fails', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
    await fixture.whenStable();
    fixture.detectChanges();

    const dialog = TestBed.inject(MatDialog);
    vi.spyOn(dialog, 'open').mockReturnValue({ afterClosed: () => of(true) } as ReturnType<MatDialog['open']>);

    fixture.componentInstance.toggleActive(AAPL);
    fixture.detectChanges();

    // Optimistic — flips before the PUT resolves.
    expect(fixture.nativeElement.textContent).toContain('Inactive');

    const req = httpMock.expectOne(API_ROUTES.asset(AAPL.id));
    expect(req.request.method).toBe('PUT');
    // Full replace — every field present, not just isActive.
    expect(req.request.body).toEqual({
      symbol: 'AAPL',
      name: 'Apple Inc.',
      assetClass: 'Stock',
      exchange: 'NASDAQ',
      currency: 'USD',
      quoteProviderKind: 'TwelveData',
      providerSymbol: 'AAPL',
      providerCoinId: null,
      isActive: false,
    });
    req.flush('boom', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Active');
    expect(fixture.nativeElement.textContent).not.toContain('Inactive');
    expect(fixture.nativeElement.textContent).toContain("Couldn't deactivate");
  });

  it('reactivates an inactive asset the same way, in the other direction', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([MSFT_INACTIVE]);
    await fixture.whenStable();
    fixture.detectChanges();

    const dialog = TestBed.inject(MatDialog);
    vi.spyOn(dialog, 'open').mockReturnValue({ afterClosed: () => of(true) } as ReturnType<MatDialog['open']>);

    fixture.componentInstance.toggleActive(MSFT_INACTIVE);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Active');

    const req = httpMock.expectOne(API_ROUTES.asset(MSFT_INACTIVE.id));
    expect(req.request.body.isActive).toBe(true);
    req.flush({ ...MSFT_INACTIVE, isActive: true });
  });

  // --- Hard delete. Two things worth pinning: the confirmation names the
  // transaction count (the only fact on that screen that would make someone
  // press Cancel), and the row is removed only AFTER the server confirms —
  // showing a row vanish and reappear would read as data loss.

  it('counts the transactions on the asset before asking, and names them in the confirmation', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
    await fixture.whenStable();
    fixture.detectChanges();

    const dialog = TestBed.inject(MatDialog);
    const open = vi
      .spyOn(dialog, 'open')
      .mockReturnValue({ afterClosed: () => of(false) } as ReturnType<MatDialog['open']>);

    fixture.componentInstance.deleteAsset(AAPL);

    const countReq = httpMock.expectOne(API_ROUTES.transactionsByAsset(AAPL.id));
    expect(countReq.request.method).toBe('GET');
    countReq.flush([{ id: 1 }, { id: 2 }, { id: 3 }]);

    expect(open).toHaveBeenCalled();
    const data = open.mock.calls[0][1]?.data as { title: string; message: string };
    expect(data.title).toContain('AAPL');
    expect(data.message).toContain('3 transactions');
    expect(data.message).toContain('cannot be undone');
  });

  it('still offers the delete when the transaction count cannot be fetched, without claiming zero', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
    await fixture.whenStable();
    fixture.detectChanges();

    const dialog = TestBed.inject(MatDialog);
    const open = vi
      .spyOn(dialog, 'open')
      .mockReturnValue({ afterClosed: () => of(false) } as ReturnType<MatDialog['open']>);

    fixture.componentInstance.deleteAsset(AAPL);
    httpMock
      .expectOne(API_ROUTES.transactionsByAsset(AAPL.id))
      .flush('boom', { status: 500, statusText: 'Server Error' });

    expect(open).toHaveBeenCalled();
    const data = open.mock.calls[0][1]?.data as { message: string };
    expect(data.message).toContain('transactions and price history');
    expect(data.message).not.toContain('no transactions');
    expect(data.message).not.toContain('0 transaction');
  });

  it('DELETEs the asset and drops the row only once the server confirms', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL, MSFT_INACTIVE]);
    await fixture.whenStable();
    fixture.detectChanges();

    const dialog = TestBed.inject(MatDialog);
    vi.spyOn(dialog, 'open').mockReturnValue({ afterClosed: () => of(true) } as ReturnType<MatDialog['open']>);

    fixture.componentInstance.deleteAsset(AAPL);
    httpMock.expectOne(API_ROUTES.transactionsByAsset(AAPL.id)).flush([]);
    fixture.detectChanges();

    // Still there while the DELETE is in flight — pessimistic, unlike the toggle.
    expect(fixture.nativeElement.textContent).toContain('AAPL');

    const req = httpMock.expectOne(API_ROUTES.asset(AAPL.id));
    expect(req.request.method).toBe('DELETE');
    req.flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).not.toContain('AAPL');
    expect(text).toContain('MSFT');
  });

  it('keeps the row and reports the failure when the DELETE fails', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
    await fixture.whenStable();
    fixture.detectChanges();

    const dialog = TestBed.inject(MatDialog);
    vi.spyOn(dialog, 'open').mockReturnValue({ afterClosed: () => of(true) } as ReturnType<MatDialog['open']>);

    fixture.componentInstance.deleteAsset(AAPL);
    httpMock.expectOne(API_ROUTES.transactionsByAsset(AAPL.id)).flush([]);
    httpMock
      .expectOne(API_ROUTES.asset(AAPL.id))
      .flush('boom', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('AAPL');
    expect(text).toContain("Couldn't delete AAPL");
  });

  it('does not touch the server when the confirmation is cancelled', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([AAPL]);
    await fixture.whenStable();
    fixture.detectChanges();

    const dialog = TestBed.inject(MatDialog);
    vi.spyOn(dialog, 'open').mockReturnValue({ afterClosed: () => of(false) } as ReturnType<MatDialog['open']>);

    fixture.componentInstance.deleteAsset(AAPL);
    httpMock.expectOne(API_ROUTES.transactionsByAsset(AAPL.id)).flush([]);
    fixture.detectChanges();

    // No DELETE at all — httpMock.verify() in afterEach is the assertion.
    expect(fixture.nativeElement.textContent).toContain('AAPL');
  });
});
