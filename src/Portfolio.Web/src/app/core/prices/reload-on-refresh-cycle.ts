import { effect, inject, untracked } from '@angular/core';

import { PriceStore } from './price-store';

/**
 * Extracted from `PortfolioOverviewPage` and `AssetDetailPage`, which each
 * carried an identical constructor `effect()` — reacts to `PriceStore`
 * completing a refresh cycle, never polls independently (rule #2 in
 * CLAUDE.md). Must be called from an injection context (a component's field
 * initializer or constructor).
 *
 * Semantics, preserved exactly from the two call sites this replaced:
 * - reads `PriceStore.lastRefreshedAt()`
 * - ignores `null` (no cycle has ever completed)
 * - ignores an unchanged value (re-run of the effect with nothing new)
 * - skips the very first non-null observation, so mount doesn't double-fetch
 *   on top of the page's own initial `httpResource` requests
 * - only a genuine, subsequent cycle completion invokes `onCycleCompleted`,
 *   inside `untracked()` so the `reload()` calls it makes don't themselves
 *   register as dependencies of this effect
 */
export function reloadOnRefreshCycle(onCycleCompleted: () => void): void {
  const priceStore = inject(PriceStore);
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
