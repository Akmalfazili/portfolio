import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { DestroyRef, InjectionToken, Injectable, computed, inject, signal } from '@angular/core';
import * as signalR from '@microsoft/signalr';

import { API_ROUTES, PRICES_HUB_URL } from '../api/api-routes';
import {
  PriceRefreshCycleResult,
  PriceRefreshStatus,
  QuoteUpdateNotification,
  RefreshCooldownProblemDetails,
} from '../api/models';

export type PriceConnectionState = 'connecting' | 'connected' | 'reconnecting' | 'polling-fallback';

/** The subset of `signalR.HubConnection` PriceStore actually uses — narrow on purpose so tests can fake it. */
export interface PricesHubConnection {
  on(methodName: string, callback: (...args: unknown[]) => void): void;
  onreconnecting(callback: (error?: Error) => void): void;
  onreconnected(callback: (connectionId?: string) => void): void;
  onclose(callback: (error?: Error) => void): void;
  start(): Promise<void>;
  stop(): Promise<void>;
}

/**
 * Factory seam for the hub connection — the default builds a real SignalR
 * connection to /hubs/prices; tests override this token with a fake so the
 * reconnect/degraded-mode state machine can be exercised without a live hub.
 */
export const PRICES_HUB_CONNECTION_FACTORY = new InjectionToken<() => PricesHubConnection>(
  'PRICES_HUB_CONNECTION_FACTORY',
  {
    providedIn: 'root',
    factory: () => () =>
      new signalR.HubConnectionBuilder()
        .withUrl(PRICES_HUB_URL)
        .withAutomaticReconnect([0, 2000, 5000, 10_000, 20_000, 30_000])
        .configureLogging(signalR.LogLevel.Warning)
        .build(),
  },
);

const POLL_FALLBACK_INTERVAL_MS = 60_000;
/** How often we retry re-establishing the push connection while degraded. */
const HUB_RETRY_INTERVAL_MS = 60_000;

