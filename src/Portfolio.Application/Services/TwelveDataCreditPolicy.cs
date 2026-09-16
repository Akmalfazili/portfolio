namespace Portfolio.Application.Services;

/// <summary>
/// Shared facts about Twelve Data's free tier, referenced by every component that spends or
/// paces its credits — <see cref="TwelveDataCreditThrottle"/>, the quote and history providers in
/// <c>Portfolio.Infrastructure</c>, <see cref="PriceRefreshService"/> and
/// <see cref="PriceBackfillService"/>. One place for the numbers so they cannot drift apart the
/// way the "req/min" wording in CLAUDE.md drifted from the truth and produced D38.
///
/// <para><b>D38, corrected:</b> the per-minute limit is credit-denominated, not
/// request-denominated. A single <c>/quote</c> request carrying N symbols spends N credits in one
/// shot — a batch of 21 symbols 429s immediately even though it is the first (and only) request
/// in its minute, because it alone exceeds <see cref="PerMinuteCreditLimit"/>. A <c>/time_series</c>
/// call (used by history/backfill) always costs exactly 1 credit, regardless of the date range
/// requested — <b>this was the suspected culprit for D39 and was measured, not assumed, on
/// 2026-08-24</b>: a 5-row call and a 1,668-row call (AAPL, 2020-01-01 to 2026-08-24, more rows
/// than the 837-row case that raised the suspicion) both moved Twelve Data's own
/// <c>GET /api_usage</c> <c>daily_usage</c> counter by exactly 1. The output-size theory is
/// KILLED by this measurement; the per-call cost model here was already correct. D39's real cause
/// is the ledger trusting its own running total forever instead of periodically checking it
/// against Twelve Data's counter — see <c>TwelveDataCreditThrottle</c>'s seed-on-day-start and
/// periodic reconciliation.</para>
/// </summary>
public static class TwelveDataCreditPolicy
{
    /// <summary>Maximum credits Twelve Data's free tier allows in any rolling 60-second window.
    /// A <c>/quote</c> batch must never carry more symbols than this in one request.</summary>
    public const int PerMinuteCreditLimit = 8;

    /// <summary>Maximum credits Twelve Data's free tier allows per UTC day.</summary>
    public const int DailyCreditBudget = 800;

    /// <summary>Length of a full NYSE regular session in minutes (9:30 AM - 4:00 PM ET), used to
    /// derive a safe quote-refresh cadence for a given symbol count — see
    /// <see cref="TwelveDataCadenceCalculator"/>.</summary>
    public const int NyseSessionMinutes = 390;

    /// <summary>How often <see cref="TwelveDataCreditThrottle"/> may re-check its persisted ledger
    /// against Twelve Data's own <c>GET /api_usage</c> counter (D39). Deliberately not "per call":
    /// that endpoint itself costs 1 credit, so reconciling more often than this would make credit
    /// monitoring a meaningful drain on the very budget it protects.</summary>
    public const int ReconciliationIntervalMinutes = 60;

    /// <summary>Credits <see cref="TwelveDataCreditThrottle"/> spends on <b>itself</b> over a full
    /// UTC day via <c>GET /api_usage</c> — never on a quote or history call. One probe seeds a new
    /// day, plus one probe per <see cref="ReconciliationIntervalMinutes"/> thereafter
    /// (<c>1440 / ReconciliationIntervalMinutes</c> reconciliations, each billed exactly like any
    /// other Twelve Data request per D45). At the default 60-minute interval this is 24 + 1 = 25.
    ///
    /// <para>These credits still count against <see cref="DailyCreditBudget"/>, so anything that
    /// budgets the day's remaining spend — currently only
    /// <see cref="TwelveDataCadenceCalculator"/> — must reserve for them explicitly. Left
    /// unreserved, the shortfall doesn't show up as an error anywhere; it lands on whichever caller
    /// happens to ask last in the day, silently, the same "not attempted vs attempted and failed"
    /// shape as D10/D26/D33/D35/D38/D45. This is the one place that knows the number — do not
    /// hardcode 25 elsewhere.</para>
    /// </summary>
    public const int DailyReconciliationCreditReserve = 1440 / ReconciliationIntervalMinutes + 1;
}
