import { signal } from '@angular/core';

import { StatefulResource, resourceState } from './resource-state';

/**
 * A hand-rolled stand-in for `httpResource`, modelling the exact asymmetry
 * documented on `lastGoodValue`:
 *
 * - `startReload()` — in flight: `isLoading()` flips true, the previous value
 *   is STILL THERE (Angular carries the prior `stream` forward when the
 *   request is unchanged).
 * - `failReload()` — failed: the resolved stream is replaced wholesale with
 *   `{ error }`, so `hasValue()`/`value()` go empty even though good data was
 *   on screen a moment ago.
 *
 * Depending on the narrow `StatefulResource` interface rather than
 * `HttpResourceRef` is what makes this possible without an HTTP layer at all.
 */
function fakeResource<T>() {
  const value = signal<T | undefined>(undefined);
  const loading = signal(false);
  const error = signal<Error | undefined>(undefined);

  const resource: StatefulResource<T | undefined> = {
    hasValue: () => value() !== undefined,
    value: () => value(),
    isLoading: () => loading(),
    error: () => error(),
  };

  return {
    resource,
    startLoad: () => loading.set(true),
    resolve: (next: T) => {
      value.set(next);
      loading.set(false);
      error.set(undefined);
    },
    startReload: () => loading.set(true),
    failReload: () => {
      value.set(undefined);
      loading.set(false);
      error.set(new Error('boom'));
    },
  };
}

describe('resourceState', () => {
  it('reports loading, with no value and no error, before anything has ever resolved', () => {
    const fake = fakeResource<string>();
    const state = resourceState(fake.resource);

    fake.startLoad();

    expect(state.isLoading()).toBe(true);
    expect(state.hasError()).toBe(false);
    expect(state.value()).toBeUndefined();
  });

  it('exposes the resolved value and stops loading once it arrives', () => {
    const fake = fakeResource<string>();
    const state = resourceState(fake.resource);

    fake.startLoad();
    fake.resolve('first');

    expect(state.value()).toBe('first');
    expect(state.isLoading()).toBe(false);
    expect(state.hasError()).toBe(false);
  });

  it('keeps the previous value while a reload is IN FLIGHT, and does not re-enter the loading state', () => {
    const fake = fakeResource<string>();
    const state = resourceState(fake.resource);

    fake.resolve('first');
    expect(state.value()).toBe('first');

    fake.startReload();

    expect(state.value()).toBe('first');
    // The point of the `cache === undefined` guard: a background reload must
    // not put a page that already has content back into its loading state.
    expect(state.isLoading()).toBe(false);
    expect(state.hasError()).toBe(false);
  });

  it('keeps the previous value when a reload FAILS — the asymmetry lastGoodValue exists for', () => {
    const fake = fakeResource<string>();
    const state = resourceState(fake.resource);

    fake.resolve('first');
    expect(state.value()).toBe('first');

    fake.startReload();
    fake.failReload();

    // The resource itself has gone empty and is reporting an error...
    expect(fake.resource.hasValue()).toBe(false);
    expect(fake.resource.error()).toBeInstanceOf(Error);
    // ...but the content the user is looking at survives, and no error state
    // replaces it. Refresh health belongs in the toolbar indicator (rule #3).
    expect(state.value()).toBe('first');
    expect(state.hasError()).toBe(false);
    expect(state.isLoading()).toBe(false);
  });

  it('reports hasError ONLY when nothing has ever loaded', () => {
    const fake = fakeResource<string>();
    const state = resourceState(fake.resource);

    fake.startLoad();
    expect(state.hasError()).toBe(false);

    fake.failReload();

    expect(state.hasError()).toBe(true);
    expect(state.isLoading()).toBe(false);
    expect(state.value()).toBeUndefined();

    // And it clears again once a value finally arrives.
    fake.resolve('recovered');
    expect(state.hasError()).toBe(false);
    expect(state.value()).toBe('recovered');
  });

  it('carries the last good value forward across repeated failures, not just the first', () => {
    const fake = fakeResource<string>();
    const state = resourceState(fake.resource);

    fake.resolve('first');
    expect(state.value()).toBe('first');

    fake.failReload();
    expect(state.value()).toBe('first');

    fake.resolve('second');
    expect(state.value()).toBe('second');

    fake.failReload();
    expect(state.value()).toBe('second');
    expect(state.hasError()).toBe(false);
  });
});
