// Global test-environment polyfills for the Vitest-based unit-test builder
// (jsdom does not implement every browser API). Currently needed by the
// Phase 9 chart components: NgxEchartsDirective observes its host element
// with a real `ResizeObserver` (for `autoResize`), which jsdom has no
// implementation of at all — every chart spec would otherwise fail with
// "please install a polyfill for ResizeObserver" before a single assertion
// runs. This is a inert no-op stub, not a behavioural fake — chart specs
// don't assert on resize behaviour, only on the option/data the component
// builds.
if (typeof globalThis.ResizeObserver === 'undefined') {
  class ResizeObserverStub {
    observe(): void {
      /* no-op */
    }
    unobserve(): void {
      /* no-op */
    }
    disconnect(): void {
      /* no-op */
    }
  }
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  (globalThis as any).ResizeObserver = ResizeObserverStub;
}

// D22 root cause, not a Karma/canvas cosmetic gap. Every chart test (`echarts`,
// `renderer: 'svg'`) still calls `HTMLCanvasElement.getContext('2d')` on an
// *offscreen* canvas purely to measure text width for label layout — that
// path runs regardless of SVG vs canvas painting (`zrender/core/platform.js`).
// Without the native `canvas` npm package (not installed — it needs a
// node-gyp/Cairo toolchain we don't want as a Windows dev dependency), jsdom's
// `getContext` returns `null` on *every single call* and, worse, constructs a
// fresh `Error` and emits it through the virtual console each time — so every
// axis tick, legend row and bar/pie label measured by every chart in every
// spec pays a synchronous stack-trace-plus-console-write tax. zrender caches
// its measuring context in a module-level closure the moment `getContext`
// returns something truthy, so a *single* real-looking context fixes every
// chart spec for the rest of the run, not just one file.
//
// That CPU/stdio load, spread across three chart-heavy spec files running
// concurrently with the rest of the suite, is what made
// `transaction-form.dialog.spec.ts`'s first test — which pays Angular's own
// one-time JIT template-compilation cost for the dialog, datepicker and
// select — intermittently blow past vitest's 5s per-test timeout (D22). The
// timeout was a resource-starvation symptom, not a bug in that test or the
// component; stubbing a cheap, real `measureText` removes the sink rather
// than papering over the symptom with a longer timeout or a retry.
if (typeof HTMLCanvasElement !== 'undefined') {
  const stub2dContext = {
    measureText: (text: string) => ({ width: (text?.length ?? 0) * 6 }) as TextMetrics,
    save: () => {},
    restore: () => {},
    scale: () => {},
    translate: () => {},
    rotate: () => {},
    transform: () => {},
    setTransform: () => {},
    clearRect: () => {},
    fillRect: () => {},
    strokeRect: () => {},
    beginPath: () => {},
    closePath: () => {},
    moveTo: () => {},
    lineTo: () => {},
    arc: () => {},
    fill: () => {},
    stroke: () => {},
    createLinearGradient: () => stub2dContext,
    createRadialGradient: () => stub2dContext,
    createPattern: () => null,
    addColorStop: () => {},
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
  } as unknown as CanvasRenderingContext2D;

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  (HTMLCanvasElement.prototype as any).getContext = function (contextId: string) {
    return contextId === '2d' ? stub2dContext : null;
  };
}

// D25's ThemeStore (core/theme/theme-store.ts) is a `providedIn: 'root'`
// singleton that reads/writes real `localStorage` and the real `<html
// data-theme>` attribute in its constructor — genuine globals that
// `TestBed`'s per-test DI reset does not touch. Left alone, a theme choice
// made in one spec (any spec that renders `AppShell`/`ThemeToggle`, not just
// theme-store.spec.ts) would leak into the next one, in this file or another
// — exactly the kind of cross-test state D22 warns about. Reset both after
// every test, unconditionally, since jsdom always implements `localStorage`.
afterEach(() => {
  localStorage.clear();
  document.documentElement.removeAttribute('data-theme');
});
