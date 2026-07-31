import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { App } from './app';
import { routes } from './app.routes';
import { PRICES_HUB_CONNECTION_FACTORY } from './core/prices/price-store';
import { FakeHubConnection } from './core/prices/testing/fake-hub-connection';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideRouter(routes),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
        { provide: PRICES_HUB_CONNECTION_FACTORY, useValue: () => new FakeHubConnection() },
      ],
    }).compileComponents();
  });

  it('creates the app shell', () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('renders the sidenav navigation for Stocks, Crypto and Transactions', () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Stocks');
    expect(text).toContain('Crypto');
    expect(text).toContain('Transactions');
  });
});
