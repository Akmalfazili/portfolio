using Portfolio.Application.Dtos;

namespace Portfolio.Application.Services;

/// <summary>
/// Backfills <c>PriceHistory</c> (per asset) and <c>FxRate</c> (per currency pair in play) from
/// each asset's earliest trade date forward to today. Idempotent against the unique indexes on
/// <c>(AssetId, Date)</c> and <c>(Date, Base, Quote)</c> — safe to re-run, and re-running is how
/// a budget-truncated backfill resumes.
/// </summary>
public interface IPriceBackfillService
{
    Task<PriceBackfillSummary> RunAsync(CancellationToken cancellationToken);
}
