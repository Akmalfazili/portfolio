import { effect, inject, untracked } from '@angular/core';

import { PriceStore } from './price-store';

/**
 * Extracted from `PortfolioOverviewPage` and `AssetDetailPage`, which each
 * carried an identical constructor `effect()` — reacts to `PriceStore`
 * completing a refresh cycle, never polls independently (rule #2 in
 * CLAUDE.md). Must be called from an injection context (a component's field
 * initializer or constructor).
 *
 * Watches TWO independent triggers, both driving the same `onCycleCompleted`
 * callback — a page's own reload logic (`resource.reload()` calls, already
 * safe against D44's "reload must not blank the page" defect via
 * `resourceState`/`lastGoodValue`) doesn't need to know which one fired:
 *
 * 1. `PriceStore.lastRefreshedAt()` — a completed live-quote refresh cycle
 *    (scheduled or manual), the original trigger. Semantics preserved
 *    EXACTLY from the two original call sites this replaced — see
 *    `watchLastRefreshedAt` below.
 * 2. `PriceStore.catchUpLandingMarker()` (2026-09-14) — a background
 *    catch-up fetch (missing price history / missing dividends, queued by a
 *    manual refresh — see `PriceRefreshCycleResult.catchUp`) has landed NEW
 *    rows. This is what makes a newly-added stock's freshly-backfilled chart
 *    or a stock's freshly-backfilled dividend history show up without the
 *    user doing anything, independent of whether a live-quote cycle happens
 *    to complete around the same time. Deliberately a DIFFERENT skip rule
 *    from trigger 1 — see `watchCatchUpLanding`'s own comment for why reusing
 *    trigger 1's mechanism verbatim would silently swallow the very first
 *    catch-up landing of a session.
 */
export function reloadOnRefreshCycle(onCycleCompleted: () => void): void {
  const priceStore = inject(PriceStore);

  watchLastRefreshedAt(priceStore, onCycleCompleted);
  watchCatchUpLanding(priceStore, onCycleCompleted);
}

/**
 * Original mechanism, UNCHANGED. Semantics:
 * - reads `PriceStore.lastRefreshedAt()`
 * - ignores `null` (no cycle has ever completed)
 * - ignores an unchanged value (re-run of the effect with nothing new)
 * - skips the very first non-null observation, so mount doesn't double-fetch
 *   on top of the page's own initial `httpResource` requests — correct here
 *   because the hub always re-broadcasts `RefreshStatus` once on every
 *   connect (`prices-hub.ts`'s own doc comment), so the first non-null value
 *   any effect instance observes is reliably that connect-time snapshot, not
 *   evidence of a change since mount.
 * - only a genuine, subsequent cycle completion invokes `onCycleCompleted`,
 *   inside `untracked()` so the `reload()` calls it makes don't themselves
 *   register as dependencies of this effect
 */
function watchLastRefreshedAt(priceStore: PriceStore, onCycleCompleted: () => void): void {
  let lastAppliedRefreshAt: string | null = null;

  effect(() => {
    const at = priceStore.lastRefreshedAt();
    if (at === null || at === lastAppliedRefreshAt) {
      return;
    }
    const isFirstObservation = lastAppliedRefreshAt === null;
    lastAppliedRefreshAt = at;
    if (isFirstObservation) {
      return;
    }
    untracked(() => onCycleCompleted());
  });
}

/**
 * 2026-09-14. Deliberately NOT the same mechanism as `watchLastRefreshedAt`
 * above, even though the shape looks similar — the baseline here is whatever
 * `catchUpLandingMarker()` ALREADY reads at the moment this page starts
 * watching (captured eagerly, synchronously, in the injection context),
 * never `null`.
 *
 * Why: `CatchUpCompleted` has no connect-time replay — unlike `RefreshStatus`,
 * nothing re-broadcasts it just because a page mounted or the hub
 * reconnected, so the marker only ever changes when a background fetch
 * genuinely just landed new rows. If this used `watchLastRefreshedAt`'s
 * null-baseline instead, the very FIRST landing of a session — typically the
 * one the user is actively looking at this page waiting for, having just
 * clicked refresh — would be misread as "the first observation" and
 * silently skipped, which is the opposite of what this trigger exists for.
 *
 * A marker that was already at its current value when THIS page mounted
 * still isn't a trigger (that page's own initial `httpResource` fetch
 * already reflects whatever was true at that moment) — captured by seeding
 * `lastApplied` from `read()` up front instead of from `null`.
 */
function watchCatchUpLanding(priceStore: PriceStore, onCycleCompleted: () => void): void {
  let lastApplied: string | null = priceStore.catchUpLandingMarker();

  effect(() => {
    const marker = priceStore.catchUpLandingMarker();
    if (marker === null || marker === lastApplied) {
      return;
    }
    lastApplied = marker;
    untracked(() => onCycleCompleted());
  });
}
