import { Signal, signal } from '@angular/core';

/**
 * F4 — extracted from `PriceStore`'s cooldown decrement loop
 * (`price-store.ts:232-249` at the time of extraction), which had nothing to
 * do with prices, the hub, or polling — a plain one-second countdown that
 * happened to live inside the store because that was the easiest place to
 * put it. `createTableState` is the precedent for this shape: a factory
 * function returning signals plus the handful of methods that mutate them,
 * no DI, no injection context required, so it can be constructed as a plain
 * field initializer wherever a countdown is needed (`PriceStore`'s 30-second
 * manual-refresh cooldown today; nothing else in this codebase needs one yet).
 *
 * This is deliberately *not* the F4 hub/store split — `PriceStore` still owns
 * the hub lifecycle, poll fallback, and hub retry timers exactly as before.
 * See F4 in frontend-solid.md for what remains open.
 */
export interface Countdown {
  /** Seconds left, or `null` when no countdown is running. */
  readonly secondsRemaining: Signal<number | null>;
  /** Starts (or restarts) a countdown from `seconds`, ticking down once a
   *  second until it reaches `null`. */
  start(seconds: number): void;
  /** Cancels a running countdown, if any. Mirrors the original
   *  `PriceStore.teardown()` behaviour of only clearing the interval
   *  handle — it does not reset `secondsRemaining`, since the only caller
   *  (component/store teardown) never reads it again afterwards. Idempotent. */
  stop(): void;
}

export function createCountdown(): Countdown {
  const secondsRemaining = signal<number | null>(null);
  let timer: ReturnType<typeof setInterval> | null = null;

  function clearTimer(): void {
    if (timer) {
      clearInterval(timer);
      timer = null;
    }
  }

  return {
    secondsRemaining: secondsRemaining.asReadonly(),
    start(seconds: number): void {
      secondsRemaining.set(seconds);
      clearTimer();
      timer = setInterval(() => {
        const remaining = secondsRemaining();
        if (remaining === null || remaining <= 1) {
          secondsRemaining.set(null);
          clearTimer();
        } else {
          secondsRemaining.set(remaining - 1);
        }
      }, 1000);
    },
    stop(): void {
      clearTimer();
    },
  };
}
