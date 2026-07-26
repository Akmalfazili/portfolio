namespace Portfolio.Application.Services;

/// <summary>
/// Bounds one <c>PriceBackfillService.RunAsync</c> invocation so a multi-year, multi-symbol
/// backfill cannot blow a provider's daily credit budget in a single run (Twelve Data free tier
/// is 800 credits/day). Each provider call — one per asset's <c>GetHistoryAsync</c>, one per
/// currency pair's FX history — counts against this budget. The service is idempotent (it only
/// inserts dates it does not already have), so assets skipped because the budget ran out are
/// simply picked up by the next run — no separate checkpoint state is needed.
/// </summary>
public sealed class PriceBackfillOptions
{
    public const string SectionName = "MarketData:Backfill";

    /// <summary>Maximum number of upstream historical-data calls (across all providers) in one run.</summary>
    public int MaxProviderCallsPerRun { get; set; } = 20;
}
