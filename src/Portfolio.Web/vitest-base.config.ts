import { defineConfig } from 'vitest/config';

/**
 * D22 root-cause fix, not a bumped timeout.
 *
 * `ng test` defaults to Vitest's thread pool sized at the machine's logical
 * CPU count (16 here), with `isolate: false` (the CLI's own default, chosen
 * to match the Karma/Jasmine experience). That means, at the start of every
 * run, up to 16 spec files start compiling and rendering **at once** — and
 * several of them (`allocation-pie-chart`, `annual-return-chart`,
 * `cost-vs-market-chart`, `chart-theme`) construct real ECharts instances in
 * jsdom, which is CPU-heavy layout/DOM work even with the canvas
 * `measureText` sink removed (see `test-setup.ts`).
 *
 * `transaction-form.dialog.spec.ts` and `confirm-dialog.spec.ts` were each
 * observed timing out — always on the file's *first* test, never a later one
 * in the same file — because that first test alone pays Angular's one-time
 * JIT template-compilation cost for a `MatDialog` host plus
 * `MatDatepickerModule`/`MatSelectModule`. `app-shell.spec.ts` had the same
 * failure mode from a different one-time cost — its own last test is the
 * only one that navigates to the lazy `/transactions` route, so it alone
 * pays that chunk's first `import()`. All three now also warm that
 * particular cost in a `beforeAll` (see each file), which helps, but doesn't
 * remove the need for this: the one-time cost is normally well under a
 * second, but a genuinely idle core is not something a busy thread pool can
 * guarantee, so it occasionally lands in a scheduling gap wide enough to
 * blow Vitest's 5s per-test (or even 10s per-hook) default — worse, and
 * reproducible on demand, the moment something *else* legitimate is also
 * contending for the same physical cores, which on this project is the
 * normal case: `backend-dotnet` and `frontend-angular` sessions run
 * concurrently on one machine per `CLAUDE.md`'s own agent model, each
 * capable of a `dotnet build`/`dotnet test` at the same time as this suite.
 * Capping the pool well below the logical core count leaves real headroom
 * for that neighbour, not just for this suite's own worker threads, rather
 * than widening a timeout around load this suite doesn't control.
 */
export default defineConfig({
  test: {
    pool: 'threads',
    maxWorkers: 2,
    minWorkers: 1,
    /**
     * F9 — raised from Vitest's 5s default, which one test in
     * `asset-detail.page.spec.ts` ("does not blank the cost-vs-market chart
     * when the performance reload fails on a refresh cycle") blew on a cold
     * run: it is a `HttpTestingController`-driven `fixture.detectChanges()`
     * flow with no `await`/timer of its own, so the 5s it burned was entirely
     * the D22 thread-pool contention this file already documents above, not
     * genuine test work. Measured directly with `--reporters=verbose`
     * (`ng test --include=asset-detail.page.spec.ts`): that same test's own
     * reported duration was **33149ms** on the cold run that reproduced the
     * flake, 24s for the whole file warm per the original F9 report. 45s is
     * chosen deliberately, not rounded up to "safely large" — it gives ~35%
     * headroom over the one worst measurement taken, anchored to a real
     * number rather than a guess, and specifically *not* so high that a
     * genuine hang (an unresolved promise, a `fixture.whenStable()` that
     * never settles) degrades into a multi-minute wait instead of a failure.
     * If a future cold run blows past 45s, that is new evidence the
     * contention got worse, not a signal to raise this further without
     * re-measuring.
     */
    testTimeout: 45_000,
  },
});
