import { Injectable, effect, signal } from '@angular/core';

export type ThemePreference = 'light' | 'dark' | 'system';

/** Matches the inline bootstrap script in index.html — keep both in sync. */
export const THEME_STORAGE_KEY = 'portfolio-web:theme';

function readStoredPreference(): ThemePreference {
  try {
    const stored = localStorage.getItem(THEME_STORAGE_KEY);
    return stored === 'light' || stored === 'dark' ? stored : 'system';
  } catch {
    // localStorage can throw in a locked-down context (private browsing with
    // storage disabled, etc.) — fall back to "system" rather than break.
    return 'system';
  }
}

/**
 * D25 — the toolbar's three-state Light / Dark / Follow OS toggle. Single
 * source of truth for `data-theme` on `<html>`, which is all ui.tokens.scss
 * needs: setting it to `'light'`/`'dark'` beats the OS preference in either
 * direction, and — the part this store has to get right, not just the
 * tokens — "Follow OS" means *removing the attribute entirely* so the
 * `@media (prefers-color-scheme: dark)` branch in ui.tokens.scss decides
 * again. Pinning the last resolved OS value instead of removing the
 * attribute is the standard way this feature gets built wrong: it would stop
 * following the OS the moment the user's system theme next changes, while
 * still claiming to.
 *
 * The choice persists to `localStorage` and is re-applied here on every
 * change so navigating/toggling within the running app works. The *first*
 * paint doesn't depend on this store at all — see the inline script in
 * index.html, which reads the same key synchronously before Angular
 * bootstraps so there is nothing to flash from.
 */
@Injectable({ providedIn: 'root' })
export class ThemeStore {
  private readonly _preference = signal<ThemePreference>(readStoredPreference());
  readonly preference = this._preference.asReadonly();

  constructor() {
    effect(() => {
      const preference = this._preference();
      if (preference === 'system') {
        document.documentElement.removeAttribute('data-theme');
      } else {
        document.documentElement.setAttribute('data-theme', preference);
      }
    });
  }

  setPreference(preference: ThemePreference): void {
    this._preference.set(preference);
    try {
      if (preference === 'system') {
        localStorage.removeItem(THEME_STORAGE_KEY);
      } else {
        localStorage.setItem(THEME_STORAGE_KEY, preference);
      }
    } catch {
      // Best effort — the in-memory signal (and so the current session)
      // still reflects the choice even if it can't be persisted.
    }
  }
}
