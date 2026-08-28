using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services;

/// <summary>Mirrors <see cref="AssetBackfillFailure"/>'s reasoning from the price backfill: an
/// asset that was actually attempted and did not succeed, distinct from one never attempted at
/// all because the per-run budget ran out first.</summary>
public sealed record DividendAssetBackfillFailure(string Symbol, string Error);

/// <summary>
/// Report of one <c>IDividendBackfillService.RunAsync</c> invocation. Every stock asset with at
/// least one transaction ends up in exactly one of <see cref="AssetsProcessed"/>,
/// <see cref="AssetsSkippedForBudget"/>, or <see cref="AssetsFailed"/> — never two, never silently
/// absent from all three, per the D26 lesson applied here.
/// </summary>
public sealed record DividendBackfillSummary(
    IReadOnlyList<string> AssetsProcessed,
    /// <summary>Never attempted — <see cref="DividendBackfillOptions.MaxAssetsPerRun"/> was already
    /// exhausted before this asset's turn.</summary>
    IReadOnlyList<string> AssetsSkippedForBudget,
    /// <summary>Attempted and did not succeed — the asset and Yahoo's own error text.</summary>
    IReadOnlyList<DividendAssetBackfillFailure> AssetsFailed,
    int DividendEventsInserted,
    int ProviderCallsUsed);

/// <summary>How one <c>IDividendBackfillService.RunIfDueAsync</c> check concluded.</summary>
public enum DividendBackfillOutcome
{
    /// <summary>A backfill actually ran; <see cref="DividendBackfillRunResult.Summary"/> is set.</summary>
    Completed,

    /// <summary>A scheduled backfill has already completed once today — dividends move quarterly at
    /// most, so re-running today would only re-spend calls to insert nothing new.</summary>
    AlreadyRanToday,
}

/// <summary>Result of one <c>IDividendBackfillService.RunIfDueAsync</c> check.</summary>
public sealed record DividendBackfillRunResult(DividendBackfillOutcome Outcome, DividendBackfillSummary? Summary);

/// <summary>
/// Backfills <see cref="Domain.Entities.DividendEvent"/> rows for every active stock asset with at
/// least one transaction, from that asset's earliest trade date forward to today — so an all-time
/// dividend total is genuinely complete, not merely "since we started tracking it". Idempotent
/// against the unique index on <c>(AssetId, ExDate)</c> — safe to re-run.
///
/// Modelled directly on <c>IPriceBackfillService</c>: <see cref="RunAsync"/> is the unconditional
/// entry point; <see cref="RunIfDueAsync"/> adds the once-per-day throttle needed to call it safely
/// on a schedule.
/// </summary>
public interface IDividendBackfillService
{
    /// <summary>Runs unconditionally, bounded only by <see cref="DividendBackfillOptions.MaxAssetsPerRun"/>.
    /// <paramref name="trigger"/> must be one of the two <c>DividendBackfill*</c> values.</summary>
    Task<DividendBackfillSummary> RunAsync(RefreshTrigger trigger, CancellationToken cancellationToken);

    /// <summary>Runs <see cref="RunAsync"/> with <see cref="RefreshTrigger.DividendBackfillScheduled"/>,
    /// but only once per calendar day — dividends move quarterly, so a background poll finding
    /// nothing new every tick would just waste Yahoo calls for no benefit.</summary>
    Task<DividendBackfillRunResult> RunIfDueAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Response for <c>POST /api/dividends/backfill</c>. Mirrors <c>BackfillQueuedResult</c> (the
/// price-backfill equivalent) exactly: the run is detached onto a background task and this
/// response returns promptly, so poll <c>GET /api/assets/{id}/dividends</c> or the
/// <see cref="Domain.Entities.RefreshRun"/> audit trail for the outcome once it completes. Unlike
/// the price backfill, there is no Twelve Data pacing reason this *has* to be detached — Yahoo's
/// per-asset calls are fast and unmetered — but detaching anyway keeps the endpoint's behaviour
/// (and its in-flight/cooldown story) consistent with its sibling rather than one manual trigger
/// behaving synchronously and the other not.
/// </summary>
public sealed record DividendBackfillQueuedResult(bool Queued);
