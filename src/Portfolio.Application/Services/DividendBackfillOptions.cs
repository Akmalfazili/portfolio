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

    /// <summary>
    /// How often <see cref="DividendBackfillBackgroundService"/> wakes up to check whether a
    /// scheduled backfill is due. Dividends themselves move quarterly, so the FULL-run gate (once
    /// per reporting day, see <see cref="IDividendBackfillService.RunIfDueAsync"/>) stays coarse on
    /// its own — but D51 added a bounded, narrowly-scoped RETRY for the specific assets whose last
    /// attempt failed (<see cref="FailedAssetRetryInterval"/>), and a 1-hour poll made that retry
    /// interval unreachable in practice: a transient failure discovered at, say, 09:05 could not
    /// actually be retried until the following hour's tick, then the one after that, compounding
    /// with 30-minute retry gaps into hours of real-world staleness for something that should have
    /// self-healed within the hour. Lowered to 15 minutes — cheap, since the due-ness check itself
    /// is a DB query with no provider call, so polling more often than the old 1-hour default costs
    /// nothing but this reachability.
    /// </summary>
    public TimeSpan SchedulePollInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// D51: how long <c>RunIfDueAsync</c> waits before retrying a dividend fetch that failed —
    /// governs TWO distinct cases, both paced by the same interval so there is one number to reason
    /// about, not two.
    ///
    /// <para><b>Partial failure</b> (most assets succeeded, some did not): the trap this closes is
    /// that a scheduled run's "did it do anything today" gate only looked at whether ANY asset was
    /// processed — a run where 21 of 22 assets succeeded and one failed still counted as productive
    /// and locked out the rest of the reporting day, stranding the one failed asset until tomorrow
    /// even though Yahoo's dividend endpoint is free, keyless, and cheap to retry.
    /// <see cref="IDividendBackfillService.RunIfDueAsync"/> instead retries just that asset once this
    /// interval has passed since its <c>AssetDividendState.LastAttemptedAt</c>.</para>
    ///
    /// <para><b>Total failure</b> (every asset failed — a review-found follow-up to the fix above,
    /// before deploy): a run where every asset failed has <c>SymbolsRefreshed == 0</c>, the exact
    /// shape D41's genuinely-nothing-to-do case has, which must retry with NO delay at all. Without
    /// pacing, an all-failed run fell through to that unpaced path and repeated a FULL run on every
    /// single poll tick — worsened, not helped, by <see cref="SchedulePollInterval"/> dropping from
    /// 1h to 15min for the partial-failure fix above, which quadrupled how often a sustained Yahoo
    /// outage or rate-limit got hammered. <see cref="IDividendBackfillService.RunIfDueAsync"/> now
    /// paces this case too, keyed on the failed <c>RefreshRun.StartedAt</c> rather than
    /// <c>AssetDividendState</c> (an all-failed run may not reach every asset's state row if it fails
    /// early).</para>
    ///
    /// <para>Unlike the price backfill's Twelve Data retries, neither case has a retry-COUNT cap —
    /// Yahoo has no credit budget to protect, so a persistently failing asset (or a persistently
    /// failing provider) simply keeps retrying every interval until it succeeds or the day rolls
    /// over.</para>
    /// </summary>
    public TimeSpan FailedAssetRetryInterval { get; set; } = TimeSpan.FromMinutes(30);
}
