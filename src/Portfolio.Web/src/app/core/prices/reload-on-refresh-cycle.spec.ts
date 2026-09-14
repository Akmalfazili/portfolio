import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';

import { reloadOnRefreshCycle } from './reload-on-refresh-cycle';
import { PRICES_HUB_CONNECTION_FACTORY } from './price-store';
import { FakeHubConnection } from './testing/fake-hub-connection';
import { CatchUpCompletedNotification, PriceRefreshStatus } from '../api/models';

@Component({ selector: 'app-reload-host', standalone: true, template: '' })
class ReloadHost {
  readonly onCycleCompleted = vi.fn();
  constructor() {
    reloadOnRefreshCycle(this.onCycleCompleted);
  }
}

function status(overrides: Partial<PriceRefreshStatus> = {}): PriceRefreshStatus {
  return {
    lastRefreshedAt: '2026-09-14T10:00:00Z',
    nyseOpen: true,
    sgxOpen: true,
    nextScheduledRunAt: null,
    sources: [],
    ...overrides,
  };
}

function completion(
  overrides: Partial<CatchUpCompletedNotification> = {},
): CatchUpCompletedNotification {
  return {
    kind: 'PriceHistory',
    succeededSymbols: ['NEWCO'],
    failed: [],
    skippedForBudgetSymbols: [],
    rowsInserted: 5,
    completedAt: '2026-09-14T10:01:00Z',
    runId: 'run-1',
    ...overrides,
  };
}

describe('reloadOnRefreshCycle', () => {
  let fakeHub: FakeHubConnection;
  let fixture: ComponentFixture<ReloadHost>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: PRICES_HUB_CONNECTION_FACTORY,
          useValue: () => (fakeHub = new FakeHubConnection()),
        },
      ],
    });
  });

  describe('lastRefreshedAt trigger', () => {
    it('does not fire on the first observation — the page already loaded fresh on mount', () => {
      fixture = TestBed.createComponent(ReloadHost);
      fixture.detectChanges();

      fakeHub.emit('RefreshStatus', status());
      fixture.detectChanges();

      expect(fixture.componentInstance.onCycleCompleted).not.toHaveBeenCalled();
    });

    it('fires once on a genuine subsequent change', () => {
      fixture = TestBed.createComponent(ReloadHost);
      fixture.detectChanges();

      fakeHub.emit('RefreshStatus', status());
      fixture.detectChanges();
      fakeHub.emit('RefreshStatus', status({ lastRefreshedAt: '2026-09-14T10:05:00Z' }));
      fixture.detectChanges();

      expect(fixture.componentInstance.onCycleCompleted).toHaveBeenCalledTimes(1);
    });

    it('does not fire again when the value is unchanged', () => {
      fixture = TestBed.createComponent(ReloadHost);
      fixture.detectChanges();

      fakeHub.emit('RefreshStatus', status());
      fixture.detectChanges();
      fakeHub.emit('RefreshStatus', status({ lastRefreshedAt: '2026-09-14T10:05:00Z' }));
      fixture.detectChanges();
      fakeHub.emit('RefreshStatus', status({ lastRefreshedAt: '2026-09-14T10:05:00Z' }));
      fixture.detectChanges();

      expect(fixture.componentInstance.onCycleCompleted).toHaveBeenCalledTimes(1);
    });
  });

  describe('catch-up landing marker trigger', () => {
    it('fires once when a CatchUpCompleted lands with rowsInserted > 0', () => {
      fixture = TestBed.createComponent(ReloadHost);
      fixture.detectChanges();

      fakeHub.emit('CatchUpCompleted', completion({ rowsInserted: 5 }));
      fixture.detectChanges();

      expect(fixture.componentInstance.onCycleCompleted).toHaveBeenCalledTimes(1);
    });

    it('does not fire when rowsInserted is 0 — nothing new for the page to reload', () => {
      fixture = TestBed.createComponent(ReloadHost);
      fixture.detectChanges();

      fakeHub.emit('CatchUpCompleted', completion({ rowsInserted: 0 }));
      fixture.detectChanges();

      expect(fixture.componentInstance.onCycleCompleted).not.toHaveBeenCalled();
    });

    it('skips the first observation of a marker that was already set before this page mounted', () => {
      // Simulates: rows landed while some OTHER page was mounted (or before
      // any page in this session watched at all), then the user navigates to
      // this one — its own initial httpResource fetch already has the landed
      // data, so observing the already-set marker for the first time here
      // must not reload a second time.
      const bootstrap = TestBed.createComponent(ReloadHost);
      bootstrap.detectChanges();
      fakeHub.emit('CatchUpCompleted', completion({ rowsInserted: 5 }));
      bootstrap.detectChanges();
      expect(bootstrap.componentInstance.onCycleCompleted).toHaveBeenCalledTimes(1);

      fixture = TestBed.createComponent(ReloadHost);
      fixture.detectChanges();

      expect(fixture.componentInstance.onCycleCompleted).not.toHaveBeenCalled();

      // But a GENUINELY NEW landing that happens after this second page
      // mounted is not "first observation" for it and must still reload —
      // the skip only ever covers the value that was already there at mount.
      fakeHub.emit(
        'CatchUpCompleted',
        completion({ rowsInserted: 3, completedAt: '2026-09-14T10:05:00Z' }),
      );
      fixture.detectChanges();

      expect(fixture.componentInstance.onCycleCompleted).toHaveBeenCalledTimes(1);
    });

    it('fires on the very first catch-up landing of a session — the null baseline is not itself a trigger, but the first CHANGE from it must not be swallowed', () => {
      // Regression guard: reusing `watchLastRefreshedAt`'s null-baseline
      // mechanism here would misread this as a "first observation" and skip
      // it — see `watchCatchUpLanding`'s header comment.
      fixture = TestBed.createComponent(ReloadHost);
      fixture.detectChanges();

      fakeHub.emit('CatchUpCompleted', completion({ rowsInserted: 5 }));
      fixture.detectChanges();

      expect(fixture.componentInstance.onCycleCompleted).toHaveBeenCalledTimes(1);
    });

    it('fires again for a second, later landing after the first was already observed', () => {
      fixture = TestBed.createComponent(ReloadHost);
      fixture.detectChanges();

      fakeHub.emit('CatchUpCompleted', completion({ rowsInserted: 5, completedAt: '2026-09-14T10:01:00Z' }));
      fixture.detectChanges();
      fakeHub.emit(
        'CatchUpCompleted',
        completion({ kind: 'Dividends', rowsInserted: 2, completedAt: '2026-09-14T10:02:00Z' }),
      );
      fixture.detectChanges();

      expect(fixture.componentInstance.onCycleCompleted).toHaveBeenCalledTimes(2);
    });
  });
});
