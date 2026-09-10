import { HttpErrorResponse } from '@angular/common/http';
import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';

import { PricesApi } from '../api/prices.api';
import {
  PriceRefreshCycleResult,
  PriceRefreshStatus,
  QuoteUpdateNotification,
  RefreshCooldownProblemDetails,
} from '../api/models';
import { createCountdown } from './countdown';
import {
  PRICES_HUB_CONNECTION_FACTORY,
  PriceConnectionState,
  PricesHub,
  PricesHubConnection,
} from './prices-hub';

// F4 — the hub lifecycle (connect state machine, poll fallback, hub retry,
// visibilitychange catch-up) moved to `prices-hub.ts`. Re-exported so every
// existing import site — `PRICES_HUB_CONNECTION_FACTORY` and `PriceStore`
// together are imported `from './price-store'` by seven spec files plus
// `testing/fake-hub-connection.ts` — keeps working unmodified. `price-store.ts`
// stays the one public seam consumers reach for.
export { PRICES_HUB_CONNECTION_FACTORY };
export type { PriceConnectionState, PricesHubConnection };

/**
 * Single source of truth for live prices and refresh status. Every view reads
 * from this store — nobody else opens a SignalR connection or polls the API.
 *
 * The hub at /hubs/prices is push-only: we never invoke a method on it, only
 * listen for "QuoteUpdated" and "RefreshStatus" (the latter pushed once on
 * connect, so a fresh tab isn't blank until the first tick). `PricesHub`
 * (`prices-hub.ts`) owns that transport; this store owns the quote cache, the
 * status snapshot, the connection-state signal and the manual-refresh
 * command, and is the only thing every view reads from.
 *
 * Degraded mode: the current REST surface (see core/api/models.ts) has no
 * "current quotes" endpoint — prices only ever arrive over the hub. So when
 * the socket is genuinely down, the 60s HTTP fallback keeps `status` (and so
 * the toolbar's timestamp / market-open / stale state) alive by polling
 * GET /api/prices/status, while `connectionState` surfaces 'polling-fallback'
 * so the UI can say live prices themselves are not updating rather than
 * silently going quiet.
 */
@Injectable({ providedIn: 'root' })
export class PriceStore {
  private readonly pricesApi = inject(PricesApi);
  private readonly destroyRef = inject(DestroyRef);
  private readonly createConnection = inject(PRICES_HUB_CONNECTION_FACTORY);

  private readonly _prices = signal<ReadonlyMap<number, QuoteUpdateNotification>>(new Map());
  private readonly _status = signal<PriceRefreshStatus | null>(null);
  private readonly _connectionState = signal<PriceConnectionState>('connecting');
  /** F4 — the 30-second manual-refresh cooldown, extracted into a
   *  self-contained factory (`countdown.ts`) with nothing to do with prices. */
  private readonly cooldown = createCountdown();
  private readonly _refreshing = signal(false);
  private readonly _lastRefreshResult = signal<PriceRefreshCycleResult | null>(null);
  private readonly _lastError = signal<string | null>(null);
  /** F4 — the SignalR transport, extracted into `PricesHub`. This store hands
   *  it sink callbacks that write straight into the signals above; it never
   *  holds a copy of anything the hub reports. */
  private readonly hub = new PricesHub(this.pricesApi, this.createConnection);

  /** All known live prices, keyed by assetId. */
  readonly prices = this._prices.asReadonly();
  /** Last snapshot pushed/polled from GET/POST/hub "RefreshStatus". */
  readonly status = this._status.asReadonly();
  readonly connectionState = this._connectionState.asReadonly();
  /** Non-null while a 429 cooldown is counting down; drives the disabled button state. */
  readonly cooldownSecondsRemaining = this.cooldown.secondsRemaining;
  readonly refreshing = this._refreshing.asReadonly();
  /** D5 messaging source — describeRefreshOutcome() turns this into UI copy. */
  readonly lastRefreshResult = this._lastRefreshResult.asReadonly();
  readonly lastError = this._lastError.asReadonly();

  readonly lastRefreshedAt = computed(() => this._status()?.lastRefreshedAt ?? null);
  readonly isDegraded = computed(() => this._connectionState() === 'polling-fallback');

  constructor() {
    this.hub.connect({
      onQuote: (payload) => this.applyQuote(payload),
      onStatus: (status) => this._status.set(status),
      onConnectionStateChange: (state) => this._connectionState.set(state),
    });
    this.destroyRef.onDestroy(() => this.teardown());
  }

  priceFor(assetId: number): QuoteUpdateNotification | undefined {
    return this._prices().get(assetId);
  }

  /** POST /api/prices/refresh. Handles the 429 cooldown by counting down, not by showing an error toast. */
  refreshNow(): void {
    if (this._refreshing() || this.cooldown.secondsRemaining() !== null) {
      return;
    }
    this._refreshing.set(true);
    this._lastError.set(null);

    this.pricesApi.refresh().subscribe({
      next: (result) => {
        this._refreshing.set(false);
        this._lastRefreshResult.set(result);
        this.hub.refreshStatusNow();
      },
      error: (error: unknown) => {
        this._refreshing.set(false);
        if (error instanceof HttpErrorResponse && error.status === 429) {
          const problem = error.error as RefreshCooldownProblemDetails | undefined;
          this.cooldown.start(problem?.secondsRemaining ?? 30);
        } else {
          this._lastError.set('Manual refresh failed — try again shortly.');
        }
      },
    });
  }

  private applyQuote(payload: QuoteUpdateNotification): void {
    const next = new Map(this._prices());
    next.set(payload.assetId, payload);
    this._prices.set(next);
  }

  private teardown(): void {
    this.cooldown.stop();
    this.hub.dispose();
  }
}
