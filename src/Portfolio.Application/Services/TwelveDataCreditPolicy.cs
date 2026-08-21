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
/// requested.</para>
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
}
