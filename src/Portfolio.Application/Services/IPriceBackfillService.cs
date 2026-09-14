using Portfolio.Application.Dtos;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services;

/// <summary>
/// Backfills <c>PriceHistory</c> (per asset) and <c>FxRate</c> (per currency pair in play) from
/// each asset's earliest trade date forward to today. Idempotent against the unique indexes on
/// <c>(AssetId, Date)</c> and <c>(Date, Base, Quote)</c> — safe to re-run, and re-running is how
/// a budget-truncated backfill resumes.
///
/// D12: this must actually be called from somewhere, or <c>PriceHistory</c> never grows.
/// <see cref="RunAsync"/> is the unconditional entry point, used by the manual
/// <c>POST /api/prices/backfill</c> endpoint; <see cref="RunIfDueAsync"/> adds the per-market
/// calendar gate needed to call it safely on a schedule, used by
/// <c>PriceBackfillBackgroundService</c>.
///
/// D47: due-ness (and the run itself) is scoped per <see cref="Market"/>, not global. NYSE and SGX
/// keep independent session calendars — gating SGX's close backfill on NYSE's session (or vice
/// versa) is exactly the bug D47 fixes; see the class remarks on <c>PriceBackfillService</c>.
///
/// D51: <see cref="RunIfDueAsync"/> also treats a completed-but-FAILED scheduled run as due for a
/// bounded, paced retry, narrowed to only what that retry still needs — see the class remarks on
/// <c>PriceBackfillService</c> and <see cref="PriceBackfillOptions.FailedRunRetryDelay"/> /
/// <see cref="PriceBackfillOptions.MaxFailedRunRetriesPerClose"/>. <see cref="RunAsync"/>'s own
/// public contract is unchanged — it is always a full pass, exactly as before D51.
/// </summary>
public interface IPriceBackfillService
{
    /// <summary>Runs unconditionally for exactly the markets in <paramref name="markets"/>,
    /// bounded only by <see cref="PriceBackfillOptions.MaxProviderCallsPerRun"/>. Only assets whose
    /// own <see cref="Market"/> (via <c>ProviderMarkets.For(asset.QuoteProviderKind)</c>) is in
    /// <paramref name="markets"/> are touched — an SGX-only run spends zero Twelve Data credits on
    /// NYSE-listed assets, and vice versa. <paramref name="trigger"/> is recorded on the
    /// <see cref="Domain.Entities.RefreshRun"/> audit row(s) this writes (one row per market in
    /// <paramref name="markets"/> — see <c>RefreshRun.Market</c>), and must be one of the two
    /// <c>Backfill*</c> values.</summary>
    Task<PriceBackfillSummary> RunAsync(RefreshTrigger trigger, IReadOnlyCollection<Market> markets, CancellationToken cancellationToken);

    /// <summary>
    /// Refresh-catch-up feature: runs unconditionally for exactly <paramref name="assetIds"/> and
    /// <paramref name="currencies"/> — narrowed BEFORE the loop by <c>IRefreshCatchUpService</c>'s
    /// planner to only what it found actually missing coverage. <paramref name="markets"/> must be
    /// exactly the markets in play, so the per-market <see cref="Domain.Entities.RefreshRun"/> rows
    /// this writes land only on markets this run actually touched. Always tagged
    /// <see cref="RefreshTrigger.BackfillCatchUp"/>, which <see cref="RunIfDueAsync"/>'s due-ness
    /// query never looks at, so a catch-up run can never be mistaken for the scheduled full pass.
    /// </summary>
    Task<PriceBackfillSummary> RunCatchUpAsync(
        IReadOnlyCollection<Market> markets,
        IReadOnlySet<int> assetIds,
        IReadOnlySet<string> currencies,
        CancellationToken cancellationToken);

    /// <summary>
    /// Evaluates due-ness independently for every market in <c>ProviderMarkets.All</c> and runs
    /// <see cref="RunAsync"/> with <see cref="RefreshTrigger.BackfillScheduled"/> for exactly the
    /// markets found due (D47). A market is due when it is currently closed (its own session, not
    /// any other market's) AND no scheduled backfill has completed for it since its own last
    /// session close (<c>IMarketCalendar.LastSessionCloseAt</c>) — which subsumes the once-per-day
    /// throttle for free, since a market publishes exactly one close per session. Never calls
    /// <see cref="RunAsync"/> at all if no market is due.
    /// </summary>
    Task<PriceBackfillRunResult> RunIfDueAsync(CancellationToken cancellationToken);
}
