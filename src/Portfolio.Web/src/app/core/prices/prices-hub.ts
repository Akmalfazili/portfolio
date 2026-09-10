import { InjectionToken } from '@angular/core';
import * as signalR from '@microsoft/signalr';

import { PRICES_HUB_URL } from '../api/api-routes';
import { PricesApi } from '../api/prices.api';
import { PriceRefreshStatus, QuoteUpdateNotification } from '../api/models';

export type PriceConnectionState = 'connecting' | 'connected' | 'reconnecting' | 'polling-fallback';

/** The subset of `signalR.HubConnection` PricesHub actually uses — narrow on purpose so tests can fake it. */
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
 * Sink callbacks `PricesHub` delivers pushed/polled data through. `PriceStore`
 * is the only implementer and stays the single owner of `_prices`, `_status`
 * and `_connectionState` — this interface is how the hub hands those values
 * over without holding any of its own copies for consumers to read.
 */
export interface PricesHubSinks {
  onQuote(payload: QuoteUpdateNotification): void;
  onStatus(status: PriceRefreshStatus): void;
  onConnectionStateChange(state: PriceConnectionState): void;
}

/**
 * F4 — extracted from `PriceStore`, which carried this alongside the quote
 * cache and the manual-refresh command. Owns the SignalR transport
 * end-to-end: the connect/reconnecting/reconnected/close state machine, the
 * 60s poll fallback, the 60s hub retry loop, and the visibilitychange
 * catch-up. `PriceStore` still owns the quote cache, status snapshot and the
 * manual-refresh command (`refreshNow`, its 429 handling, `createCountdown()`)
 * — this class owns everything about *how* data arrives, nothing about what
 * is done with it.
 *
 * Composition, not inheritance: `PriceStore` constructs one of these as a
 * plain field and hands it sink callbacks via `connect()`. This is not a base
 * class `PriceStore` extends — see frontend-solid.md, "There are no base
 * components, and none should be added".
 *
 * Teardown defect (found in review, fixed here): in `@microsoft/signalr`
 * 10.0.0, a user-initiated `stop()` on a *connected* hub fires every
 * registered `onclose` callback via `_completeClose` (`HubConnection.js`:
 * `stop()` → `_stopInternal` → `connection.stop()` → `_connectionClosed` →
 * `_completeClose` → `_closedCallbacks.forEach`, guarded only by
 * `_connectionStarted`, which `dispose()` calling `stop()` on a connected hub
 * satisfies). It arrives *asynchronously* — `HttpConnection._stopInternal`
 * awaits the start promise before closing the transport — so it lands after
 * `dispose()` has returned, which is why the guard is a sticky flag rather
 * than anything scoped to the `stop()` call. Without the `disposed` guard
 * below, that `onclose` would call `enterDegradedMode()`, which re-arms the poll-fallback and
 * hub-retry timers and eventually calls `start()` again — reopening a live
 * SignalR connection from a store that no longer exists. `onclose` and the
 * `start().then()`/`.catch()` continuations — every place a callback can
 * fire *after* `dispose()` has already run — check `disposed` before doing
 * anything. The hub-retry tick needs no separate check: `dispose()` clears
 * both timers via `stopPollFallback()` before it can fire again, and the
 * only thing that could re-arm them post-dispose is the `onclose` path
 * above, which is already guarded.
 */
export class PricesHub {
  private hubConnection: PricesHubConnection | null = null;
  private pollTimer: ReturnType<typeof setInterval> | null = null;
  private hubRetryTimer: ReturnType<typeof setInterval> | null = null;
  private sinks: PricesHubSinks | null = null;
  /** Internal state used for the hub's own decisions (whether to (re)start
   *  polling, whether a retry tick should act). Reported out to the store
   *  through `sinks.onConnectionStateChange` on every transition. */
  private state: PriceConnectionState = 'connecting';
  /** Set once by `dispose()`. See the class doc comment for the defect this guards. */
  private disposed = false;

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
    if (this.state === 'polling-fallback') {
      this.startConnection();
    }
  };

  constructor(
    private readonly pricesApi: PricesApi,
    private readonly createConnection: () => PricesHubConnection,
  ) {}

  /**
   * Opens the hub connection and starts delivering data through `sinks`.
   * Must be called at most once per instance — `PriceStore` calls this from
   * its constructor, the same place the connection used to open.
   */
  connect(sinks: PricesHubSinks): void {
    this.sinks = sinks;
    document.addEventListener('visibilitychange', this.onVisibilityChange);

    this.hubConnection = this.createConnection();

    this.hubConnection.on('QuoteUpdated', (...args: unknown[]) =>
      this.sinks?.onQuote(args[0] as QuoteUpdateNotification),
    );
    this.hubConnection.on('RefreshStatus', (...args: unknown[]) =>
      this.sinks?.onStatus(args[0] as PriceRefreshStatus),
    );

    this.hubConnection.onreconnecting(() => this.setState('reconnecting'));
    this.hubConnection.onreconnected(() => {
      this.setState('connected');
      this.stopPollFallback();
    });
    this.hubConnection.onclose(() => {
      // See the class doc comment: a stop() dispose() itself triggered must
      // not revive the connection it was closing.
      if (this.disposed) {
        return;
      }
      this.enterDegradedMode();
    });

    this.startConnection();
  }

  /**
   * GET /api/prices/status once, forwarded through `sinks.onStatus`. Used by
   * `PriceStore.refreshNow()` to refresh the toolbar after a manual refresh
   * cycle completes — a one-shot fetch, not part of the poll-fallback loop.
   */
  refreshStatusNow(): void {
    this.fetchStatus();
  }

  /**
   * Tears the hub down: stops listening for tab visibility, clears both
   * timers, and closes the connection. Idempotent-safe to call once from
   * `PriceStore`'s `DestroyRef.onDestroy`. Sets `disposed` *before* calling
   * `hubConnection.stop()` so the `onclose` that call triggers sees the guard
   * already up, whenever it arrives (asynchronously for real SignalR,
   * synchronously for `FakeHubConnection`).
   */
  dispose(): void {
    document.removeEventListener('visibilitychange', this.onVisibilityChange);
    this.disposed = true;
    this.stopPollFallback();
    void this.hubConnection?.stop();
  }

  private setState(state: PriceConnectionState): void {
    this.state = state;
    this.sinks?.onConnectionStateChange(state);
  }

  private startConnection(): void {
    this.hubConnection
      ?.start()
      .then(() => {
        if (this.disposed) {
          return;
        }
        this.setState('connected');
        this.stopPollFallback();
      })
      .catch(() => {
        if (this.disposed) {
          return;
        }
        this.enterDegradedMode();
      });
  }

  private enterDegradedMode(): void {
    this.setState('polling-fallback');
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
      if (this.state !== 'polling-fallback') {
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
    this.pricesApi.status().subscribe({
      next: (status) => this.sinks?.onStatus(status),
      error: () => {
        // Keep the last known status rather than blanking the indicator on a transient failure.
      },
    });
  }
}
