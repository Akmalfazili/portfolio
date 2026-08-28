namespace Portfolio.Application.Services;

/// <summary>
/// Bounds one <c>DividendBackfillService.RunAsync</c> invocation. Unlike
/// <see cref="PriceBackfillOptions"/>, this has no Twelve Data credit budget to derive from —
/// Yahoo's dividend endpoint is free and keyless — but a per-run ceiling still exists so a
/// portfolio growing to an unusual size cannot turn one run into an unbounded loop of calls; the
/// default is generous enough not to constrain any realistic portfolio in one pass.
/// </summary>
public sealed class DividendBackfillOptions
{
    public const string SectionName = "MarketData:DividendBackfill";

    /// <summary>Safety ceiling on assets fetched in a single run. The service is idempotent (only
    /// inserts ex-dates it does not already have), so an asset skipped for budget is simply picked
    /// up by the next run, ordered least-recently-fetched first — the same D37 anti-starvation
    /// ordering <c>PriceBackfillService</c> uses.</summary>
    public int MaxAssetsPerRun { get; set; } = 500;

    /// <summary>How often <see cref="DividendBackfillBackgroundService"/> wakes up to check whether
    /// a scheduled backfill is due. Deliberately coarse — dividends move quarterly at most, so
    /// "once per day" (the gate <see cref="IDividendBackfillService.RunIfDueAsync"/> evaluates) is
    /// plenty, and polling more often than this buys nothing.</summary>
    public TimeSpan SchedulePollInterval { get; set; } = TimeSpan.FromHours(1);
}
