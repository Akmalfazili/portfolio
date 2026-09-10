import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';

import { RefreshIndicator } from './refresh-indicator';
import { PriceStore } from '../../core/prices/price-store';
import { PriceRefreshCycleResult, PriceRefreshStatus } from '../../core/api/models';

function makeFakeStore(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    connectionState: signal('connected'),
    refreshing: signal(false),
    cooldownSecondsRemaining: signal<number | null>(null),
    status: signal<PriceRefreshStatus | null>(null),
    lastRefreshedAt: signal<string | null>('2026-07-31T11:58:00Z'),
    isDegraded: signal(false),
    lastRefreshResult: signal<PriceRefreshCycleResult | null>(null),
    refreshNow: () => undefined,
    ...overrides,
  };
}

describe('RefreshIndicator', () => {
  let fixture: ComponentFixture<RefreshIndicator>;
  let fakeStore: ReturnType<typeof makeFakeStore>;

  function setup(overrides: Partial<Record<string, unknown>> = {}) {
    fakeStore = makeFakeStore(overrides);
    TestBed.configureTestingModule({
      imports: [RefreshIndicator],
      providers: [{ provide: PriceStore, useValue: fakeStore }],
    });
    fixture = TestBed.createComponent(RefreshIndicator);
    fixture.detectChanges();
  }

  it('shows a relative "Updated" label from the ticking signal', () => {
    setup();
    const label: HTMLElement = fixture.nativeElement.querySelector('.refresh-indicator__label');
    expect(label.textContent).toContain('Updated');
  });

  it('disables the button and shows the countdown while a cooldown is active', () => {
    setup({ cooldownSecondsRemaining: signal(17) });
    const button: HTMLButtonElement = fixture.nativeElement.querySelector('button');
    expect(button.disabled).toBe(true);
    expect(
      fixture.nativeElement.querySelector('.refresh-indicator__cooldown').textContent,
    ).toContain('17s');
  });

  it('calls refreshNow() on click when not disabled', () => {
    setup();
    const spy = vi.spyOn(fakeStore, 'refreshNow');
    const button: HTMLButtonElement = fixture.nativeElement.querySelector('button');
    button.click();
    expect(spy).toHaveBeenCalled();
  });

  it('shows the degraded (wifi-off) warning state when the socket is genuinely down', () => {
    setup({ isDegraded: signal(true), connectionState: signal('polling-fallback') });
    expect(fixture.nativeElement.textContent).toContain('wifi_off');
  });

  it('surfaces the D5 outcome message (why nothing moved) after a manual refresh', () => {
    const result: PriceRefreshCycleResult = {
      outcome: 'Completed',
      cooldownSecondsRemaining: null,
      totalSymbolsRefreshed: 0,
      // A gated provider is omitted from `sources` by the backend, not listed with
      // attempted:false — so only CoinGecko appears here, as it does live.
      sources: [
        { source: 'CoinGecko', attempted: true, success: true, symbolsRefreshed: 0, error: null },
      ],
    };
    setup({
      lastRefreshResult: signal(result),
      status: signal<PriceRefreshStatus>({
        lastRefreshedAt: '2026-07-31T04:00:00Z',
        nyseOpen: false,
        sgxOpen: false,
        nextScheduledRunAt: null,
        sources: [],
      }),
    });

    const component = fixture.componentInstance;
    expect(component.tooltip()).toContain('US market and SGX are closed');
  });
});
