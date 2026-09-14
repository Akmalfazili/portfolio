import { HttpErrorResponse } from '@angular/common/http';
import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';

import { PricesApi } from '../api/prices.api';
import {
  CatchUpCompletedNotification,
  CatchUpKind,
  CatchUpLeg,
  PriceRefreshCycleResult,
  PriceRefreshStatus,
  QuoteUpdateNotification,
  RefreshCatchUpPlan,
  RefreshCooldownProblemDetails,
} from '../api/models';
import { createCountdown } from './countdown';
import {
  PRICES_HUB_CONNECTION_FACTORY,
  PriceConnectionState,
  PricesHub,
  PricesHubConnection,
} from './prices-hub';

/** See `_catchUpCompletionsByRunId`'s comment — a small bounded set, not an
 *  attempt at unbounded history. */
const MAX_TRACKED_RUN_IDS = 10;

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
  /**
   * 2026-09-14 catch-up feature, id-matched (not timestamp-matched — see
   * `isLegInFlight`'s header comment for the race that ruled a clock
   * comparison out).
   */
  private readonly _catchUpPlan = signal<RefreshCatchUpPlan | null>(null);
  /**
   * Every recently-seen completion, keyed by its OWN `runId` — this is what
   * makes the id match order-independent. The backend starts the detached
   * catch-up task before it sends the `POST /api/prices/refresh` response,
   * so a fast leg (one Yahoo dividend call, ~300ms) can finish and push its
   * `CatchUpCompleted` over SignalR before `refreshNow()`'s own `next`
   * handler has even run — i.e. the completion can arrive BEFORE the plan
   * that queued it. Recording every completion here (not just ones that
   * currently match a known plan) means that when the plan lands moments
   * later, `isLegInFlight`/the panel line find the match immediately instead
   * of reading "still fetching" forever for a leg that, in reality, already
   * finished. Bounded to `MAX_TRACKED_RUN_IDS` entries (oldest evicted first)
   * — only ever a couple of runs are relevant at once, so this is generous
   * headroom, not an attempt at unbounded history.
   */
  private readonly _catchUpCompletionsByRunId = signal<
    ReadonlyMap<string, CatchUpCompletedNotification>
  >(new Map());
  /**
   * A composite `"<kind>:<completedAt>"` marker, set only when a
   * `CatchUpCompleted` lands with `rowsInserted > 0` — this is what
   * `reloadOnRefreshCycle` watches (alongside `lastRefreshedAt`) to reload a
   * page's price-history/dividend resources as soon as new rows exist,
   * rather than waiting for the next unrelated live-quote cycle. Two
   * simultaneous completions (both legs landing in the same tick) can
   * collapse into a single marker change — harmless, since a reload re-fetches
   * everything either way.
   */
  private readonly _catchUpLandingMarker = signal<string | null>(null);
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

  /** The catch-up plan from the LAST manual refresh's response — `null` before
   *  any manual refresh, or once a manual refresh with no `catchUp` field
   *  (an older server) has been the most recent one. */
  readonly catchUpPlan = this._catchUpPlan.asReadonly();
  /**
   * The completion whose `runId` matches the CURRENT plan's price-history
   * leg — `null` for every other case: no plan yet, a `NothingToFetch` /
   * `AlreadyRunning` leg (both always carry `runId: null`), or a `Queued`
   * leg whose own completion hasn't arrived yet. Deliberately NOT "the
   * latest completion seen for this kind" — an earlier version fell back to
   * that, which is exactly the bug this replaced: click 1 queued both legs
   * and finished ("Fetched price history for 7 stocks."); click 2 returned
   * `NothingToFetch` for both (nothing was due), but the fallback kept
   * showing click 1's completion, stating a PREVIOUS click's outcome as the
   * CURRENT one's. The click's own toast (`describeRefreshOutcome`) already
   * says "already up to date" for that case — this signal has nothing to add
   * and must say nothing, not repeat stale news. See `completionForKind`.
   */
  readonly lastPriceHistoryCatchUpCompletion = computed(() =>
    this.completionForKind('PriceHistory'),
  );
  readonly lastDividendsCatchUpCompletion = computed(() => this.completionForKind('Dividends'));
  /** True from a `Queued` price-history leg until ITS OWN matching
   *  `CatchUpCompleted` arrives — see `isLegInFlight`'s comment for the id
   *  match this relies on and the race it closes. */
  readonly priceHistoryCatchUpInFlight = computed(() => this.isLegInFlight('PriceHistory'));
  readonly dividendsCatchUpInFlight = computed(() => this.isLegInFlight('Dividends'));
  /** Watched by `reloadOnRefreshCycle` alongside `lastRefreshedAt` — see its
   *  own header comment. Not otherwise meant to be read directly. */
  readonly catchUpLandingMarker = this._catchUpLandingMarker.asReadonly();

  constructor() {
    this.hub.connect({
      onQuote: (payload) => this.applyQuote(payload),
      onStatus: (status) => this._status.set(status),
      onConnectionStateChange: (state) => this._connectionState.set(state),
      onCatchUpCompleted: (notification) => this.applyCatchUpCompletion(notification),
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
        // catchUp is absent/null on an older server or a non-manual cycle —
        // both must clear any previous plan, not leave a stale one behind
        // for `catchUpPlan`/the in-flight signals to keep describing. No
        // timestamp bookkeeping needed here any more — matching now happens
        // by `runId`, recorded as each `CatchUpCompleted` arrives regardless
        // of whether this plan has landed yet (see `isLegInFlight`).
        this._catchUpPlan.set(result.catchUp ?? null);
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

  private applyCatchUpCompletion(notification: CatchUpCompletedNotification): void {
    // Recorded by runId regardless of whether a matching plan has arrived
    // yet — see `_catchUpCompletionsByRunId`'s header comment for the race
    // this closes (the completion can beat its own plan client-side).
    const byRunId = new Map(this._catchUpCompletionsByRunId());
    byRunId.set(notification.runId, notification);
    if (byRunId.size > MAX_TRACKED_RUN_IDS) {
      // Map iteration order is insertion order — the first key is the oldest.
      const oldestRunId = byRunId.keys().next().value;
      if (oldestRunId !== undefined) {
        byRunId.delete(oldestRunId);
      }
    }
    this._catchUpCompletionsByRunId.set(byRunId);

    if (notification.rowsInserted > 0) {
      this._catchUpLandingMarker.set(`${notification.kind}:${notification.completedAt}`);
    }
  }

  /**
   * True only between a `Queued` leg (with a `runId`) and ITS OWN completion
   * arriving — matched by id, not by timestamp.
   *
   * A clock comparison was tried first and is wrong: the backend starts the
   * detached catch-up task BEFORE it sends the `POST /api/prices/refresh`
   * response, so a fast leg (one Yahoo dividend call, ~300ms) can finish and
   * push its `CatchUpCompleted` before `refreshNow()`'s own `next` handler
   * has even run. That completion's `completedAt` then reads EARLIER than
   * whenever the plan was received client-side, so a "was this completion
   * timestamped after the plan arrived" check misreads a leg that already
   * finished as a stale straggler from some earlier click — and because
   * nothing ever re-queues a `NothingToFetch`/`AlreadyRunning` leg to produce
   * a second, later-timestamped completion, that misreading never resolves:
   * the panel reads "Fetching…" forever. That is the wrong direction to err
   * in — it is a false claim, not an honestly-stale one.
   *
   * The fix: `_catchUpCompletionsByRunId` records every completion AS IT
   * ARRIVES, before this method ever runs, keyed by its own id — so whichever
   * of {plan, completion} lands first, the match is found the moment both are
   * known, with no ordering assumption at all.
   */
  private isLegInFlight(kind: CatchUpKind): boolean {
    const plan = this._catchUpPlan();
    const leg = plan ? this.legFor(plan, kind) : null;
    if (!leg || leg.state !== 'Queued' || leg.runId == null) {
      return false;
    }
    return !this._catchUpCompletionsByRunId().has(leg.runId);
  }

  /**
   * The completion to show for this kind's panel line: the one whose
   * `runId` matches the CURRENT plan's leg — `null` if that leg has no
   * `runId` (`NothingToFetch`/`AlreadyRunning`, or no plan at all) or its
   * matching completion hasn't arrived yet. Deliberately NO fallback to
   * "whatever completion was last seen for this kind" — see
   * `lastPriceHistoryCatchUpCompletion`'s doc comment for the bug that shape
   * caused (a stale completion from a PREVIOUS click outliving the click it
   * belonged to).
   */
  private completionForKind(kind: CatchUpKind): CatchUpCompletedNotification | null {
    const plan = this._catchUpPlan();
    const leg = plan ? this.legFor(plan, kind) : null;
    if (leg?.runId == null) {
      return null;
    }
    return this._catchUpCompletionsByRunId().get(leg.runId) ?? null;
  }

  private legFor(plan: RefreshCatchUpPlan, kind: CatchUpKind): CatchUpLeg {
    return kind === 'PriceHistory' ? plan.priceHistory : plan.dividends;
  }

  private teardown(): void {
    this.cooldown.stop();
    this.hub.dispose();
  }
}
