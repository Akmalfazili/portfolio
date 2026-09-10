import { PricesHubConnection } from '../prices-hub';

/**
 * Minimal fake for `PricesHubConnection`, for use with
 * `{ provide: PRICES_HUB_CONNECTION_FACTORY, useValue: () => new FakeHubConnection() }`
 * in any test that constructs `PriceStore`/`PricesHub` (directly or via a
 * component that injects it) — keeps tests from opening a real SignalR
 * connection.
 *
 * `stop()` fires `oncloseCb` when (and only when) the fake is currently
 * connected — mirroring real `@microsoft/signalr`'s own contract:
 * `HubConnection._completeClose` invokes every registered `onclose` callback
 * whenever `_connectionStarted` is true, which is exactly the case a
 * user-initiated `stop()` on a connected hub satisfies. The earlier version
 * of this fake never invoked `oncloseCb` from `stop()` at all, which is why a
 * real `PriceStore`/`PricesHub` teardown defect — `dispose()`'s own `stop()`
 * call reviving the connection it was meant to close — was invisible to
 * every test that used it: the fake simply didn't exercise the code path the
 * bug lived in. Not fired when `start()` never resolved, or `stop()` has
 * already been called since the last successful `start()`.
 */
export class FakeHubConnection implements PricesHubConnection {
  handlers = new Map<string, (...args: unknown[]) => void>();
  onreconnectingCb: ((error?: Error) => void) | null = null;
  onreconnectedCb: ((connectionId?: string) => void) | null = null;
  oncloseCb: ((error?: Error) => void) | null = null;
  startResult: 'resolve' | 'reject' = 'resolve';
  stopped = false;
  /** True once a `start()` call has resolved and `stop()` hasn't run since. */
  private connected = false;

  on(methodName: string, callback: (...args: unknown[]) => void): void {
    this.handlers.set(methodName, callback);
  }
  onreconnecting(callback: (error?: Error) => void): void {
    this.onreconnectingCb = callback;
  }
  onreconnected(callback: (connectionId?: string) => void): void {
    this.onreconnectedCb = callback;
  }
  onclose(callback: (error?: Error) => void): void {
    this.oncloseCb = callback;
  }
  start(): Promise<void> {
    if (this.startResult !== 'resolve') {
      return Promise.reject(new Error('down'));
    }
    this.connected = true;
    return Promise.resolve();
  }
  stop(): Promise<void> {
    const wasConnected = this.connected;
    this.connected = false;
    this.stopped = true;
    if (wasConnected) {
      this.oncloseCb?.();
    }
    return Promise.resolve();
  }

  emit(event: string, payload: unknown): void {
    this.handlers.get(event)?.(payload);
  }
}
