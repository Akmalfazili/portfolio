using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services.Calculators;
using Portfolio.Application.Services.Calendar;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services;

/// <summary>
/// See <see cref="IPriceBackfillService"/>. Reporting currency is USD, so only currencies other
/// than USD need an FX history (today that is just SGD, for Z74) — the FX pair(s) needed are
/// derived from the currencies of assets that actually have transactions, not hard-coded.
///
/// Stocks only, by decision — crypto is gain/loss only and keeps no <see cref="PriceHistory"/> at
/// all, so backfilling it would spend Twelve Data/CoinGecko rate limit on data nothing reads.
///
/// <para><b>D47: due-ness and the run itself are scoped per <see cref="Market"/>, not global.</b>
/// The bug this closed: NYSE and SGX close at very different local times but were gated by ONE
/// NYSE-keyed "is the market open" check plus ONE once-per-Singapore-day latch, applied to every
/// asset. NYSE closing at ~04:00 SGT satisfied both gates for the whole run, including Z74 — but
/// SGX's own session for that same calendar day had not even opened yet at 04:00 SGT, so Z74 only
/// ever got the PREVIOUS day's close at that point. By the time SGX actually published its close
/// (17:00 SGT), the once-per-day latch had already fired (from the 04:00 run) and blocked a
/// second attempt — and NYSE reopening at 21:30 SGT then blocked everything again until the next
/// 04:00. Z74's close landed roughly 11 hours late, every single day, with 100% uptime — a
/// structural gating bug, not a downtime symptom. The read path
/// (<c>PortfolioSummaryService</c>'s live/close fallback) was never wrong; the close it fell back
/// to just usually was not on file yet. The fix gates each market on its OWN session and its OWN
/// last close (<see cref="IMarketCalendar.LastSessionCloseAt"/>) rather than NYSE's or a shared
/// calendar-day latch — see <c>RunIfDueAsync</c> and tracker.md's D47 entry.</para>
///
/// <para><b>D51: "a run completed" is not "the market is covered" — D47's own trap, one layer
/// down.</b> A transient outage (e.g. the API container's DNS taking ~30-60s to come up after a
/// restart) landing exactly when a market's close became due could throw for some assets while
/// others still succeeded; the run's <see cref="Domain.Entities.RefreshRun"/> row still counted as
/// "completed since last close" for D47's gate, and D47's gate had no concept of failure — so the
/// failed assets were stranded until the market's NEXT session close, which for a Friday failure
/// means a full weekend stale. Two changes close this: <see cref="Domain.Entities.RefreshRun.Success"/>
/// is now computed PER MARKET (a market's row fails iff one of ITS OWN assets or FX pairs failed —
/// see the per-market loop at the end of <c>RunAsync</c>), and <c>RunIfDueAsync</c> offers a
/// bounded, paced retry (<see cref="PriceBackfillOptions.FailedRunRetryDelay"/>,
/// <see cref="PriceBackfillOptions.MaxFailedRunRetriesPerClose"/>) when the most recent run since a
/// market's last close failed. A retry is narrowed to only the assets (and FX pairs) still missing
/// that market's latest close — see the <c>retryOnlyMarkets</c> handling inside <c>RunAsync</c>'s
/// private core below — so it never re-spends a credit confirming something a partially-successful
/// first pass already filled in. Only <c>RunIfDueAsync</c>'s own scheduled retries are narrowed;
/// the manual <c>POST /api/prices/backfill</c> path (the public <c>RunAsync</c>) stays a full pass,
/// unchanged, because a full pass is also what fills history for a newly recorded back-dated
/// transaction on an asset whose latest close is already on file.</para>
///
/// <para><b>D53: a market reading "closed" is not the same as its close being SETTLED — a completed
/// but unfinished bar can freeze in permanently, for any trigger, not just a scheduled one.</b> The
/// only guard against writing today's in-progress bar as today's close used to be
/// <c>from == today</c>, which defers a range only when it is ENTIRELY today; it does nothing once
/// an asset has ANY prior history, which is the ordinary case. Three ways this bit in practice: (1)
/// the manual endpoint (<c>POST /api/prices/backfill</c>) deliberately bypasses due-ness and the
/// market calendar entirely, so a manual click mid-session requests up to `today` and gets today's
/// partial bar back; (2) SGX's midday lunch break reads <see cref="IMarketCalendar.IsOpen"/> false
/// while the session is still ongoing; (3) SGX's closing routine (pre-close auction ~17:00-17:06
/// SGT, trade-at-close to ~17:16) runs AFTER the calendar already models the session as closed at
/// 17:00, so even a request right at the nominal close can land mid-auction. All three let a
/// provider's `end_date`/`period2` argument (or, for a provider that ignores it, its response
/// regardless of what was asked) include an unfinished bar, and the per-asset uniqueness check
/// (<c>existingDateSet</c> below) means that bar is on file forever once inserted — it is never
/// reconciled against a later, correct close for the same date. Fixed with a per-market "cap": the
/// latest date this run will ever request from a provider or accept from one, computed once as
/// <c>calendar.LocalDateOn(market, calendar.LastSessionCloseAt(market, now - CloseSettleDelay))</c>
/// (<see cref="PriceBackfillOptions.CloseSettleDelay"/>) — see <c>capByMarket</c> in
/// <c>RunAsyncCore</c> below, applied both as the requested `to` and, defensively, as a filter on
/// whatever points a provider actually returns. Subtracting <c>CloseSettleDelay</c> from `now`
/// BEFORE walking <c>LastSessionCloseAt</c> back is what handles the lunch break for free: at 12:48
/// SGT, the settled instant (12:18 SGT) is still well before today's 17:00 close, so
/// <c>LastSessionCloseAt</c> walks back to the PREVIOUS day's close exactly as it would mid-morning
/// — there is no separate "is this a lunch break" case to get wrong.
/// <c>RunIfDueAsync</c>'s own due-ness check is changed to consult <c>LastSessionCloseAt</c> on the
/// SAME settled instant, not raw `now` — this is not optional: if due-ness stayed on raw `now`
/// while the cap moved to the settled instant, a scheduled run firing between a close and
/// `now - CloseSettleDelay` would find itself "due" against today's (unsettled) close, defer every
/// asset via the cap, and still write a <i>successful</i> <c>RefreshRun</c> row for that close —
/// which is D51's exact trap one layer in: the next tick would read that row as
/// <see cref="Dtos.PriceBackfillSkipReason.AlreadyCoveredSinceLastClose"/> and never fetch the close
/// at all. See tracker.md's D53 entry for the live evidence (8 of Z74's 1552 stored closes were
/// wrong, all written by manual runs during SGX hours) and for why D48's same-date tie-break in
/// <c>PortfolioSummaryService</c> depends on this fix rather than needing one of its own.</para>
///
/// <para><b>D53 follow-up: the cap alone is wrong if Twelve Data's `end_date` is EXCLUSIVE, and
/// live evidence says it probably is.</b> The first D53 pass sent `end_date = cap` (or `= fxCap`)
/// straight through, on the unstated assumption that `end_date` behaves like this contract's own
/// `to` (inclusive). A coordinator review of the live `RefreshRuns`/`PriceHistories` history found
/// the opposite: a manual NYSE run mid-session on 2026-09-08 sent `end_date=2026-09-08` and
/// inserted no 2026-09-08 rows at all — they only landed the next day once a run sent
/// `end_date=2026-09-09` — and a scheduled FX run on 2026-09-12 sent `end_date=2026-09-12` and
/// stored no 2026-09-12 USD/SGD row, despite Twelve Data publishing weekend FX bars on file either
/// side of it. The ORIGINAL (pre-D53) code only ever worked for NYSE by coincidence: SGT `today` is
/// already D+1 relative to an NYSE close on day D, so an exclusive `end_date = D+1` still returned
/// D. Sending the settled `cap` itself as an exclusive `end_date` would have silently landed every
/// US close and every FX rate one day late, forever — <c>RunIfDueAsync</c> would still record the
/// run as covering that close (D47/D51), and the read path would fall back to the stale mid-session
/// quote because it out-dates the (missing) close, reopening D48's exact symptom every night.
/// <c>TwelveDataQuoteProvider</c>/<c>TwelveDataFxProvider</c> now send `end_date = to.AddDays(1)`
/// — correct under EITHER reading of `end_date`, since <see cref="IQuoteProvider.GetHistoryAsync"/>
/// and <see cref="IFxRateProvider.GetHistoryAsync"/>'s own `to` is contractually inclusive
/// regardless of what any one implementation's upstream query string requires to achieve that. This
/// makes the `point.Date > cap`/`point.Date > fxCap` filters below load-bearing, not merely defence
/// in depth, for a Twelve Data asset specifically: if `end_date` turns out to be inclusive after
/// all, the `+1` day asks for (and may receive) one real day beyond the cap, and these filters are
/// the only thing stopping it from reaching <c>PriceHistory</c>/<c>FxRates</c>.</para>
///
/// <para><b>2026-09-15: BackfillCatchUp and a D51 narrowed retry request only the TAIL that is
/// missing — not the full range from the asset's earliest trade date.</b> Live evidence: a queued
/// catch-up requested USD/SGD with `start_date=2020-07-10` to obtain one missing day, and a
/// scheduled NYSE retry requested AAPL from `start_date=2022-10-08` — both discarding almost the
/// entire response via the per-date uniqueness check, on every run, forever. Credits are unaffected
/// (Twelve Data charges one credit per symbol regardless of range), but a multi-year payload is far
/// more exposed to a 10-second HTTP timeout on a weak connection than the one or two rows actually
/// missing.
///
/// <para><b>The narrowed `from` must be proven by BOTH coverage state AND stored data — state alone
/// is unsafe.</b> The first version of this fix narrowed to <c>state.CoveredTo + 1</c> alone. A
/// coordinator review caught the gap: a run can succeed and record <c>CoveredTo = cap</c> even when
/// the provider's own response did not actually include that specific date's bar (a late-publishing
/// provider, or a partial response) — this is exactly why D51's own retry SELECTION already filters
/// by stored rows (<c>lastBackfilledByAsset</c> below and the newest stored <c>FxRate</c> for a
/// pair), not by state: an asset can be "successfully asked" through the cap while still genuinely
/// missing the cap's own bar. Narrowing on state alone would then compute
/// <c>from = state.CoveredTo + 1</c>, land one day past the cap, and file the asset under
/// <see cref="Dtos.PriceBackfillSummary.AssetsAlreadyCovered"/> with ZERO calls spent — silently
/// stopping the retry of a close that is still missing until the market's NEXT session close, D47's
/// own lateness family one layer further in. Fixed by narrowing to
/// <c>min(state.CoveredTo, newest stored date) + 1</c> instead — see the asset loop's and FX loop's
/// own `narrowingApplies` checks below, both of which now query the newest actually-stored row/rate
/// alongside the state before narrowing, and fall back to the FULL range (no narrowing at all) when
/// there is no stored data whatsoever to corroborate state with. A back-dated transaction that moves
/// the asset's own earliest trade date BEFORE `state.CoveredFrom` also falls back to the full range:
/// the state does not prove that newly-relevant early history was ever asked for. Applies to exactly
/// two paths, by design — <see cref="Domain.Enums.RefreshTrigger.BackfillCatchUp"/>
/// (<see cref="RunCatchUpAsync"/>) and a market in <c>retryOnlyMarkets</c> (D51's own narrowed
/// retry) — never the first scheduled pass per close or the manual endpoint, both of which stay a
/// full pass unchanged: a full pass is also what backfills a newly recorded back-dated transaction
/// on an asset whose latest close happens to already be on file, which a tail-only request would
/// never revisit. The consequence of the fix: a late-publishing-bar retry now makes a genuine 1-2
/// day call (the same credit it always cost) rather than narrowing past it — this is a deliberately
/// slightly larger request than the state-alone version, in exchange for never silently stopping a
/// retry that is still needed.</para>
///
/// <para><b>The coverage-merge trap.</b> <c>RecordPriceHistoryState</c>/<c>RecordFxPairState</c>
/// used to set <c>CoveredFrom</c>/<c>CoveredTo</c> straight from the requested range. A narrowed
/// request's own `from` can be later than the state's existing <c>CoveredFrom</c> — passing it
/// straight through would overwrite <c>CoveredFrom</c> with that later date, and the very next
/// planner check (<c>RefreshCatchUpService</c>'s <c>coveredFrom &lt;= firstTrade</c> test) would then
/// read the EARLIER history as newly uncovered and re-request the full range — which narrows back
/// down next time, then goes full again: the exact "re-spends credits every click, forever" loop
/// tracker.md's 2026-09-14 entry exists to prevent, one layer further down. Fixed by making both
/// methods take the UNION of the existing and newly-requested range (min <c>CoveredFrom</c>, max
/// <c>CoveredTo</c>) on every success, narrowed or full — a full-range success's own range is always
/// a superset of whatever was already covered, so the union is a no-op for it. A non-contiguous
/// union (a gap between the old covered range and the new one) does not occur by construction: every
/// caller either requests the full range from the asset's/currency's own lower bound, or resumes at
/// <c>min(state.CoveredTo, newest stored date) + 1</c>, which is never later than
/// <c>state.CoveredTo + 1</c> — there is never a requested range that starts strictly after the
/// state's own existing upper bound, so the two ranges are always contiguous or overlapping. Note
/// what this union does NOT paper over: <c>CoveredTo</c> itself is still recorded as the REQUESTED
/// cap on success (see the per-asset/per-currency call sites below), not the newest point actually
/// returned — the dual state-AND-stored check above is what keeps a still-missing cap-day bar from
/// being narrowed past, precisely because it does not trust <c>CoveredTo</c> alone.</para>
///
/// <para><b>A narrowed `from` landing beyond the cap means BOTH sources already reach it — "already
/// covered", not "not yet settled".</b> The planner (<c>RefreshCatchUpService</c>) and this method
/// compute the settled cap and read coverage state at slightly different instants, so a narrowed
/// `from` can legitimately land past `cap`/`fxCap` even though the planner judged the asset/currency
/// missing (most plausibly a concurrent run advancing coverage between the planner's decision and
/// this run's execution). Filing that under
/// <see cref="Dtos.PriceBackfillSummary.AssetsSkippedTodayNotClosed"/> — whose name and doc comment
/// both assert "not yet settled" — would be exactly the asserts-a-single-reason defect CLAUDE.md
/// calls out (D10/D26/D33/D35/D38/D45/D47): it is not true here, the market HAS settled, coverage
/// just already reaches it. Filed instead under
/// <see cref="Dtos.PriceBackfillSummary.AssetsAlreadyCovered"/> — reachable now only when the newest
/// STORED row/rate reaches the cap too, not merely the state, so the label is honest in the sense
/// that matters: the close really is on file, not just "asked for" — still zero calls spent, still
/// not a failure, just an honest different reason.</para>
///
/// <para><b>Left deliberately unnarrowed: a legitimately empty narrowed FX range.</b> A narrowed
/// asset request's range always includes `cap` itself, which is by construction a trading day (it
/// comes from <c>LastSessionCloseAt</c> walking back to an actual session) — it can never be
/// entirely non-trading days. FX has no such guarantee: `fxCap` is one UTC calendar day behind `now`
/// with no session to anchor it, and Twelve Data's own FX weekend coverage is known to be
/// incomplete (tracker.md's refresh-catch-up entry measured 54 Fridays against only 35 Sundays on
/// file). A narrowed one-or-two-day FX range can therefore legitimately contain no bar at all, and
/// if Twelve Data answers that with an error payload rather than an empty list (unconfirmed — not
/// observed live, and not reproducible without calling the real API), this method reports it as a
/// failure exactly as any other FX error — see the FX loop below. This is a pre-existing, not a new,
/// exposure (a full multi-year range could always coincidentally end on such a gap too), simply more
/// likely once ranges narrow to a day or two; it never erodes recorded coverage (a failure never
/// touches <c>CoveredFrom</c>/<c>CoveredTo</c>), and D51's bounded, paced retry already exists to
/// stop a persistently "failing" pair from re-spending credits without bound.</para>
/// </summary>
public sealed class PriceBackfillService(
    IPortfolioDbContext db,
    IQuoteProviderRouter router,
    IFxRateProvider fxRateProvider,
    IMarketCalendar calendar,
    TimeProvider timeProvider,
    IOptions<PriceBackfillOptions> options,
    ITwelveDataCreditThrottle creditThrottle,
    ILogger<PriceBackfillService> logger) : IPriceBackfillService
{
    private const string ReportingCurrency = "USD";

    public async Task<PriceBackfillRunResult> RunIfDueAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        // D53: due-ness must be judged on the same SETTLED instant RunAsyncCore's per-market cap
        // uses (see the class remarks) — never raw `now` — or a market can go "due" against a
        // close that hasn't settled yet, get deferred by the cap, and still write a successful
        // RefreshRun row that then reads as covered on every later check. See CloseSettleDelay.
        var settledInstant = now - options.Value.CloseSettleDelay;

        var dueMarkets = new List<Market>();
        // D51: markets in dueMarkets AND retryMarkets get a narrowed run (only what's still
        // missing that market's latest close); markets in dueMarkets but NOT retryMarkets get the
        // full pass, exactly as before D51.
        var retryMarkets = new List<Market>();
        var skipped = new List<PriceBackfillMarketSkip>();

        foreach (var market in ProviderMarkets.All)
        {
            // Don't spend a call mid-session for THIS market — its own day's close is not on the
            // wire yet. NYSE being open must never gate SGX's due-ness, and vice versa (D47):
            // each market is evaluated entirely on its own session.
            if (calendar.IsOpen(market, now))
            {
                skipped.Add(new PriceBackfillMarketSkip(market, PriceBackfillSkipReason.SessionOpen));
                continue;
            }

            // A market publishes exactly one close per session, so "has a scheduled run completed
            // for this market since ITS OWN last close?" is both the due-ness check and, for free,
            // the once-per-session throttle the old global once-per-day latch used to provide
            // separately (and wrongly — see the class remarks). Self-correcting after downtime: on
            // startup, any market closed and uncovered since its own last close is immediately due.
            //
            // D51: "has a run completed" is no longer sufficient on its own — it must also have
            // SUCCEEDED, or this is exactly D47's trap one layer down (a completed-but-failed run
            // silently treated as coverage). Pulled back as a list, newest first, rather than just
            // MaxAsync(CompletedAt), because a retry decision also needs to know how many
            // BackfillScheduled attempts this market has already had since its own last close.
            //
            // D53: evaluated on `settledInstant`, not `now` — see this method's opening remarks and
            // the class-level D53 remarks above RunAsyncCore.
            var lastClose = calendar.LastSessionCloseAt(market, settledInstant);
            var runsSinceLastClose = await db.RefreshRuns
                .Where(r => r.Trigger == RefreshTrigger.BackfillScheduled && r.Market == market
                    && r.CompletedAt != null && r.CompletedAt >= lastClose)
                .OrderByDescending(r => r.CompletedAt)
                .ToListAsync(cancellationToken);

            if (runsSinceLastClose.Count == 0)
            {
                // No attempt at all yet since this close — the ordinary due-ness case, a full pass.
                dueMarkets.Add(market);
                continue;
            }

            var mostRecentRun = runsSinceLastClose[0];
            if (mostRecentRun.Success)
            {
                skipped.Add(new PriceBackfillMarketSkip(market, PriceBackfillSkipReason.AlreadyCoveredSinceLastClose));
                continue;
            }

            // The most recent attempt since this close failed. A retry is warranted, but bounded
            // (PriceBackfillOptions.MaxFailedRunRetriesPerClose) and paced
            // (PriceBackfillOptions.FailedRunRetryDelay) — see PriceBackfillService's class remarks
            // for why both exist.
            if (runsSinceLastClose.Count > options.Value.MaxFailedRunRetriesPerClose)
            {
                skipped.Add(new PriceBackfillMarketSkip(market, PriceBackfillSkipReason.RetriesExhausted));
                continue;
            }

            if (now - mostRecentRun.CompletedAt!.Value < options.Value.FailedRunRetryDelay)
            {
                skipped.Add(new PriceBackfillMarketSkip(market, PriceBackfillSkipReason.RetryPending));
                continue;
            }

            dueMarkets.Add(market);
            retryMarkets.Add(market);
        }

        if (dueMarkets.Count == 0)
        {
            return new PriceBackfillRunResult([], skipped, null);
        }

        var summary = await RunAsyncCore(
            RefreshTrigger.BackfillScheduled, dueMarkets, retryMarkets, restrictToAssetIds: null, restrictToCurrencies: null, cancellationToken);
        return new PriceBackfillRunResult(dueMarkets, skipped, summary);
    }

    /// <summary>Unconditional entry point — always a full pass. See <see cref="IPriceBackfillService"/>.
    /// D51's narrowed-retry path is reached only from <see cref="RunIfDueAsync"/>, via the private
    /// <see cref="RunAsyncCore"/> overload below; this public method never narrows, by design (the
    /// manual endpoint must stay a full pass, unchanged).</summary>
    public Task<PriceBackfillSummary> RunAsync(
        RefreshTrigger trigger, IReadOnlyCollection<Market> markets, CancellationToken cancellationToken) =>
        RunAsyncCore(trigger, markets, retryOnlyMarkets: [], restrictToAssetIds: null, restrictToCurrencies: null, cancellationToken);

    /// <summary>
    /// Refresh-catch-up feature: runs unconditionally for exactly <paramref name="assetIds"/> and
    /// <paramref name="currencies"/> — narrowed BEFORE the loop, the same shape D51's
    /// <c>retryOnlyMarkets</c> narrowing established, but decided by <c>IRefreshCatchUpService</c>'s
    /// planner rather than by "still missing the latest close". <paramref name="markets"/> must be
    /// exactly the markets in play (every asset's own market, plus every market requiring one of
    /// <paramref name="currencies"/> — see <c>RefreshCatchUpService</c>'s <c>MarketsInPlay</c>), so
    /// the per-market <see cref="Domain.Entities.RefreshRun"/> rows this writes land only on markets
    /// this run actually touched. Always tagged <see cref="RefreshTrigger.BackfillCatchUp"/>, which
    /// <see cref="RunIfDueAsync"/>'s due-ness query never looks at.
    /// </summary>
    public Task<PriceBackfillSummary> RunCatchUpAsync(
        IReadOnlyCollection<Market> markets,
        IReadOnlySet<int> assetIds,
        IReadOnlySet<string> currencies,
        CancellationToken cancellationToken) =>
        RunAsyncCore(RefreshTrigger.BackfillCatchUp, markets, retryOnlyMarkets: [], assetIds, currencies, cancellationToken);

    /// <summary>
    /// D51: <paramref name="retryOnlyMarkets"/> (a subset of <paramref name="markets"/>) narrows
    /// the run for exactly those markets to only the assets (and FX pairs) still missing that
    /// market's own latest close — see the filter applied to <c>assets</c> and
    /// <c>currenciesNeedingFx</c> below. A market in <paramref name="markets"/> but NOT in
    /// <paramref name="retryOnlyMarkets"/> gets the full pass, exactly as before D51 — this is what
    /// keeps the first scheduled pass per close (and the manual endpoint, which always passes an
    /// empty <paramref name="retryOnlyMarkets"/>) unchanged: a full pass is also what fills history
    /// for a newly recorded back-dated transaction on an asset whose latest close is already on
    /// file, which a "missing the latest close" filter alone would never pick up.
    ///
    /// <para>Refresh-catch-up: <paramref name="restrictToAssetIds"/>/<paramref name="restrictToCurrencies"/>,
    /// when non-null, narrow independently of <paramref name="retryOnlyMarkets"/> — see
    /// <see cref="RunCatchUpAsync"/>. The two narrowing mechanisms never combine in practice
    /// (<see cref="RunCatchUpAsync"/> always passes an empty <paramref name="retryOnlyMarkets"/>,
    /// and D51's own retry path always passes null for both), but nothing stops them from composing
    /// safely if that ever changed: both are applied as extra <c>Where</c> filters on top of
    /// whatever <paramref name="retryOnlyMarkets"/> already produced.</para>
    /// </summary>
    private async Task<PriceBackfillSummary> RunAsyncCore(
        RefreshTrigger trigger,
        IReadOnlyCollection<Market> markets,
        IReadOnlyCollection<Market> retryOnlyMarkets,
        IReadOnlySet<int>? restrictToAssetIds,
        IReadOnlySet<string>? restrictToCurrencies,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var today = ReportingClock.Today(timeProvider);
        var marketSet = markets.ToHashSet();
        var retryOnlyMarketSet = retryOnlyMarkets.ToHashSet();

        // D53: per-market cap — the latest date this run will ever request from a provider or
        // accept from one, for a given market. Computed once for every market in `markets` (a
        // superset of `retryOnlyMarkets`), since a FULL pass needs this exactly as much as a
        // narrowed retry does; see the per-asset loop and the FX loop below, both of which replace
        // the old blanket `to: today` with this. Subtracting CloseSettleDelay from `now` before
        // walking LastSessionCloseAt back (inside PriceBackfillCapCalculator.ComputeCap) is what
        // handles SGX's lunch break and closing-auction window for free — see the class remarks for
        // why. Applies to EVERY run, not just RunIfDueAsync's scheduled/retry paths: the manual
        // endpoint bypasses due-ness entirely but must never bypass "is this close even finished
        // settling," which is exactly the mid-session / lunch-break / closing-auction defect.
        //
        // Extracted to PriceBackfillCapCalculator so the refresh-catch-up planner
        // (RefreshCatchUpService) computes this identically — two independently-maintained copies of
        // "the latest date this run will ever request from a provider or accept from one" is exactly
        // the D7 drift trap; see PriceBackfillCapCalculator's own remarks.
        var capByMarket = markets.ToDictionary(
            m => m, m => PriceBackfillCapCalculator.ComputeCap(calendar, m, now, options.Value.CloseSettleDelay));

        // D53: FX has no market session to key a settle cap off — extracted to
        // PriceBackfillCapCalculator.ComputeFxCap alongside ComputeCap above, for the identical D7
        // reason: the refresh-catch-up planner must derive the exact same boundary, or it can queue
        // a catch-up that spends a credit and inserts nothing (see FxPairBackfillState's remarks for
        // the live-measured defect this closed). See ComputeFxCap's own doc comment for the full
        // "why UTC, why one day behind now" reasoning.
        var fxCap = PriceBackfillCapCalculator.ComputeFxCap(now);

        // D37: the budget is derived from what Twelve Data's persisted daily ledger says is
        // actually left today, not a hardcoded constant — a fixed budget smaller than one full
        // pass (20, against the 22 calls 21 assets + 1 FX pair needed) is exactly what left a
        // contiguous block of assets permanently unbackfilled. MaxProviderCallsPerRun still acts
        // as an explicit safety ceiling for a single run (its default no longer artificially
        // constrains below the derived figure — see PriceBackfillOptions).
        var creditStatus = await creditThrottle.GetStatusAsync(cancellationToken);
        var budget = Math.Min(options.Value.MaxProviderCallsPerRun, creditStatus.RemainingToday);
        var callsUsed = 0;

        var assetsProcessed = new List<string>();
        var assetsSkippedForBudget = new List<string>();
        var assetsFailed = new List<AssetBackfillFailure>();
        var assetsSkippedTodayNotClosed = new List<string>();
        // 2026-09-15: a narrowed request (see the class remarks) whose `from` landed beyond the
        // cap because BOTH coverage state and the newest actually-stored row/rate already reach
        // it — "already covered", never "not yet settled". Kept separate from
        // assetsSkippedTodayNotClosed on purpose; see that list's own doc comment and
        // PriceBackfillSummary.AssetsAlreadyCovered.
        var assetsAlreadyCovered = new List<string>();
        var assetsTruncated = new List<string>();
        var priceHistoryInserted = 0;
        var fxRateInserted = 0;
        // D51: which currencies' FX fetch failed, tracked separately from assetsFailed's flat
        // string-keyed list so the per-market Success attribution below can ask "did a currency
        // THIS market needs fail?" without parsing the "FX:USD/xxx" symbol format back apart.
        var failedFxCurrencies = new HashSet<string>();

        // Crypto is gain/loss only, by decision — it keeps no PriceHistory at all, so backfilling
        // it would spend provider rate limit on data nothing reads. See the Phase 6 decision.
        //
        // D47: further filtered to only the assets whose OWN market (via ProviderMarkets, the
        // single provider-to-market table — see its own doc comment and D7) is in scope for this
        // run. This is what makes an SGX-only run spend zero Twelve Data credits on NYSE assets —
        // they are never even loaded into the ordering/FX derivation below, not merely skipped
        // after the fact.
        var assets = await db.Assets
            .Where(a => a.IsActive && a.AssetClass == AssetClass.Stock)
            .ToListAsync(cancellationToken);

        assets = assets
            .Where(a => ProviderMarkets.For(a.QuoteProviderKind) is { } assetMarket && marketSet.Contains(assetMarket))
            .ToList();

        var marketByAssetSymbol = assets.ToDictionary(
            a => a.Symbol,
            a => ProviderMarkets.For(a.QuoteProviderKind)!.Value);

        // D51: captured BEFORE the retry-narrowing filter below reassigns `assets` — this is the
        // full market-scoped set, used to derive which currencies/markets are in play at all. FX
        // narrowing and asset-loop narrowing are independent decisions (an asset can be up to date
        // on its own close while its currency's FX row for that date genuinely is missing, or vice
        // versa), so neither derivation should be based on the other's narrowed output.
        var assetsInScope = assets;

        // D37: least-recently-backfilled first (nulls — never backfilled at all — first of all),
        // not the implicit clustered-index (Id) order EF/SQL Server return with no ORDER BY. That
        // stable ordering is exactly what made a budget-truncated run starve the SAME contiguous
        // block of assets on every single run, forever: with no ORDER BY, the query always came
        // back in Id order, so a budget of N always served the same first N assets and never
        // reached the rest. Sorting by "how stale is this asset's history" instead means whichever
        // assets fell short of the budget last time are first in line next time.
        var lastBackfilledByAsset = await db.PriceHistories
            .GroupBy(p => p.AssetId)
            .Select(g => new { AssetId = g.Key, LastDate = g.Max(p => p.Date) })
            .ToDictionaryAsync(x => x.AssetId, x => x.LastDate, cancellationToken);

        // D51: retries fetch only what is missing. For a market in retryOnlyMarkets, an asset is
        // excluded from this run entirely when its newest stored PriceHistory date already covers
        // that market's own last session close — re-fetching it would just re-confirm a close
        // already on file, spending a credit for nothing. A market NOT in retryOnlyMarkets (the
        // ordinary first pass per close, and every manual run) is untouched by this filter — a full
        // pass must still fetch every in-scope asset regardless of what it already has on file,
        // because it is also what fills history for a newly recorded back-dated transaction on an
        // asset whose latest close happens to already be on file.
        if (retryOnlyMarketSet.Count > 0)
        {
            // D53: reuses `capByMarket` (computed on the settled instant, once, for every market in
            // scope) rather than a second, separately-computed "last close local date" keyed on raw
            // `now` — the two must never drift apart, on pain of D51's trap one layer in (see the
            // class remarks).
            assets = assets
                .Where(a =>
                {
                    var assetMarket = marketByAssetSymbol[a.Symbol];
                    if (!retryOnlyMarketSet.Contains(assetMarket))
                    {
                        return true; // this asset's market isn't being retried this run — full pass
                    }

                    var requiredDate = capByMarket[assetMarket];
                    var alreadyHasLatestClose =
                        lastBackfilledByAsset.TryGetValue(a.Id, out var lastDate) && lastDate >= requiredDate;
                    return !alreadyHasLatestClose;
                })
                .ToList();
        }

        // Refresh-catch-up: narrowed independently of retryOnlyMarketSet above (see RunAsyncCore's
        // class remarks) — IRefreshCatchUpService's planner has already decided exactly which
        // assets are missing coverage, so this run touches only those, never every in-scope asset
        // the way an ordinary full pass or D51 retry does.
        if (restrictToAssetIds is not null)
        {
            assets = assets.Where(a => restrictToAssetIds.Contains(a.Id)).ToList();
        }

        assets = assets
            .OrderBy(a => lastBackfilledByAsset.TryGetValue(a.Id, out var lastDate) ? lastDate : DateOnly.MinValue)
            .ThenBy(a => a.Id) // stable, deterministic tie-break for assets backfilled on the same date
            .ToList();

        var earliestTradeDateByAsset = await db.Transactions
            .GroupBy(t => t.AssetId)
            .Select(g => new { AssetId = g.Key, Earliest = g.Min(t => t.TradeDate) })
            .ToDictionaryAsync(x => x.AssetId, x => x.Earliest, cancellationToken);

        // Refresh-catch-up: every asset's coverage state, upserted in the per-asset loop below for
        // EVERY attempt this run makes (whatever the trigger — see AssetPriceHistoryState's own
        // remarks), so IRefreshCatchUpService's planner can tell a permanent gap (the provider was
        // successfully asked, nothing more will ever come back) apart from genuinely missing data.
        var priceHistoryStatesByAsset = await db.AssetPriceHistoryStates.ToDictionaryAsync(s => s.AssetId, cancellationToken);

        // Derived up front, before either loop spends a call, so the FX loop below can run first
        // without waiting on the asset loop to discover which currencies are in play. Derived from
        // `assetsInScope` (the full market-scoped set, NOT the retry-narrowed `assets`), so an
        // SGX-only run only ever asks for USD/SGD, not any currency pair only an out-of-scope NYSE
        // asset would need — and so FX narrowing below (also D51) is judged on its own criterion,
        // not accidentally starved by which individual assets a retry happened to exclude.
        var currenciesNeedingFx = assetsInScope
            .Where(a => earliestTradeDateByAsset.ContainsKey(a.Id) && a.Currency != ReportingCurrency)
            .Select(a => a.Currency)
            .ToHashSet();

        // Refresh-catch-up: narrows to exactly the currencies the planner decided are missing —
        // independent of restrictToAssetIds above, since a currency can be in scope here purely on
        // the FX upper-bound rule with no asset of that currency actually being fetched this run
        // (see IRefreshCatchUpService's remarks on the deliberate no-lower-bound-alone FX check).
        if (restrictToCurrencies is not null)
        {
            currenciesNeedingFx = currenciesNeedingFx.Where(restrictToCurrencies.Contains).ToHashSet();
        }

        // Which market(s) need each non-USD currency — used both for D51's per-market Success
        // attribution below (a market's RefreshRun row must fail if an FX pair only IT needs
        // failed) and for D51's FX retry narrowing immediately below.
        var marketsByCurrency = assetsInScope
            .Where(a => earliestTradeDateByAsset.ContainsKey(a.Id) && a.Currency != ReportingCurrency)
            .GroupBy(a => a.Currency)
            .ToDictionary(g => g.Key, g => g.Select(a => marketByAssetSymbol[a.Symbol]).ToHashSet());

        // D51: the FX equivalent of the asset-loop narrowing above. A currency is excluded from
        // this run only when EVERY market that needs it is being retried (a currency needed by a
        // full-pass market must still be fetched in full, unchanged) AND the newest stored FxRate
        // for it already covers the latest of those markets' own last close.
        if (retryOnlyMarketSet.Count > 0 && currenciesNeedingFx.Count > 0)
        {
            // D53: reuses `capByMarket` (settled-instant based) rather than a second computation on
            // raw `now` — see the same reasoning on the asset-loop narrowing above.
            foreach (var currency in currenciesNeedingFx.ToList())
            {
                var requiringMarkets = marketsByCurrency[currency];
                if (!requiringMarkets.All(retryOnlyMarketSet.Contains))
                {
                    continue; // at least one requiring market is a full pass — do not narrow
                }

                var requiredDate = requiringMarkets.Max(m => capByMarket[m]);
                var newestStoredFxDate = await db.FxRates
                    .Where(f => f.Base == ReportingCurrency && f.Quote == currency)
                    .Select(f => (DateOnly?)f.Date)
                    .MaxAsync(cancellationToken);

                if (newestStoredFxDate is { } stored && stored >= requiredDate)
                {
                    currenciesNeedingFx.Remove(currency);
                }
            }
        }

        // Refresh-catch-up: every FX pair's coverage state, upserted in the loop below for EVERY
        // attempt (whatever the trigger) — mirrors priceHistoryStatesByAsset below exactly, but
        // keyed by currency rather than asset id. See FxPairBackfillState's own remarks for why FX
        // needs this in addition to the asset leg's own state (Twelve Data's incomplete Sunday
        // coverage, and the SGX-evening cap mismatch).
        var fxPairStatesByCurrency = await db.FxPairBackfillStates
            .Where(s => s.Base == ReportingCurrency)
            .ToDictionaryAsync(s => s.Quote, cancellationToken);

        // FX runs before the per-asset price-history loop, and gets first claim on the shared
        // call budget, even though it appears second in PriceBackfillSummary's field order. This
        // is deliberate, not an oversight: a missing FX rate throws inside FxRateResolver and 500s
        // every USD-reporting endpoint for every holding, USD-denominated or not, while a missing
        // price-history point just leaves a gap in one asset's chart. FX is a hard prerequisite;
        // price history is degradable. Running the asset loop first (as this used to) let it burn
        // the entire budget on Twelve Data's 8-req/min limit before FX ever got a turn, so the one
        // FX call at the end was reliably 429'd once more than ~8 stocks were held.
        foreach (var currency in currenciesNeedingFx)
        {
            // The earliest date any asset in this currency needs a converted value. Derived from
            // `assetsInScope`, not the retry-narrowed `assets` — an FX call still fetched (i.e. not
            // skipped by the narrowing above) must cover the full range every in-scope asset in
            // this currency needs, not just the narrowed subset a retry happens to be fetching
            // prices for.
            var earliestNeeded = assetsInScope
                .Where(a => a.Currency == currency && earliestTradeDateByAsset.ContainsKey(a.Id))
                .Select(a => earliestTradeDateByAsset[a.Id])
                .DefaultIfEmpty(today)
                .Min();
            var from = earliestNeeded;

            // 2026-09-15: narrow `from` to the tail that is missing — see the class remarks. Only
            // on BackfillCatchUp, or when every market that needs this currency is a D51 narrowed
            // retry market (mirrors the pre-filter above, which decides whether to fetch AT ALL —
            // this decides, given that it IS being fetched, how far back to start). Narrowing
            // requires BOTH the state's own `CoveredTo` AND the newest actually-stored `FxRate`
            // for this pair to corroborate each other — state alone is not proof the cap-day rate
            // is on file (a coordinator review caught this: a run can succeed and record
            // `CoveredTo = cap` even though the provider's response didn't include that specific
            // date's rate). No stored rate at all for this pair means nothing to narrow against,
            // so `from` stays the full range.
            var requiringMarkets = marketsByCurrency.TryGetValue(currency, out var rm) ? rm : [];
            var fxNarrowingApplies = trigger == RefreshTrigger.BackfillCatchUp
                || (retryOnlyMarketSet.Count > 0 && requiringMarkets.Count > 0 && requiringMarkets.All(retryOnlyMarketSet.Contains));
            var fxAlreadyCovered = false;
            if (fxNarrowingApplies
                && fxPairStatesByCurrency.TryGetValue(currency, out var existingFxState)
                && existingFxState.CoveredFrom is { } fxCoveredFrom
                && existingFxState.CoveredTo is { } fxCoveredTo
                && fxCoveredFrom <= earliestNeeded)
            {
                var newestStoredFxDate = await db.FxRates
                    .Where(f => f.Base == ReportingCurrency && f.Quote == currency)
                    .Select(f => (DateOnly?)f.Date)
                    .MaxAsync(cancellationToken);

                if (newestStoredFxDate is { } storedFxDate)
                {
                    var boundedCoveredTo = fxCoveredTo < storedFxDate ? fxCoveredTo : storedFxDate;
                    var narrowedFrom = boundedCoveredTo.AddDays(1);
                    if (narrowedFrom > from)
                    {
                        from = narrowedFrom;
                        fxAlreadyCovered = from > fxCap;
                    }
                }
            }

            if (from > fxCap)
            {
                if (fxAlreadyCovered)
                {
                    // 2026-09-15: the narrowed `from` outran `fxCap` because both state AND the
                    // stored FxRate table already cover it, not because the day hasn't settled —
                    // see AssetsAlreadyCovered's own doc comment for why this must not share
                    // AssetsSkippedTodayNotClosed.
                    assetsAlreadyCovered.Add($"FX:{ReportingCurrency}/{currency}");
                }
                else
                {
                    // D53: same "deferred, not yet settled" bucket as the per-asset guard below —
                    // `from` is beyond the FX day-cap (see `fxCap` above), so there is nothing to
                    // fetch yet.
                    assetsSkippedTodayNotClosed.Add($"FX:{ReportingCurrency}/{currency}");
                }

                continue;
            }

            if (callsUsed >= budget)
            {
                assetsSkippedForBudget.Add($"FX:{ReportingCurrency}/{currency}");
                continue;
            }

            FxHistoryFetchResult fxResult;
            try
            {
                fxResult = await fxRateProvider.GetHistoryAsync(ReportingCurrency, currency, from, fxCap, cancellationToken);
                callsUsed++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Historical FX backfill failed for {Base}/{Quote}",
                    ReportingCurrency,
                    currency);
                RecordFxPairState(fxPairStatesByCurrency, currency, now, success: false, ex.Message);
                assetsFailed.Add(new AssetBackfillFailure($"FX:{ReportingCurrency}/{currency}", ex.Message));
                failedFxCurrencies.Add(currency);
                continue;
            }

            if (!fxResult.Success)
            {
                // Must land in assetsFailed, not silently pass through as zero rates inserted —
                // this is the false-success bug: a 429 that returns an empty list here is
                // indistinguishable from "the provider genuinely has no rates for this range"
                // unless the failure is surfaced explicitly.
                logger.LogWarning(
                    "Historical FX backfill for {Base}/{Quote} failed: {Error}",
                    ReportingCurrency,
                    currency,
                    fxResult.Error);
                var fxError = fxResult.Error ?? "Provider reported failure without a message.";
                RecordFxPairState(fxPairStatesByCurrency, currency, now, success: false, fxError);
                assetsFailed.Add(new AssetBackfillFailure($"FX:{ReportingCurrency}/{currency}", fxError));
                failedFxCurrencies.Add(currency);
                continue;
            }

            var existingFxDates = await db.FxRates
                .Where(f => f.Base == ReportingCurrency && f.Quote == currency && f.Date >= from && f.Date <= fxCap)
                .Select(f => f.Date)
                .ToListAsync(cancellationToken);
            var existingFxDateSet = existingFxDates.ToHashSet();

            foreach (var point in fxResult.Points)
            {
                // D53: never write a rate beyond the day-cap. This is NOT merely defence in depth —
                // TwelveDataFxProvider deliberately sends `end_date = fxCap.AddDays(1)` because
                // Twelve Data's own end-date semantics are unconfirmed and live evidence points at
                // exclusive, so under an inclusive reading this filter is the ONLY thing standing
                // between a legitimately-returned `fxCap + 1` rate and CLAUDE.md's "a live spot rate
                // is never written into FxRates" rule. See this class's D53 remarks.
                if (point.Date > fxCap)
                {
                    continue;
                }

                if (!existingFxDateSet.Add(point.Date))
                {
                    continue;
                }

                db.AddFxRate(new FxRate
                {
                    Date = point.Date,
                    Base = ReportingCurrency,
                    Quote = currency,
                    Rate = point.Rate,
                });
                fxRateInserted++;
            }

            // Refresh-catch-up: CoveredFrom/CoveredTo are the REQUESTED from/fxCap, not derived
            // from fxResult.Points — mirrors RecordPriceHistoryState's identical reasoning below.
            RecordFxPairState(fxPairStatesByCurrency, currency, now, success: true, error: null, coveredFrom: from, coveredTo: fxCap);
        }

        foreach (var asset in assets)
        {
            if (!earliestTradeDateByAsset.TryGetValue(asset.Id, out var earliestTrade))
            {
                // No transactions yet for this asset — nothing to backfill against.
                continue;
            }

            var market = marketByAssetSymbol[asset.Symbol];
            var cap = capByMarket[market];
            var from = earliestTrade;

            // 2026-09-15: narrow `from` to the tail that is missing — see the class remarks. Only
            // on BackfillCatchUp, or when this asset's own market is a D51 narrowed retry market:
            // the first scheduled pass per close and the manual endpoint must stay a full pass,
            // unchanged. Narrowing requires BOTH the state's own `CoveredTo` AND the newest
            // actually-stored `PriceHistory` row (`lastBackfilledByAsset`, the same source D51's
            // own retry SELECTION already trusts) to corroborate each other — state alone is not
            // proof the cap-day bar is on file (a coordinator review caught this: a run can
            // succeed and record `CoveredTo = cap` even though the provider's response didn't
            // include that specific date's bar, silently stopping the retry of a close that is
            // still missing). No stored row at all for this asset means nothing to narrow against,
            // so `from` stays the full range. A back-dated transaction that moved `earliestTrade`
            // before the state's own CoveredFrom also falls back to the full range — the state
            // does not prove that newly-relevant early history was ever asked for.
            var narrowingApplies = trigger == RefreshTrigger.BackfillCatchUp || retryOnlyMarketSet.Contains(market);
            var alreadyCovered = false;
            if (narrowingApplies
                && priceHistoryStatesByAsset.TryGetValue(asset.Id, out var existingState)
                && existingState.CoveredFrom is { } stateCoveredFrom
                && existingState.CoveredTo is { } stateCoveredTo
                && stateCoveredFrom <= earliestTrade
                && lastBackfilledByAsset.TryGetValue(asset.Id, out var newestStoredDate))
            {
                var boundedCoveredTo = stateCoveredTo < newestStoredDate ? stateCoveredTo : newestStoredDate;
                var narrowedFrom = boundedCoveredTo.AddDays(1);
                if (narrowedFrom > from)
                {
                    from = narrowedFrom;
                    // A narrowed `from` beyond `cap` means BOTH state AND the stored PriceHistory
                    // table already cover this market's settled cap — "already covered", not "not
                    // yet settled" (see the class remarks and AssetsAlreadyCovered's own doc
                    // comment).
                    alreadyCovered = from > cap;
                }
            }

            if (from > cap)
            {
                if (alreadyCovered)
                {
                    assetsAlreadyCovered.Add(asset.Symbol);
                    continue;
                }

                // D53: `from` is beyond this asset's own market's settled cap — either because it
                // was bought today (the original guard this replaces) or because that market's most
                // recent session hasn't finished SETTLING yet (mid-session, SGX's lunch break, or
                // SGX's closing-auction window — see `capByMarket` and the class remarks above). An
                // equity provider cannot return a genuine daily close for a range like this — Twelve
                // Data returns HTTP 400 for a same-day range, every time — so short-circuit before
                // spending a call: raising MaxProviderCallsPerRun would not fix a guaranteed
                // failure, and this must never be reported as a budget skip or a failure — it isn't
                // one, it is simply premature. A later run, once `cap` has advanced past `from`,
                // picks this asset up with a normal multi-day range.
                logger.LogInformation(
                    "Historical price backfill deferred for asset {AssetId} ({Symbol}): earliest needed date {From} is beyond {Market}'s settled cap {Cap}",
                    asset.Id,
                    asset.Symbol,
                    from,
                    market,
                    cap);
                assetsSkippedTodayNotClosed.Add(asset.Symbol);
                continue;
            }

            if (callsUsed >= budget)
            {
                assetsSkippedForBudget.Add(asset.Symbol);
                continue;
            }

            HistoryFetchResult historyResult;
            try
            {
                var provider = router.GetProvider(asset);
                historyResult = await provider.GetHistoryAsync(asset, from, cap, cancellationToken);
                callsUsed++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Historical price backfill failed for asset {AssetId} ({Symbol})",
                    asset.Id,
                    asset.Symbol);
                RecordPriceHistoryState(priceHistoryStatesByAsset, asset.Id, now, success: false, ex.Message);
                assetsFailed.Add(new AssetBackfillFailure(asset.Symbol, ex.Message));
                continue;
            }

            if (!historyResult.Success)
            {
                logger.LogWarning(
                    "Historical price backfill for asset {AssetId} ({Symbol}) failed: {Error}",
                    asset.Id,
                    asset.Symbol,
                    historyResult.Error);
                var error = historyResult.Error ?? "Provider reported failure without a message.";
                RecordPriceHistoryState(priceHistoryStatesByAsset, asset.Id, now, success: false, error);
                assetsFailed.Add(new AssetBackfillFailure(asset.Symbol, error));
                continue;
            }

            if (historyResult.Truncated)
            {
                // The series starts later than requested by provider policy, not by accident —
                // record it so a caller (and eventually Phase 6's cost-vs-market-value series)
                // can tell the two apart instead of silently building on a gap.
                logger.LogWarning(
                    "Historical price backfill for asset {AssetId} ({Symbol}) truncated: requested from {RequestedFrom}, provider limited to {EffectiveFrom}",
                    asset.Id,
                    asset.Symbol,
                    historyResult.RequestedFrom,
                    historyResult.EffectiveFrom);
                assetsTruncated.Add(
                    $"{asset.Symbol}: requested from {historyResult.RequestedFrom:yyyy-MM-dd}, provider limited to {historyResult.EffectiveFrom:yyyy-MM-dd}");
            }

            var existingDates = await db.PriceHistories
                .Where(p => p.AssetId == asset.Id && p.Date >= historyResult.EffectiveFrom && p.Date <= cap)
                .Select(p => p.Date)
                .ToListAsync(cancellationToken);
            var existingDateSet = existingDates.ToHashSet();

            foreach (var point in historyResult.Points)
            {
                // D53: never insert a point beyond this market's own settled cap. This is NOT
                // merely defence in depth for a Twelve Data asset — TwelveDataQuoteProvider
                // deliberately sends `end_date = cap.AddDays(1)` because Twelve Data's own end-date
                // semantics are unconfirmed and live evidence points at exclusive (see the class
                // remarks); under an inclusive reading, this filter is the ONLY thing stopping a
                // legitimately-returned `cap + 1` bar (i.e. tomorrow's, or today's still-unsettled
                // one) from reaching PriceHistory. It genuinely is defence in depth for Yahoo, whose
                // chart endpoint has no documented guarantee that `period2` is honoured exactly —
                // either way, a close for a session that has not finished settling must never reach
                // PriceHistory, regardless of why the provider handed it back.
                if (point.Date > cap)
                {
                    continue;
                }

                if (!existingDateSet.Add(point.Date))
                {
                    continue;
                }

                db.AddPriceHistory(new PriceHistory
                {
                    AssetId = asset.Id,
                    Date = point.Date,
                    Close = point.Close,
                    Currency = point.Currency,
                });
                priceHistoryInserted++;
            }

            // Refresh-catch-up: CoveredFrom records the REQUESTED `from`, not
            // historyResult.EffectiveFrom — even when Truncated, re-asking for the same `from`
            // will never return more (truncation is provider policy, see AssetPriceHistoryState's
            // own remarks), so the planner must not keep treating this as missing forever.
            // CoveredTo is this market's own requested cap, not the last point actually returned —
            // a provider that legitimately has no bar for a particular date (a market holiday it
            // knows about but this calendar doesn't model) must not read as "still missing" either.
            RecordPriceHistoryState(priceHistoryStatesByAsset, asset.Id, now, success: true, error: null, coveredFrom: from, coveredTo: cap);
            assetsProcessed.Add(asset.Symbol);
        }

        // Audit row alongside the quote-refresh RefreshRuns (see RefreshTrigger) — one row PER
        // MARKET covered by this run (D47), not one row for the whole run, so RunIfDueAsync's
        // per-market due-ness query (Trigger + Market) has something to read for each exchange
        // independently. SymbolsRefreshed is counted per market (an asset's own processed/failed
        // outcome is shared across the run, but how many symbols a given market's row can claim
        // credit for is market-specific — see D45's reminder that internal bookkeeping numbers
        // need the same "which one actually happened" care as outward-facing DTOs).
        //
        // D51: Success is now computed PER MARKET too, not `assetsFailed.Count == 0` stamped
        // identically on every market's row. A market's row fails iff one of ITS OWN assets failed,
        // or an FX pair only ITS OWN assets need failed — NYSE's row must never read false because
        // Z74's SGD conversion failed, and SGX's row must never read false because an unrelated
        // NYSE asset failed. ErrorMessage stays whole-run text (not scoped per market) — it is a
        // human-facing diagnostic string, never machine-read (RefreshRun.Success is the only field
        // RunIfDueAsync's gate consults), so one shared summary is simplest and loses nothing.
        var runSummaryText = BuildRunSummary(assetsSkippedForBudget, assetsFailed, assetsSkippedTodayNotClosed, assetsTruncated, assetsAlreadyCovered);
        var completedAt = timeProvider.GetUtcNow();
        foreach (var market in markets)
        {
            var symbolsForMarket = assetsProcessed.Count(symbol =>
                marketByAssetSymbol.TryGetValue(symbol, out var assetMarket) && assetMarket == market);

            var assetFailureForThisMarket = assetsFailed.Any(f =>
                marketByAssetSymbol.TryGetValue(f.Symbol, out var assetMarket) && assetMarket == market);
            var fxFailureForThisMarket = failedFxCurrencies.Any(currency =>
                marketsByCurrency.TryGetValue(currency, out var requiringMarkets) && requiringMarkets.Contains(market));

            db.AddRefreshRun(new RefreshRun
            {
                Trigger = trigger,
                AssetClass = AssetClass.Stock,
                Market = market,
                StartedAt = now,
                CompletedAt = completedAt,
                Success = !assetFailureForThisMarket && !fxFailureForThisMarket,
                ErrorMessage = runSummaryText,
                SymbolsRefreshed = symbolsForMarket,
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        return new PriceBackfillSummary(
            assetsProcessed,
            assetsSkippedForBudget,
            assetsFailed,
            assetsSkippedTodayNotClosed,
            priceHistoryInserted,
            fxRateInserted,
            callsUsed,
            assetsTruncated,
            assetsAlreadyCovered);
    }

    /// <summary>
    /// Refresh-catch-up feature: upserts <see cref="Domain.Entities.AssetPriceHistoryState"/> for
    /// ONE asset's attempt this run — called for every asset actually attempted (never for one
    /// skipped for budget or deferred by <c>capByMarket</c>, which are not attempts at all).
    /// <paramref name="coveredFrom"/>/<paramref name="coveredTo"/> are supplied only on success and
    /// are the range THIS attempt requested, not what came back — see the call site's own remarks.
    /// A failure updates <see cref="Domain.Entities.AssetPriceHistoryState.LastAttemptedAt"/>/
    /// <c>LastRunSuccess</c>/<c>LastError</c> but must never touch <c>CoveredFrom</c>/<c>CoveredTo</c>:
    /// shrinking known coverage on a failed retry would make the catch-up planner re-fetch an asset
    /// it has already, successfully, asked about.
    ///
    /// <para><b>2026-09-15: a success takes the UNION of the existing and newly-requested range,
    /// never overwrites it.</b> A narrowed request's own <paramref name="coveredFrom"/> is the
    /// state's own prior <c>CoveredTo + 1</c> — writing it straight through would move
    /// <c>CoveredFrom</c> LATER, and the very next planner check
    /// (<c>RefreshCatchUpService</c>'s <c>coveredFrom &lt;= firstTrade</c> test) would then read the
    /// earlier history as newly uncovered and re-request the full range on the next run. Taking
    /// <c>min(existing CoveredFrom, coveredFrom)</c>/<c>max(existing CoveredTo, coveredTo)</c>
    /// instead is a no-op for a full-range success (its own range is already a superset of whatever
    /// was covered before) and is exactly what lets a narrowed success extend <c>CoveredTo</c>
    /// without ever losing the earlier <c>CoveredFrom</c> a prior full pass established. A
    /// non-contiguous union (a gap between the old and new ranges) does not occur by construction:
    /// every caller requests either the full range from the asset's own lower bound, or resumes at
    /// exactly <c>CoveredTo + 1</c> — never a range starting strictly later than that.</para>
    /// </summary>
    private void RecordPriceHistoryState(
        Dictionary<int, AssetPriceHistoryState> statesByAsset,
        int assetId,
        DateTimeOffset now,
        bool success,
        string? error,
        DateOnly? coveredFrom = null,
        DateOnly? coveredTo = null)
    {
        if (!statesByAsset.TryGetValue(assetId, out var state))
        {
            state = new AssetPriceHistoryState { AssetId = assetId };
            db.AddAssetPriceHistoryState(state);
            statesByAsset[assetId] = state;
        }

        state.LastAttemptedAt = now;
        state.LastRunSuccess = success;
        state.LastError = error is { Length: > 2000 } ? error[..2000] : error;

        if (success)
        {
            state.LastSuccessAt = now;
            state.CoveredFrom = MinDate(state.CoveredFrom, coveredFrom);
            state.CoveredTo = MaxDate(state.CoveredTo, coveredTo);
        }
    }

    /// <summary>
    /// Refresh-catch-up feature: upserts <see cref="Domain.Entities.FxPairBackfillState"/> for ONE
    /// currency pair's attempt this run — the FX mirror of <see cref="RecordPriceHistoryState"/>,
    /// same rules: <paramref name="coveredFrom"/>/<paramref name="coveredTo"/> supplied only on
    /// success, a failure must never touch them, and a success takes the union of the existing and
    /// newly-requested range rather than overwriting it — see
    /// <see cref="RecordPriceHistoryState"/>'s 2026-09-15 remarks for why.
    /// </summary>
    private void RecordFxPairState(
        Dictionary<string, FxPairBackfillState> statesByCurrency,
        string currency,
        DateTimeOffset now,
        bool success,
        string? error,
        DateOnly? coveredFrom = null,
        DateOnly? coveredTo = null)
    {
        if (!statesByCurrency.TryGetValue(currency, out var state))
        {
            state = new FxPairBackfillState { Base = ReportingCurrency, Quote = currency };
            db.AddFxPairBackfillState(state);
            statesByCurrency[currency] = state;
        }

        state.LastAttemptedAt = now;
        state.LastRunSuccess = success;
        state.LastError = error is { Length: > 2000 } ? error[..2000] : error;

        if (success)
        {
            state.LastSuccessAt = now;
            state.CoveredFrom = MinDate(state.CoveredFrom, coveredFrom);
            state.CoveredTo = MaxDate(state.CoveredTo, coveredTo);
        }
    }

    /// <summary>The earlier of two nullable dates, treating null as "no bound yet" rather than as
    /// the smallest possible value — <c>null</c> only when BOTH inputs are null. Shared by
    /// <see cref="RecordPriceHistoryState"/>/<see cref="RecordFxPairState"/>'s coverage-union fix;
    /// see their 2026-09-15 remarks.</summary>
    private static DateOnly? MinDate(DateOnly? a, DateOnly? b) =>
        a is null ? b : b is null ? a : a < b ? a : b;

    /// <summary>The later of two nullable dates, same null handling as <see cref="MinDate"/>.</summary>
    private static DateOnly? MaxDate(DateOnly? a, DateOnly? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;

    /// <summary>Best-effort backfill has no single pass/fail flag — a skipped or truncated asset
    /// is not an error, just something worth a human noticing in the audit trail. Kept separated
    /// by reason, not merged into one bag of strings, for the same reason the summary DTO keeps
    /// them separate: "budget", "failed", and "not yet settled" (D53 — today, or a market whose
    /// last session hasn't finished settling) call for different human responses.</summary>
    private static string? BuildRunSummary(
        IReadOnlyList<string> skippedForBudget,
        IReadOnlyList<AssetBackfillFailure> failed,
        IReadOnlyList<string> skippedTodayNotClosed,
        IReadOnlyList<string> truncated,
        IReadOnlyList<string> alreadyCovered)
    {
        var parts = new List<string>();
        if (skippedForBudget.Count > 0)
        {
            parts.Add($"{skippedForBudget.Count} skipped for budget: {string.Join(", ", skippedForBudget)}");
        }

        if (failed.Count > 0)
        {
            parts.Add($"{failed.Count} failed: {string.Join(", ", failed.Select(f => $"{f.Symbol} ({f.Error})"))}");
        }

        if (skippedTodayNotClosed.Count > 0)
        {
            parts.Add($"{skippedTodayNotClosed.Count} deferred (not yet settled): {string.Join(", ", skippedTodayNotClosed)}");
        }

        if (truncated.Count > 0)
        {
            parts.Add($"{truncated.Count} truncated: {string.Join(", ", truncated)}");
        }

        if (alreadyCovered.Count > 0)
        {
            // 2026-09-15: benign — a narrowed (BackfillCatchUp/D51-retry) request found BOTH the
            // state and the stored data already reaching the cap, before spending a call.
            // Reported for visibility, not because it needs a human response; see
            // PriceBackfillSummary.AssetsAlreadyCovered.
            parts.Add($"{alreadyCovered.Count} already covered: {string.Join(", ", alreadyCovered)}");
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }
}
