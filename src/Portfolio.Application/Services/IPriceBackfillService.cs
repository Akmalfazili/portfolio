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
/// <c>POST /api/prices/backfill</c> endpoint; <see cref="RunIfDueAsync"/> adds the market-calendar
/// gate and once-per-day throttle needed to call it safely on a schedule, used by
/// <c>PriceBackfillBackgroundService</c>.
/// </summary>
public interface IPriceBackfillService
{
    /// <summary>Runs unconditionally, bounded only by <see cref="PriceBackfillOptions.MaxProviderCallsPerRun"/>.
    /// <paramref name="trigger"/> is recorded on the <see cref="Domain.Entities.RefreshRun"/> audit
    /// row this writes, and must be one of the two <c>Backfill*</c> values.</summary>
    Task<PriceBackfillSummary> RunAsync(RefreshTrigger trigger, CancellationToken cancellationToken);

    /// <summary>Runs <see cref="RunAsync"/> with <see cref="RefreshTrigger.BackfillScheduled"/>, but
    /// only once NYSE is closed for the day (so the day's own close is actually available to fetch,
    /// and a call is never spent mid-session) and only once per calendar day (so a 15-minute poll
    /// tick does not re-spend a provider call every time it finds nothing new to insert).</summary>
    Task<PriceBackfillRunResult> RunIfDueAsync(CancellationToken cancellationToken);
}
