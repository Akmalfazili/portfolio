import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { TransactionsPage } from './transactions.page';
import { API_ROUTES } from '../../core/api/api-routes';
import { TransactionDto } from '../../core/api/models';

const TRANSACTION: TransactionDto = {
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

describe('TransactionsPage', () => {
  let fixture: ComponentFixture<TransactionsPage>;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [TransactionsPage],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(TransactionsPage);
  });

  afterEach(() => httpMock.verify());

  it('renders a sub-cent price and a 10dp quantity correctly in the table, never rounded to $0.00', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.transactions).flush([TRANSACTION]);
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('1,000,000');
    expect(text).toContain('$0.0005326');
  });

  it('shows the empty state when there are no transactions', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.transactions).flush([]);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('No transactions yet');
  });

  it('shows the error state on a failed load', async () => {
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.transactions).flush('boom', { status: 500, statusText: 'Server Error' });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain("Couldn't load transactions");
  });
});
