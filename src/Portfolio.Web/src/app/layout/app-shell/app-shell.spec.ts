import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { AppShell } from './app-shell';
import { routes } from '../../app.routes';
import { API_ROUTES } from '../../core/api/api-routes';
import { PRICES_HUB_CONNECTION_FACTORY } from '../../core/prices/price-store';
import { FakeHubConnection } from '../../core/prices/testing/fake-hub-connection';

describe('AppShell — section accent token switching', () => {
  let fixture: ComponentFixture<AppShell>;
  let router: Router;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    document.documentElement.removeAttribute('data-section');
    TestBed.configureTestingModule({
      imports: [AppShell],
      providers: [
        provideRouter(routes, withComponentInputBinding()),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
        { provide: PRICES_HUB_CONNECTION_FACTORY, useValue: () => new FakeHubConnection() },
      ],
    });

    httpMock = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    fixture = TestBed.createComponent(AppShell);
    fixture.detectChanges();
  });

  afterEach(() => {
    httpMock.verify();
    document.documentElement.removeAttribute('data-section');
  });

  it('sets data-section="stock" for /stocks, driving --ui-color-accent-stock', async () => {
    await router.navigateByUrl('/stocks');
    fixture.detectChanges();
    expect(document.documentElement.getAttribute('data-section')).toBe('stock');
    httpMock.expectOne(API_ROUTES.assets).flush([]);
  });

  it('sets data-section="crypto" for /crypto, driving --ui-color-accent-crypto', async () => {
    await router.navigateByUrl('/crypto');
    fixture.detectChanges();
    expect(document.documentElement.getAttribute('data-section')).toBe('crypto');
    httpMock.expectOne(API_ROUTES.assets).flush([]);
  });

  it('swaps the attribute again when navigating from /stocks to /crypto', async () => {
    await router.navigateByUrl('/stocks');
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([]);
    expect(document.documentElement.getAttribute('data-section')).toBe('stock');

    await router.navigateByUrl('/crypto');
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.assets).flush([]);
    expect(document.documentElement.getAttribute('data-section')).toBe('crypto');
  });

  it('defaults to "stock" for a section-less route like /transactions', async () => {
    await router.navigateByUrl('/transactions');
    fixture.detectChanges();
    httpMock.expectOne(API_ROUTES.transactions).flush([]);
    httpMock.expectOne(API_ROUTES.assets).flush([]);
    expect(document.documentElement.getAttribute('data-section')).toBe('stock');
  });
});
