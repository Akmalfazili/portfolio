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
});
