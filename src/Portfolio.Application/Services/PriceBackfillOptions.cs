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
    /// benefit to polling as tightly as the live-quote refresh loop does. Also the floor on how
    /// often a D51 retry can actually fire in practice — <see cref="FailedRunRetryDelay"/> below
    /// defaults to the same 15 minutes so the retry is reachable on the very next tick rather than
    /// waiting on a coarser poll it can never catch up to.</summary>
    public TimeSpan SchedulePollInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// D51: how long <c>RunIfDueAsync</c> waits after a failed <c>BackfillScheduled</c> run for a
    /// market before offering that market again as a retry. The trap this closes: "a run completed"
    /// is not "the market is covered" — before D51, a market whose scheduled run failed (e.g. a
    /// transient DNS outage right when the market's close became due) was indistinguishable from a
    /// genuinely covered one, and the failed assets sat stale until the NEXT session close, which
    /// could be a full day away or, for a Friday close, over the weekend. Defaults to 15 minutes —
    /// the same as <see cref="SchedulePollInterval"/>, so a retry is offered on the very next poll
    /// tick rather than an interval the poll loop can never actually observe.
    /// </summary>
    public TimeSpan FailedRunRetryDelay { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// D51: caps how many extra <c>BackfillScheduled</c> attempts one market gets per session close
    /// on top of the first (full-pass) attempt, so a persistently broken provider cannot retry
    /// forever and quietly burn the daily credit budget one small retry at a time. Defaults to 3 —
    /// at most 1 full pass + 3 retries per close. A retry is narrowed to only the assets (and FX
    /// pairs) still missing that market's latest close (see <c>PriceBackfillService.RunAsync</c>'s
    /// remarks), so the worst-case added spend from this cap is
    /// <c>MaxFailedRunRetriesPerClose × (assets that keep failing)</c> credits per close, not
    /// <c>MaxFailedRunRetriesPerClose</c> full passes.
    /// </summary>
    public int MaxFailedRunRetriesPerClose { get; set; } = 3;
}
