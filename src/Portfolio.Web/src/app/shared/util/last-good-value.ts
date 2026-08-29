import { Signal, linkedSignal } from '@angular/core';

/** The subset of `httpResource`'s API this helper actually reads. */
export interface ValueBearingResource<T> {
  hasValue(): boolean;
  value(): T;
}

/**
 * Carries forward the last value a resource successfully resolved to,
 * surviving a subsequent failed reload.
 *
 * `httpResource.reload()` behaves asymmetrically: a reload that is merely
 * *in flight* preserves the previous value while `isLoading()` flips back to
 * `true` (`_resource-chunk.mjs`'s `state` linkedSignal carries the prior
 * `stream` forward when the request is unchanged) — but a reload that
 * *fails* does NOT preserve it. `loadEffect`'s `catch` block replaces the
 * resolved stream wholesale with `{ error }`, so `hasValue()`/`value()` both
 * go empty even though good data was on screen a moment ago.
 *
 * A background reload failing must not blow away content the user is
 * already looking at — the toolbar's refresh indicator is where refresh
 * health belongs (rule #3), not a full-page/full-panel error replacing
 * working data. Callers read the signal this returns to render and to gate
 * their error state, never the resource's own `.value()`/`.hasValue()`
 * directly, once a resource is wired through this helper.
 */
export function lastGoodValue<T>(resource: ValueBearingResource<T>): Signal<T | undefined> {
  return linkedSignal<T | undefined, T | undefined>({
    source: () => (resource.hasValue() ? resource.value() : undefined),
    computation: (latest, previous) => latest ?? previous?.value,
  });
}
