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
