import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';

import { RefreshIndicator } from './refresh-indicator';
import { PriceStore } from '../../core/prices/price-store';
import { NotificationService } from '../../core/notifications/notification.service';
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
    lastError: signal<string | null>(null),
    refreshNow: () => undefined,
    ...overrides,
  };
}

function makeFakeNotifications() {
  return { success: vi.fn(), error: vi.fn(), info: vi.fn() };
}

function status(overrides: Partial<PriceRefreshStatus> = {}): PriceRefreshStatus {
  return {
    lastRefreshedAt: '2026-07-31T04:00:00Z',
    nyseOpen: true,
    sgxOpen: true,
    nextScheduledRunAt: null,
    sources: [],
    ...overrides,
  };
}

describe('RefreshIndicator', () => {
  let fixture: ComponentFixture<RefreshIndicator>;
  let fakeStore: ReturnType<typeof makeFakeStore>;
  let fakeNotifications: ReturnType<typeof makeFakeNotifications>;

  function setup(overrides: Partial<Record<string, unknown>> = {}) {
    fakeStore = makeFakeStore(overrides);
    fakeNotifications = makeFakeNotifications();
    TestBed.configureTestingModule({
      imports: [RefreshIndicator],
      providers: [
        { provide: PriceStore, useValue: fakeStore },
        { provide: NotificationService, useValue: fakeNotifications },
      ],
    });
    fixture = TestBed.createComponent(RefreshIndicator);
    fixture.detectChanges();
  }

  it('shows a relative "Updated" label from the ticking signal', () => {
    setup();
    const label: HTMLElement = fixture.nativeElement.querySelector('.refresh-indicator__label');
    expect(label.textContent).toContain('Updated');
  });

  it('exposes an accessible name and expanded state on the details-panel trigger', () => {
    setup();
    const trigger: HTMLButtonElement = fixture.nativeElement.querySelector(
      '.refresh-indicator__trigger',
    );
    expect(trigger.getAttribute('aria-label')).toContain('Refresh details');
    // MatMenuTrigger manages aria-haspopup/aria-expanded itself once wired to [matMenuTriggerFor].
    expect(trigger.getAttribute('aria-haspopup')).toBe('menu');
    expect(trigger.getAttribute('aria-expanded')).toBe('false');
  });

  it('disables the button and shows the countdown while a cooldown is active', () => {
    setup({ cooldownSecondsRemaining: signal(17) });
    const button: HTMLButtonElement =
      fixture.nativeElement.querySelector('button[mat-icon-button]');
    expect(button.disabled).toBe(true);
    expect(
      fixture.nativeElement.querySelector('.refresh-indicator__cooldown').textContent,
    ).toContain('17s');
  });

  it('calls refreshNow() on click when not disabled', () => {
    setup();
    const spy = vi.spyOn(fakeStore, 'refreshNow');
    const button: HTMLButtonElement =
      fixture.nativeElement.querySelector('button[mat-icon-button]');
    button.click();
    expect(spy).toHaveBeenCalled();
  });

  it('shows the degraded (wifi-off) warning state when the socket is genuinely down', () => {
    setup({ isDegraded: signal(true), connectionState: signal('polling-fallback') });
    expect(fixture.nativeElement.textContent).toContain('wifi_off');
  });

  describe('idle pre-click tooltip', () => {
    it('previews what a press will do right now, before any click', () => {
      setup({ status: signal(status({ nyseOpen: true, sgxOpen: false })) });
      expect(fixture.componentInstance.tooltip()).toContain('SGX is closed');
    });

    it('keeps the degraded tooltip state working', () => {
      setup({ isDegraded: signal(true) });
      expect(fixture.componentInstance.tooltip()).toContain('reconnecting');
    });

    it('keeps the cooldown tooltip state working', () => {
      setup({ cooldownSecondsRemaining: signal(12) });
      expect(fixture.componentInstance.tooltip()).toBe('Refresh available in 12s.');
    });

    it('keeps the refreshing tooltip state working', () => {
      setup({ refreshing: signal(true) });
      expect(fixture.componentInstance.tooltip()).toBe('Refreshing…');
    });

    it('keeps the stale tooltip state working, alongside the idle preview', () => {
      setup({ status: signal(null) }); // no status at all => isRefreshStale() is true
      const tooltip = fixture.componentInstance.tooltip();
      expect(tooltip).toContain('Prices look stale');
      expect(tooltip).toContain('Fetches live prices now');
    });
  });

  describe('details panel — market rows', () => {
    it('null status renders as "waiting", never a grid of closed markets', () => {
      setup({ status: signal(null) });
      expect(fixture.componentInstance.marketRows()).toEqual([]);
      expect(fixture.componentInstance.status()).toBeNull();
    });

    it('a closed market is distinguishable from an open one', () => {
      setup({ status: signal(status({ nyseOpen: false, sgxOpen: true })) });
      const rows = fixture.componentInstance.marketRows();
      const us = rows.find((r) => r.provider === 'TwelveData')!;
      const sgx = rows.find((r) => r.provider === 'Yahoo')!;
      expect(us.isOpen).toBe(false);
      expect(sgx.isOpen).toBe(true);
    });

    it('a source missing from `sources` reads as "waiting for first update", not zero or failed', () => {
      setup({ status: signal(status({ sources: [] })) });
      const rows = fixture.componentInstance.marketRows();
      expect(rows.every((r) => !r.hasData)).toBe(true);
      expect(rows.every((r) => r.freshnessLabel === 'Waiting for first update')).toBe(true);
      expect(rows.every((r) => !r.attemptedAndFailed)).toBe(true);
    });

    it('an attempted-and-failed source is surfaced distinctly, with a short summary', () => {
      setup({
        status: signal(
          status({
            sources: [
              {
                source: 'Yahoo',
                lastAttemptedAt: '2026-07-31T04:00:00Z',
                lastSuccessAt: '2026-07-28T04:00:00Z',
                lastRunSuccess: false,
                lastError: 'HTTP 500 from chart endpoint',
                symbolsRefreshed: 0,
                nextDueAt: '2026-07-31T05:00:00Z',
              },
            ],
          }),
        ),
      });
      const sgx = fixture.componentInstance.marketRows().find((r) => r.provider === 'Yahoo')!;
      expect(sgx.attemptedAndFailed).toBe(true);
      expect(sgx.errorSummary).toBe('HTTP 500 from chart endpoint');
    });

    it('times an attempted-and-failed row from lastAttemptedAt, not from when it is being viewed', () => {
      // The live D-style scenario this fix addresses: a Friday-evening failure
      // on a market that is now closed for the weekend, viewed on Sunday. The
      // row must time the FAILURE, not read as if it just happened.
      setup({
        status: signal(
          status({
            sgxOpen: false,
            sources: [
              {
                source: 'Yahoo',
                lastAttemptedAt: '2026-07-31T04:00:00Z',
                lastSuccessAt: '2026-07-28T04:00:00Z',
                lastRunSuccess: false,
                lastError: 'Yahoo request failed.',
                symbolsRefreshed: 0,
                nextDueAt: null,
              },
            ],
          }),
        ),
      });
      const sgx = fixture.componentInstance.marketRows().find((r) => r.provider === 'Yahoo')!;
      expect(sgx.attemptedAndFailed).toBe(true);
      expect(sgx.lastAttemptedLabel).not.toBeNull();
      expect(sgx.lastAttemptedLabel).not.toBe('never');
    });

    it('degrades to a null lastAttemptedLabel, never "Invalid Date", when the failed row has no lastAttemptedAt', () => {
      setup({
        status: signal(
          status({
            sources: [
              {
                source: 'Yahoo',
                lastAttemptedAt: null,
                lastSuccessAt: '2026-07-28T04:00:00Z',
                lastRunSuccess: false,
                lastError: 'Yahoo request failed.',
                symbolsRefreshed: 0,
                nextDueAt: null,
              },
            ],
          }),
        ),
      });
      const sgx = fixture.componentInstance.marketRows().find((r) => r.provider === 'Yahoo')!;
      expect(sgx.attemptedAndFailed).toBe(true);
      expect(sgx.lastAttemptedLabel).toBeNull();
    });

    it('the exact 2026-09-13 live scenario: a stale SGX live failure is superseded by a later recorded close', () => {
      // Fri 2026-09-11 08:56 UTC: Yahoo's last live poll fails in a host
      // network outage. SGX closes at 09:00 UTC and stays closed all weekend.
      // Sat 2026-09-12 17:01 UTC: Friday's SGX close backfills successfully.
      // Viewed Sun 2026-09-13: the panel must show the close, not the stale
      // live failure.
      const nowSpy = vi
        .spyOn(Date, 'now')
        .mockReturnValue(new Date('2026-09-13T10:00:00Z').getTime());
      try {
        setup({
          status: signal(
            status({
              sgxOpen: false,
              sources: [
                {
                  source: 'Yahoo',
                  lastAttemptedAt: '2026-09-11T08:56:27Z',
                  lastSuccessAt: '2026-09-04T09:00:00Z',
                  lastRunSuccess: false,
                  lastError: 'Yahoo request failed.',
                  symbolsRefreshed: 0,
                  nextDueAt: null,
                },
              ],
              closes: [
                {
                  market: 'Sgx',
                  latestCloseDate: '2026-09-11',
                  lastAttemptedAt: '2026-09-12T17:01:22Z',
                  lastSuccessAt: '2026-09-12T17:01:22Z',
                  lastRunSuccess: true,
                  lastError: null,
                },
              ],
            }),
          ),
        });
        fixture.detectChanges();

        const sgx = fixture.componentInstance.marketRows().find((r) => r.provider === 'Yahoo')!;
        expect(sgx.attemptedAndFailed).toBe(true); // the raw fact is unchanged
        expect(sgx.showLiveFailure).toBe(false); // but the stale line is suppressed
        expect(sgx.freshnessLabel).toBe('Closing prices · Fri 11 Sep');
        expect(sgx.closeFailedLabel).toBeNull();
      } finally {
        nowSpy.mockRestore();
      }
    });
  });

  // `<mat-menu>` content is rendered into a CDK overlay only once the menu is
  // actually opened — it is not present in `fixture.nativeElement` just
  // because the host component was created — so these assert against the
  // component's own signals, the same convention the rest of this spec file
  // already uses for the (identical, pre-existing) `marketRows()` tests
  // above rather than querying menu DOM that was never instantiated.
  describe('details panel — row state text', () => {
    it('reports Open/Closed/24-7 per row, never the old "Always on" wording', () => {
      setup({ status: signal(status({ nyseOpen: false, sgxOpen: true })) });
      const rows = fixture.componentInstance.marketRows();
      const us = rows.find((r) => r.provider === 'TwelveData')!;
      const sgx = rows.find((r) => r.provider === 'Yahoo')!;
      const crypto = rows.find((r) => r.provider === 'CoinGecko')!;
      expect(us.isOpen).toBe(false);
      expect(sgx.isOpen).toBe(true);
      expect(crypto.alwaysOpen).toBe(true);
    });

    it('exposes a freshness line for each row', () => {
      setup({
        status: signal(
          status({
            nyseOpen: true,
            sources: [
              {
                source: 'TwelveData',
                lastAttemptedAt: '2026-07-31T11:55:00Z',
                lastSuccessAt: '2026-07-31T11:55:00Z',
                lastRunSuccess: true,
                lastError: null,
                symbolsRefreshed: 4,
                nextDueAt: null,
              },
            ],
          }),
        ),
      });
      const us = fixture.componentInstance.marketRows().find((r) => r.provider === 'TwelveData')!;
      expect(us.freshnessLabel).toContain('Updated');
    });
  });

  describe('details panel — footer scope statement', () => {
    it('renders what the button will do right now, keyed off the live status', () => {
      setup({ status: signal(status({ nyseOpen: false, sgxOpen: true })) });
      expect(fixture.componentInstance.refreshScopeLabel()).toBe(
        'Refresh updates SGX and crypto now.',
      );
    });

    it('says "all markets" when nothing is closed', () => {
      setup({ status: signal(status({ nyseOpen: true, sgxOpen: true })) });
      expect(fixture.componentInstance.refreshScopeLabel()).toBe('Refresh updates all markets now.');
    });

    it('falls back to a generic message before the first status snapshot arrives', () => {
      setup({ status: signal(null) });
      expect(fixture.componentInstance.refreshScopeLabel()).toBe('Refresh fetches live prices now.');
    });
  });

  describe('post-refresh outcome — moved to a snackbar, not the tooltip', () => {
    it('shows an info toast for a normal completed outcome after a click this component made', () => {
      setup();
      fixture.componentInstance.refresh();
      const result: PriceRefreshCycleResult = {
        outcome: 'Completed',
        cooldownSecondsRemaining: null,
        totalSymbolsRefreshed: 3,
        sources: [
          { source: 'CoinGecko', attempted: true, success: true, symbolsRefreshed: 3, error: null },
        ],
      };
      (fakeStore.lastRefreshResult as ReturnType<typeof signal>).set(result);
      fixture.detectChanges();

      expect(fakeNotifications.info).toHaveBeenCalledWith('Refreshed 3 symbols (crypto).');
      expect(fakeNotifications.error).not.toHaveBeenCalled();
    });

    it('shows an error toast when the result reports an attempted-and-failed source', () => {
      setup();
      fixture.componentInstance.refresh();
      const result: PriceRefreshCycleResult = {
        outcome: 'Completed',
        cooldownSecondsRemaining: null,
        totalSymbolsRefreshed: 0,
        sources: [
          {
            source: 'TwelveData',
            attempted: true,
            success: false,
            symbolsRefreshed: 0,
            error: 'timeout',
          },
        ],
      };
      (fakeStore.lastRefreshResult as ReturnType<typeof signal>).set(result);
      fixture.detectChanges();

      expect(fakeNotifications.error).toHaveBeenCalled();
      expect(fakeNotifications.info).not.toHaveBeenCalled();
    });

    it('does NOT toast a result the component never asked for (not user-initiated here)', () => {
      setup();
      // No call to component.refresh() — lastRefreshResult changing on its own
      // (e.g. a background sweep another tab/component triggered) must not
      // toast from an indicator that did not click the button itself.
      const result: PriceRefreshCycleResult = {
        outcome: 'Completed',
        cooldownSecondsRemaining: null,
        totalSymbolsRefreshed: 1,
        sources: [
          { source: 'CoinGecko', attempted: true, success: true, symbolsRefreshed: 1, error: null },
        ],
      };
      (fakeStore.lastRefreshResult as ReturnType<typeof signal>).set(result);
      fixture.detectChanges();

      expect(fakeNotifications.info).not.toHaveBeenCalled();
      expect(fakeNotifications.error).not.toHaveBeenCalled();
    });

    it('shows an error toast from lastError when the manual call fails outright', () => {
      setup();
      fixture.componentInstance.refresh();
      (fakeStore.lastError as ReturnType<typeof signal>).set(
        'Manual refresh failed — try again shortly.',
      );
      fixture.detectChanges();

      expect(fakeNotifications.error).toHaveBeenCalledWith(
        'Manual refresh failed — try again shortly.',
      );
    });
  });
});
