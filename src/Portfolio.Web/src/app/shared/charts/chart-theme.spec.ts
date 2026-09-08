import { computed } from '@angular/core';
import { foldToOther, gainLossColor, readChartTokens, seriesColor, themeVersion } from './chart-theme';

describe('chart-theme', () => {
  describe('readChartTokens', () => {
    it('falls back to the documented light-mode palette when no stylesheet is applied', () => {
      const tokens = readChartTokens();
      // These are the exact hexes ui.tokens.scss declares under :root — the
      // dataviz-validated default palette (see references/palette.md).
      expect(tokens.series).toEqual([
        '#2a78d6',
        '#eb6834',
        '#1baf7a',
        '#eda100',
        '#e87ba4',
        '#008300',
        '#4a3aa7',
        '#e34948',
      ]);
      expect(tokens.gain).toBe('#006300');
      expect(tokens.loss).toBe('#d03b3b');
    });

    it('reads a real custom property off the DOM when one is set', () => {
      document.documentElement.style.setProperty('--ui-color-gain', '#123456');
      try {
        const tokens = readChartTokens();
        expect(tokens.gain).toBe('#123456');
      } finally {
        document.documentElement.style.removeProperty('--ui-color-gain');
      }
    });
  });

  describe('seriesColor', () => {
    it('assigns the fixed hues in order and never past index 7', () => {
      const tokens = readChartTokens();
      expect(seriesColor(tokens, 0)).toBe(tokens.series[0]);
      expect(seriesColor(tokens, 7)).toBe(tokens.series[7]);
      // Index 8 must wrap rather than throw — callers are expected to have
      // already folded a 9th series into "Other" before this point.
      expect(seriesColor(tokens, 8)).toBe(tokens.series[0]);
    });
  });

  describe('gainLossColor', () => {
    it('colours a positive value with the gain token and a negative with loss', () => {
      const tokens = readChartTokens();
      expect(gainLossColor(tokens, 12.5)).toBe(tokens.gain);
      expect(gainLossColor(tokens, -0.01)).toBe(tokens.loss);
      expect(gainLossColor(tokens, 0)).toBe(tokens.gain);
    });
  });

  describe('themeVersion (D16 — re-resolve on theme change)', () => {
    // The real trigger is a `matchMedia` change event or a `[data-theme]`
    // mutation, neither of which jsdom implements (see the guards in
    // chart-theme.ts, mirroring test-setup.ts's ResizeObserver stub) — so
    // this proves the *reactive wiring* `readChartTokens()` relies on
    // (a `computed()` re-running when `themeVersion` changes), not the two
    // browser listeners that bump it. Those remain unverified outside a
    // real browser; see the D16 report.
    afterEach(() => {
      document.documentElement.style.removeProperty('--ui-color-gain');
    });

    it('does NOT re-read the DOM on its own when a token value changes underneath it', () => {
      const tokens = computed(() => readChartTokens());
      expect(tokens().gain).toBe('#006300');

      document.documentElement.style.setProperty('--ui-color-gain', '#abcdef');
      expect(tokens().gain).toBe('#006300'); // stale until themeVersion bumps
    });

    it('re-reads the DOM once themeVersion bumps, exactly the signal the theme-change listeners raise', () => {
      document.documentElement.style.setProperty('--ui-color-gain', '#abcdef');
      const tokens = computed(() => readChartTokens());
      expect(tokens().gain).toBe('#abcdef');

      document.documentElement.style.setProperty('--ui-color-gain', '#123456');
      themeVersion.update((v) => v + 1);
      expect(tokens().gain).toBe('#123456');
    });
  });

  // F6 — the two theme-change listeners used to be installed as a
  // module-level side effect at `import` time. These tests mock
  // `window.matchMedia` and the global `MutationObserver` *before* loading a
  // fresh copy of the module (`vi.resetModules()` + a dynamic `import()`),
  // so the assertions are about the module itself, not about whatever the
  // statically-imported copy above already did as a side effect of this
  // file's earlier `describe` blocks calling `readChartTokens()`.
  describe('module-level side effects (F6)', () => {
    let originalMatchMedia: typeof window.matchMedia;
    let originalMutationObserver: typeof MutationObserver;

    beforeEach(() => {
      originalMatchMedia = window.matchMedia;
      originalMutationObserver = globalThis.MutationObserver;
      vi.resetModules();
    });

    afterEach(() => {
      window.matchMedia = originalMatchMedia;
      globalThis.MutationObserver = originalMutationObserver;
    });

    it('registers nothing merely by importing the module', async () => {
      const matchMediaSpy = vi.fn(() => ({
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      }));
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      window.matchMedia = matchMediaSpy as any;
      const observeSpy = vi.fn();
      const mutationObserverCtorSpy = vi.fn(function (this: unknown) {
        return { observe: observeSpy, disconnect: vi.fn() };
      });
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      globalThis.MutationObserver = mutationObserverCtorSpy as any;

      await import('./chart-theme');

      expect(matchMediaSpy).not.toHaveBeenCalled();
      expect(mutationObserverCtorSpy).not.toHaveBeenCalled();
    });

    it('installs both listeners on the first readChartTokens() call, and the OS-preference listener still bumps themeVersion so tokens re-resolve', async () => {
      const changeListeners: (() => void)[] = [];
      const matchMediaSpy = vi.fn(() => ({
        addEventListener: (_event: string, handler: () => void) => changeListeners.push(handler),
        removeEventListener: vi.fn(),
      }));
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      window.matchMedia = matchMediaSpy as any;
      const observeSpy = vi.fn();
      const mutationObserverCtorSpy = vi.fn(function (this: unknown) {
        return { observe: observeSpy, disconnect: vi.fn() };
      });
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      globalThis.MutationObserver = mutationObserverCtorSpy as any;

      const fresh = await import('./chart-theme');

      // Not installed yet — importing alone still registers nothing.
      expect(matchMediaSpy).not.toHaveBeenCalled();
      expect(mutationObserverCtorSpy).not.toHaveBeenCalled();

      document.documentElement.style.setProperty('--ui-color-gain', '#111111');
      const tokens = computed(() => fresh.readChartTokens());
      expect(tokens().gain).toBe('#111111');

      // First call installed both listeners exactly once.
      expect(matchMediaSpy).toHaveBeenCalledTimes(1);
      expect(mutationObserverCtorSpy).toHaveBeenCalledTimes(1);
      expect(observeSpy).toHaveBeenCalledWith(document.documentElement, {
        attributes: true,
        attributeFilter: ['data-theme'],
      });
      expect(changeListeners).toHaveLength(1);

      // A second call must not install a second pair.
      fresh.readChartTokens();
      expect(matchMediaSpy).toHaveBeenCalledTimes(1);
      expect(mutationObserverCtorSpy).toHaveBeenCalledTimes(1);

      // The theme-change path: firing the captured OS-preference handler
      // (what the browser would do on a real light/dark toggle) bumps
      // themeVersion, which is exactly the signal readChartTokens()'s
      // callers rely on to re-resolve colours.
      document.documentElement.style.setProperty('--ui-color-gain', '#222222');
      changeListeners[0]();
      expect(tokens().gain).toBe('#222222');

      document.documentElement.style.removeProperty('--ui-color-gain');
    });
  });

  describe('foldToOther', () => {
    it('leaves 8 or fewer items untouched', () => {
      const items = Array.from({ length: 8 }, (_, i) => ({ value: i }));
      const result = foldToOther(items, (rest) => ({ value: rest.reduce((s, r) => s + r.value, 0) }));
      expect(result).toEqual(items);
    });

    it('folds the 9th+ item into a single "Other" bucket capped at 8 total slots', () => {
      const items = Array.from({ length: 10 }, (_, i) => ({ value: i }));
      const result = foldToOther(items, (rest) => ({ value: rest.reduce((s, r) => s + r.value, 0) }));
      expect(result).toHaveLength(8);
      // First 7 kept as-is, the 8th slot is "Other" summing indices 7..9 (7+8+9=24).
      expect(result.slice(0, 7)).toEqual(items.slice(0, 7));
      expect(result[7].value).toBe(24);
    });
  });
});
