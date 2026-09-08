import { Signal, computed } from '@angular/core';

import { ValueBearingResource, lastGoodValue } from './last-good-value';

/**
 * The subset of `httpResource`'s API this helper reads, on top of
 * `ValueBearingResource`'s `hasValue()`/`value()`. Narrow on purpose — the
 * same reason `lastGoodValue` depends on `ValueBearingResource` rather than
 * `HttpResourceRef`: a test can hand this a plain object of signals, and
 * nothing here is coupled to the HTTP transport.
 */
export interface StatefulResource<T> extends ValueBearingResource<T> {
  isLoading(): boolean;
  error(): unknown;
}

/** What every data-backed view actually needs from a resource. */
export interface ResourceState<T> {
  /** The last value that successfully resolved — survives a failed reload. */
  readonly value: Signal<T | undefined>;
  /** True only while loading with NOTHING previously on screen. */
  readonly isLoading: Signal<boolean>;
  /** True only when a failure happened and NOTHING has ever loaded. */
  readonly hasError: Signal<boolean>;
}

/**
 * The one place the "a reload must not blank working content" rule is
 * encoded. It was previously spelled out by hand at eight call sites as
 *
 * ```ts
 * private readonly xCache = lastGoodValue(this.xResource);
 * readonly isLoading = computed(() => this.xResource.isLoading() && this.xCache() === undefined);
 * readonly hasError  = computed(() => this.xResource.error() != null && this.xCache() === undefined);
 * ```
 *
 * — and a ninth site (`asset-detail.page.ts`'s `performanceResource`) read
 * `.value()` raw and so lost the cost-vs-market chart on any transient
 * failure of the ~5-minute refresh-cycle reload. Applying the rule halfway
 * is what this function exists to make impossible.
 *
 * The asymmetry it papers over is documented at length on `lastGoodValue`
 * (`shared/util/last-good-value.ts`): an in-flight reload keeps the previous
 * value while `isLoading()` flips true, whereas a FAILED reload replaces the
 * resolved stream wholesale with `{ error }`. Hence both derived signals gate
 * on the cached value, not on the resource's own `hasValue()`.
 *
 * Callers render from `value` and never touch the resource's own
 * `.value()`/`.hasValue()` once it is wired through here. Composing several
 * of these with `||` (as the two overview pages do) is expected — the point
 * is that each resource's rule is right, not that a page has only one.
 */
export function resourceState<T>(resource: StatefulResource<T>): ResourceState<T> {
  const value = lastGoodValue(resource);
  return {
    value,
    isLoading: computed(() => resource.isLoading() && value() === undefined),
    hasError: computed(() => resource.error() != null && value() === undefined),
  };
}
