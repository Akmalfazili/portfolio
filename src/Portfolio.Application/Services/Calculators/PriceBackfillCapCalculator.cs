using Portfolio.Application.Abstractions;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services.Calculators;

/// <summary>
/// D53's "settled cap" computation, extracted to one place so <c>PriceBackfillService.RunAsyncCore</c>
/// and the refresh-catch-up planner (<c>RefreshCatchUpService</c>) can never drift apart — see
/// <c>PriceBackfillService</c>'s own class remarks for the full D53 history of why this exact
/// computation exists. Two independently-maintained copies of "the latest date this run will ever
/// request from a provider or accept from one" is exactly the kind of drift D7 was raised about:
/// if the planner ever decided an asset was missing using a cap one day ahead of what
/// <c>RunAsyncCore</c> would actually accept, the catch-up leg it queues would come back Truncated
/// or with nothing new, in a loop, on every click.
/// </summary>
public static class PriceBackfillCapCalculator
{
    /// <summary>The latest date <paramref name="market"/>'s settled session close can be trusted as
    /// of <paramref name="now"/> — <c>calendar.LocalDateOn(market, calendar.LastSessionCloseAt(market,
    /// now - closeSettleDelay))</c>. See <see cref="PriceBackfillOptions.CloseSettleDelay"/> for why
    /// the delay is subtracted from <paramref name="now"/> BEFORE walking <c>LastSessionCloseAt</c>
    /// back, not after.</summary>
    public static DateOnly ComputeCap(
        IMarketCalendar calendar, Market market, DateTimeOffset now, TimeSpan closeSettleDelay)
    {
        var settledInstant = now - closeSettleDelay;
        return calendar.LocalDateOn(market, calendar.LastSessionCloseAt(market, settledInstant));
    }

    /// <summary>
    /// FX has no market session to key a settle cap off — Twelve Data's <c>/time_series</c> is the
    /// exact same endpoint, with the exact same query construction, for a currency pair as for an
    /// equity, so whether it withholds a still-updating "today" forex bar the way it apparently does
    /// for equities cannot be established from the client code alone: forex trades continuously five
    /// days a week with no discrete session close for a "the day is over" check to key off, unlike
    /// NYSE/SGX. Capping to one UTC calendar day behind <paramref name="now"/> is the conservative
    /// choice either way — it can never write today's still-updating rate as a "close" (see
    /// CLAUDE.md's live-spot-into-FxRates rule), at the cost of an FX row landing up to a day later
    /// than it might safely have been able to. Deliberately UTC: Twelve Data's own daily credit
    /// ledger already keys off UTC midnight for the same reason (see <c>ReportingClock</c>'s remarks
    /// on why that one field stays off this codebase's usual SGT "today"), and there is no SGT-based
    /// session boundary here to justify departing from it.
    ///
    /// <para>Extracted alongside <see cref="ComputeCap"/> for the identical D7 reason: both
    /// <c>PriceBackfillService.RunAsyncCore</c> and the refresh-catch-up planner
    /// (<c>RefreshCatchUpService</c>) must derive the exact same "will this run ever request/accept a
    /// rate for this date" boundary, or the planner can queue a catch-up that spends a credit and
    /// inserts nothing — see <c>FxPairBackfillState</c>'s own remarks for the live-measured defect
    /// this closed (SGX-evening hours, and Twelve Data's incomplete Sunday FX coverage).</para>
    /// </summary>
    public static DateOnly ComputeFxCap(DateTimeOffset now) => DateOnly.FromDateTime(now.UtcDateTime).AddDays(-1);
}