/**
 * Single source of truth for live prices and refresh status. Every view reads
 * from this store — nobody else opens a SignalR connection or polls the API.
 *
 * The hub at /hubs/prices is push-only: we never invoke a method on it, only
 * listen for "QuoteUpdated" and "RefreshStatus" (the latter pushed once on
 * connect, so a fresh tab isn't blank until the first tick).
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
  private readonly http = inject(HttpClient);
  private readonly destroyRef = inject(DestroyRef);
  private readonly createConnection = inject(PRICES_HUB_CONNECTION_FACTORY);

  private readonly _prices = signal<ReadonlyMap<number, QuoteUpdateNotification>>(new Map());
  private readonly _status = signal<PriceRefreshStatus | null>(null);
  private readonly _connectionState = signal<PriceConnectionState>('connecting');
  private readonly _cooldownSecondsRemaining = signal<number | null>(null);
  private readonly _refreshing = signal(false);
  private readonly _lastRefreshResult = signal<PriceRefreshCycleResult | null>(null);
  private readonly _lastError = signal<string | null>(null);

  /** All known live prices, keyed by assetId. */
  readonly prices = this._prices.asReadonly();
  /** Last snapshot pushed/polled from GET/POST/hub "RefreshStatus". */
  readonly status = this._status.asReadonly();
  readonly connectionState = this._connectionState.asReadonly();
  /** Non-null while a 429 cooldown is counting down; drives the disabled button state. */
  readonly cooldownSecondsRemaining = this._cooldownSecondsRemaining.asReadonly();
  readonly refreshing = this._refreshing.asReadonly();
  /** D5 messaging source — describeRefreshOutcome() turns this into UI copy. */
  readonly lastRefreshResult = this._lastRefreshResult.asReadonly();
  readonly lastError = this._lastError.asReadonly();

  readonly lastRefreshedAt = computed(() => this._status()?.lastRefreshedAt ?? null);
  readonly isDegraded = computed(() => this._connectionState() === 'polling-fallback');

  private hubConnection: PricesHubConnection | null = null;
  private pollTimer: ReturnType<typeof setInterval> | null = null;
  private hubRetryTimer: ReturnType<typeof setInterval> | null = null;
  private cooldownTimer: ReturnType<typeof setInterval> | null = null;

  private readonly onVisibilityChange = () => {
    if (document.visibilityState !== 'visible') {
      return;
    }
    // A status missed while the tab was throttled/hidden should apply
    // immediately on focus rather than waiting up to 60s for the next poll —
    // GET /api/prices/status is explicitly the safe thing to poll (CLAUDE.md:
    // it can never cost a provider credit).
    this.fetchStatus();
    // If the socket died while the tab was frozen, don't wait out the rest of
    // HUB_RETRY_INTERVAL_MS — try to reconnect right away.
    if (this._connectionState() === 'polling-fallback') {
      this.startConnection();
    }
  };

  constructor() {
    this.connect();
    document.addEventListener('visibilitychange', this.onVisibilityChange);
    this.destroyRef.onDestroy(() => this.teardown());
  }

  priceFor(assetId: number): QuoteUpdateNotification | undefined {
    return this._prices().get(assetId);
  }

  /** POST /api/prices/refresh. Handles the 429 cooldown by counting down, not by showing an error toast. */
  refreshNow(): void {
    if (this._refreshing() || this._cooldownSecondsRemaining() !== null) {
      return;
    }
    this._refreshing.set(true);
    this._lastError.set(null);

    this.http.post<PriceRefreshCycleResult>(API_ROUTES.pricesRefresh, {}).subscribe({
      next: (result) => {
        this._refreshing.set(false);
        this._lastRefreshResult.set(result);
        this.fetchStatus();
      },
      error: (error: unknown) => {
        this._refreshing.set(false);
        if (error instanceof HttpErrorResponse && error.status === 429) {
          const problem = error.error as RefreshCooldownProblemDetails | undefined;
          this.startCooldown(problem?.secondsRemaining ?? 30);
        } else {
          this._lastError.set('Manual refresh failed — try again shortly.');
        }
      },
    });
  }

  private connect(): void {
    this.hubConnection = this.createConnection();

    this.hubConnection.on('QuoteUpdated', (...args: unknown[]) =>
      this.applyQuote(args[0] as QuoteUpdateNotification),
    );
    this.hubConnection.on('RefreshStatus', (...args: unknown[]) => this._status.set(args[0] as PriceRefreshStatus));

    this.hubConnection.onreconnecting(() => this._connectionState.set('reconnecting'));
    this.hubConnection.onreconnected(() => {
      this._connectionState.set('connected');
      this.stopPollFallback();
    });
    this.hubConnection.onclose(() => this.enterDegradedMode());

    this.startConnection();
  }

  private startConnection(): void {
    this.hubConnection
      ?.start()
      .then(() => {
        this._connectionState.set('connected');
        this.stopPollFallback();
      })
      .catch(() => this.enterDegradedMode());
  }

  private enterDegradedMode(): void {
    this._connectionState.set('polling-fallback');
    this.startPollFallback();
    this.scheduleHubRetry();
  }

  /** Background retry of the push connection while degraded — withAutomaticReconnect only
   *  covers a drop mid-connection, not the case where it has given up entirely or the
   *  initial handshake never succeeded, so this loop is what actually recovers. */
  private scheduleHubRetry(): void {
    if (this.hubRetryTimer) {
      return;
    }
    this.hubRetryTimer = setInterval(() => {
      if (this._connectionState() !== 'polling-fallback') {
        return;
      }
      this.startConnection();
    }, HUB_RETRY_INTERVAL_MS);
  }

  private startPollFallback(): void {
    if (this.pollTimer) {
      return;
    }
    this.fetchStatus();
    this.pollTimer = setInterval(() => this.fetchStatus(), POLL_FALLBACK_INTERVAL_MS);
  }

  private stopPollFallback(): void {
    if (this.pollTimer) {
      clearInterval(this.pollTimer);
      this.pollTimer = null;
    }
    if (this.hubRetryTimer) {
      clearInterval(this.hubRetryTimer);
      this.hubRetryTimer = null;
    }
  }

  private fetchStatus(): void {
    this.http.get<PriceRefreshStatus>(API_ROUTES.pricesStatus).subscribe({
      next: (status) => this._status.set(status),
      error: () => {
        // Keep the last known status rather than blanking the indicator on a transient failure.
      },
    });
  }

  private applyQuote(payload: QuoteUpdateNotification): void {
    const next = new Map(this._prices());
    next.set(payload.assetId, payload);
    this._prices.set(next);
  }

  private startCooldown(seconds: number): void {
    this._cooldownSecondsRemaining.set(seconds);
    if (this.cooldownTimer) {
      clearInterval(this.cooldownTimer);
    }
    this.cooldownTimer = setInterval(() => {
      const remaining = this._cooldownSecondsRemaining();
      if (remaining === null || remaining <= 1) {
        this._cooldownSecondsRemaining.set(null);
        if (this.cooldownTimer) {
          clearInterval(this.cooldownTimer);
          this.cooldownTimer = null;
        }
      } else {
        this._cooldownSecondsRemaining.set(remaining - 1);
      }
    }, 1000);
  }

  private teardown(): void {
    document.removeEventListener('visibilitychange', this.onVisibilityChange);
    this.stopPollFallback();
    if (this.cooldownTimer) {
      clearInterval(this.cooldownTimer);
    }
    void this.hubConnection?.stop();
  }
}
