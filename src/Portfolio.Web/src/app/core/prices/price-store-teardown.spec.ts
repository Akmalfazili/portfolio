import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { PRICES_HUB_CONNECTION_FACTORY, PriceStore } from './price-store';
import { FakeHubConnection } from './testing/fake-hub-connection';

/**
 * Regression test for the F4 teardown defect (frontend-solid.md): in
 * `@microsoft/signalr` 10.0.0, a user-initiated `stop()` on a *connected* hub
 * fires every registered `onclose` callback, asynchronously
 * (`HubConnection.js`: `stop()` → `_stopInternal` → `connection.stop()` →
 * `_connectionClosed` → `_completeClose` → `_closedCallbacks.forEach`, guarded
 * only by `_connectionStarted`). `PriceStore.teardown()` calls
 * `hub.dispose()`, which itself calls `stop()` on the hub it is closing — so
 * before `PricesHub`'s `disposed` guard existed, that `onclose` re-entered
 * `enterDegradedMode()`, which re-armed the poll-fallback and hub-retry
 * timers and eventually called `start()` again from a store that no longer
 * exists.
 *
 * Kept in its own file rather than added to `price-store.spec.ts`: that
 * file's import of `PRICES_HUB_CONNECTION_FACTORY` and `PriceStore` from
 * `./price-store`, unmodified, is itself the regression evidence this step
 * was told to preserve (frontend-solid.md, F4 — the same "unmodified specs as
 * regression evidence" precedent recorded under F1).
 */
describe('PriceStore teardown', () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  it('does not re-enter degraded mode, re-arm timers, or reopen the hub once the store is destroyed', async () => {
    vi.useFakeTimers();

    const fakeConnection = new FakeHubConnection();
    fakeConnection.startResult = 'resolve';

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: PRICES_HUB_CONNECTION_FACTORY, useValue: () => fakeConnection },
      ],
    });

    const httpMock = TestBed.inject(HttpTestingController);
    TestBed.inject(PriceStore);

    // Let the fake's start() resolve so the hub is genuinely connected —
    // the defect only reproduces from a *connected* hub (see the class doc
    // comment on PricesHub: `_completeClose`'s callbacks fire only when
    // `_connectionStarted` is true).
    await Promise.resolve();
    await Promise.resolve();

    const startSpy = vi.spyOn(fakeConnection, 'start');

    // Destroys the environment injector, firing PriceStore's
    // `DestroyRef.onDestroy` hook synchronously.
    TestBed.resetTestingModule();

    // Flush whatever microtask the fake's stop()/oncloseCb touches, then
    // advance well past both the 60s poll-fallback and 60s hub-retry
    // intervals — long enough for either to have fired at least twice.
    await Promise.resolve();
    await Promise.resolve();
    vi.advanceTimersByTime(150_000);

    // This is the assertion that catches the defect. The verify() below is
    // hygiene only: measured with the guard reverted and this line removed,
    // the test still passes — a poll re-armed after resetTestingModule()
    // never reaches this HttpTestingController. prices-hub.spec.ts is where
    // the orphaned GET /api/prices/status is observable.
    expect(startSpy).not.toHaveBeenCalled();
    httpMock.verify();
  });
});
