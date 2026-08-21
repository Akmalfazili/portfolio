namespace Portfolio.Application.Services;

/// <summary>
/// Bounds one <c>PriceBackfillService.RunAsync</c> invocation so a multi-year, multi-symbol
/// backfill cannot blow a provider's daily credit budget in a single run (Twelve Data free tier
/// is 800 credits/day). Each provider call — one per asset's <c>GetHistoryAsync</c>, one per
/// currency pair's FX history — counts against this budget. The service is idempotent (it only
/// inserts dates it does not already have), so assets skipped because the budget ran out are
/// simply picked up by the next run — no separate checkpoint state is needed, and D37's ordering
/// fix means a skipped asset is never the SAME asset run after run.
/// </summary>
public sealed class PriceBackfillOptions
{
    public const string SectionName = "MarketData:Backfill";

    /// <summary>An explicit safety ceiling on top of the derived budget (D37 —
    /// <c>PriceBackfillService.RunAsync</c> actually uses
    /// <c>Math.Min(MaxProviderCallsPerRun, remaining daily Twelve Data credits)</c>). Defaults to
    /// the full daily budget so it does not, by itself, constrain a run below what the day's
    /// remaining credits actually allow — the hardcoded 20 this replaced was smaller than one full
    /// pass (22 calls for 21 assets + 1 FX pair) and is exactly what made D37 permanent. Lower this
    /// explicitly to bound a single run further, e.g. in tests.</summary>
    public int MaxProviderCallsPerRun { get; set; } = TwelveDataCreditPolicy.DailyCreditBudget;

    /// <summary>How often <c>PriceBackfillBackgroundService</c> wakes up to check whether a
    /// scheduled backfill is due (see <see cref="Services.IPriceBackfillService.RunIfDueAsync"/>).
    /// Deliberately coarse — the check itself costs no provider call, and the gate it evaluates
    /// (NYSE closed, not already run today) only actually becomes true once a day, so there is no
    /// benefit to polling as tightly as the live-quote refresh loop does.</summary>
    public TimeSpan SchedulePollInterval { get; set; } = TimeSpan.FromMinutes(15);
}
