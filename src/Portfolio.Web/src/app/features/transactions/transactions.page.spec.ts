import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MatDialog } from '@angular/material/dialog';
import { of } from 'rxjs';

import { TransactionsPage } from './transactions.page';
import { API_ROUTES } from '../../core/api/api-routes';
import { TransactionDto } from '../../core/api/models';
import { NotificationService } from '../../core/notifications/notification.service';

const ANVL_BUY: TransactionDto = {
  id: 1,
  assetId: 2,
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

const AAPL_BUY: TransactionDto = {
  id: 2,
  assetId: 1,
  assetSymbol: 'AAPL',
  assetClass: 'Stock',
  type: 'Buy',
  tradeDate: '2026-07-20',
  quantity: 10,
  pricePerUnit: 200,
  fees: 0,
  currency: 'USD',
  notes: null,
};

describe('TransactionsPage', () => {
  let fixture: ComponentFixture<TransactionsPage>;
  let httpMock: HttpTestingController;
  let notifySuccess: ReturnType<typeof vi.fn>;
  let notifyError: ReturnType<typeof vi.fn>;
  let notifyInfo: ReturnType<typeof vi.fn>;

  function flushAssets(): void {
    httpMock.expectOne(API_ROUTES.assets).flush([]);
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [TransactionsPage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations()],
    });
    httpMock = TestBed.inject(HttpTestingController);
    // Discrete action outcomes go through NotificationService, not an inline
    // banner — spy on it rather than digging through the CDK overlay for a
    // snackbar's rendered text.
    const notifications = TestBed.inject(NotificationService);
    notifySuccess = vi.spyOn(notifications, 'success').mockImplementation(() => {});
    notifyError = vi.spyOn(notifications, 'error').mockImplementation(() => {});
    notifyInfo = vi.spyOn(notifications, 'info').mockImplementation(() => {});
    fixture = TestBed.createComponent(TransactionsPage);
  });

  afterEach(() => httpMock.verify());

  it('renders a sub-cent price and a 10dp quantity correctly in the table, never rounded to $0.00', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.transactions).flush([ANVL_BUY]);
    flushAssets();
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('1,000,000');
    expect(text).toContain('$0.0005326');
  });

  it('shows the empty state when there are no transactions at all', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.transactions).flush([]);
    flushAssets();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('No transactions yet');
  });

  it('shows the error state on a failed load, independent of the assets request', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.transactions).flush('boom', { status: 500, statusText: 'Server Error' });
    flushAssets();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain("Couldn't load transactions");
  });

  it('filters by asset class without re-fetching, and shows a distinct empty state for a filter that matches nothing', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.transactions).flush([ANVL_BUY, AAPL_BUY]);
    flushAssets();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('ANVL');
    expect(fixture.nativeElement.textContent).toContain('AAPL');

    fixture.componentInstance.setFilter('Crypto');
    fixture.detectChanges();
    let text = fixture.nativeElement.textContent as string;
    expect(text).toContain('ANVL');
    expect(text).not.toContain('AAPL');

    fixture.componentInstance.setFilter('Stock');
    fixture.detectChanges();
    text = fixture.nativeElement.textContent as string;
    expect(text).toContain('AAPL');
    expect(text).not.toContain('ANVL');
  });

  it('shows "no matching transactions" (not the zero-transactions empty state) when a filter matches nothing', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.transactions).flush([ANVL_BUY]);
    flushAssets();
    await fixture.whenStable();
    fixture.detectChanges();

    fixture.componentInstance.setFilter('Stock');
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('No matching transactions');
    expect(text).not.toContain('No transactions yet');
  });

  it('warns (without blocking the list) when the asset list fails to load', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.transactions).flush([ANVL_BUY]);
    httpMock.expectOne(API_ROUTES.assets).flush('boom', { status: 500, statusText: 'Server Error' });
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain("Couldn't load the asset list");
    expect(text).toContain('ANVL');
  });

  it('adds a newly-saved transaction to the list without a full reload', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.transactions).flush([ANVL_BUY]);
    flushAssets();
    await fixture.whenStable();
    fixture.detectChanges();

    const dialog = TestBed.inject(MatDialog);
    const created: TransactionDto = { ...AAPL_BUY, id: 3, tradeDate: '2026-08-01' };
    vi.spyOn(dialog, 'open').mockReturnValue({
      afterClosed: () => of({ kind: 'saved', transaction: created }),
    } as ReturnType<MatDialog['open']>);

    fixture.componentInstance.openCreateDialog();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('AAPL');
    expect(notifySuccess).toHaveBeenCalledWith('Transaction recorded.');
    httpMock.verify();
  });

  it('shows a distinct success toast for an edit, not the create wording', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.transactions).flush([ANVL_BUY]);
    flushAssets();
    await fixture.whenStable();
    fixture.detectChanges();

    const dialog = TestBed.inject(MatDialog);
    const edited: TransactionDto = { ...ANVL_BUY, quantity: 2000000 };
    vi.spyOn(dialog, 'open').mockReturnValue({
      afterClosed: () => of({ kind: 'saved', transaction: edited }),
    } as ReturnType<MatDialog['open']>);

    fixture.componentInstance.openEditDialog(ANVL_BUY);
    fixture.detectChanges();

    expect(notifySuccess).toHaveBeenCalledWith('Transaction updated.');
    expect(notifySuccess).not.toHaveBeenCalledWith('Transaction recorded.');
  });

  it('reloads and shows an info toast (not an error) when the edited row was deleted elsewhere', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.transactions).flush([ANVL_BUY]);
    flushAssets();
    await fixture.whenStable();
    fixture.detectChanges();

    const dialog = TestBed.inject(MatDialog);
    vi.spyOn(dialog, 'open').mockReturnValue({
      afterClosed: () => of({ kind: 'deleted-elsewhere' }),
    } as ReturnType<MatDialog['open']>);

    fixture.componentInstance.openEditDialog(ANVL_BUY);
    fixture.detectChanges();

    httpMock.expectOne(API_ROUTES.transactions).flush([]); // reload() triggered by deleted-elsewhere
    await fixture.whenStable();
    fixture.detectChanges();

    expect(notifyInfo).toHaveBeenCalledWith(
      'That transaction no longer exists — the list has been refreshed.',
    );
    expect(notifyError).not.toHaveBeenCalled();
    expect(notifySuccess).not.toHaveBeenCalled();
  });

  it('does not open the create dialog before the asset list has loaded — there would be nothing to pick', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.transactions).flush([ANVL_BUY]);
    // Assets request deliberately left unflushed — still in flight.

    const dialog = TestBed.inject(MatDialog);
    const openSpy = vi.spyOn(dialog, 'open');

    fixture.componentInstance.openCreateDialog();

    expect(openSpy).not.toHaveBeenCalled();
    flushAssets();
  });

  it('shows a success toast once the DELETE completes', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.transactions).flush([ANVL_BUY]);
    flushAssets();
    await fixture.whenStable();
    fixture.detectChanges();

    const dialog = TestBed.inject(MatDialog);
    vi.spyOn(dialog, 'open').mockReturnValue({
      afterClosed: () => of(true),
    } as ReturnType<MatDialog['open']>);

    fixture.componentInstance.deleteTransaction(ANVL_BUY);
    httpMock.expectOne(API_ROUTES.transaction(ANVL_BUY.id)).flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    expect(notifySuccess).toHaveBeenCalledWith('ANVL transaction deleted.');
    expect(notifyError).not.toHaveBeenCalled();
  });

  it('optimistically removes a row on delete, and restores it if the DELETE call fails', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.transactions).flush([ANVL_BUY]);
    flushAssets();
    await fixture.whenStable();
    fixture.detectChanges();

    const dialog = TestBed.inject(MatDialog);
    vi.spyOn(dialog, 'open').mockReturnValue({
      afterClosed: () => of(true),
    } as ReturnType<MatDialog['open']>);

    fixture.componentInstance.deleteTransaction(ANVL_BUY);
    fixture.detectChanges();

    // Optimistic removal happens before the DELETE call resolves.
    expect(fixture.nativeElement.textContent).not.toContain('ANVL');

    httpMock.expectOne(API_ROUTES.transaction(ANVL_BUY.id)).flush('boom', {
      status: 500,
      statusText: 'Server Error',
    });
    fixture.detectChanges();

    httpMock.expectOne(API_ROUTES.transactions).flush([ANVL_BUY]); // reload() rollback
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('ANVL');
    expect(notifyError).toHaveBeenCalledWith(
      "Couldn't delete the ANVL transaction — it has been restored.",
    );
    expect(notifySuccess).not.toHaveBeenCalled();
  });
});
