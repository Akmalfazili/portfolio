import { TestBed } from '@angular/core/testing';

import { THEME_STORAGE_KEY, ThemeStore } from './theme-store';

describe('ThemeStore', () => {
  beforeEach(() => {
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
  });

  // No component/fixture involved (this store's own effect is the thing
  // under test, not a template), so nothing else flushes pending effects —
  // `TestBed.tick()` does, and must be called after construction and after
  // every `setPreference()` for the `data-theme` assertions below to see the
  // effect's result rather than its stale pre-run state.
  function create(): ThemeStore {
    TestBed.configureTestingModule({});
    const store = TestBed.inject(ThemeStore);
    TestBed.tick();
    return store;
  }

  function setPreference(store: ThemeStore, value: 'light' | 'dark' | 'system'): void {
    store.setPreference(value);
    TestBed.tick();
  }

  it('defaults to "system" and sets no data-theme attribute when nothing is stored', () => {
    const store = create();

    expect(store.preference()).toBe('system');
    expect(document.documentElement.hasAttribute('data-theme')).toBe(false);
  });

  it('reads a previously stored explicit preference on construction', () => {
    localStorage.setItem(THEME_STORAGE_KEY, 'dark');

    const store = create();

    expect(store.preference()).toBe('dark');
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
  });

  it('setPreference("light") sets data-theme="light" and persists it', () => {
    const store = create();

    setPreference(store, 'light');

    expect(document.documentElement.getAttribute('data-theme')).toBe('light');
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('light');
  });

  it('setPreference("dark") sets data-theme="dark" and persists it', () => {
    const store = create();

    setPreference(store, 'dark');

    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark');
  });

  it('setPreference("system") after an explicit choice removes the attribute rather than pinning the last resolved value — the D25 failure mode', () => {
    const store = create();
    setPreference(store, 'dark');
    expect(document.documentElement.hasAttribute('data-theme')).toBe(true);

    setPreference(store, 'system');

    expect(document.documentElement.hasAttribute('data-theme')).toBe(false);
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBeNull();
  });

  it('ignores a garbage stored value and falls back to "system"', () => {
    localStorage.setItem(THEME_STORAGE_KEY, 'not-a-theme');

    const store = create();

    expect(store.preference()).toBe('system');
    expect(document.documentElement.hasAttribute('data-theme')).toBe(false);
  });
});
