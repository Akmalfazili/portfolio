import { PricesHubConnection } from '../price-store';

/**
 * Minimal fake for `PricesHubConnection`, for use with
 * `{ provide: PRICES_HUB_CONNECTION_FACTORY, useValue: () => new FakeHubConnection() }`
 * in any test that constructs `PriceStore` (directly or via a component that
 * injects it) — keeps tests from opening a real SignalR connection.
 */
export class FakeHubConnection implements PricesHubConnection {
  handlers = new Map<string, (...args: unknown[]) => void>();
  onreconnectingCb: ((error?: Error) => void) | null = null;
  onreconnectedCb: ((connectionId?: string) => void) | null = null;
  oncloseCb: ((error?: Error) => void) | null = null;
  startResult: 'resolve' | 'reject' = 'resolve';
  stopped = false;

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
    return this.startResult === 'resolve' ? Promise.resolve() : Promise.reject(new Error('down'));
  }
  stop(): Promise<void> {
    this.stopped = true;
    return Promise.resolve();
  }

  emit(event: string, payload: unknown): void {
    this.handlers.get(event)?.(payload);
  }
}
