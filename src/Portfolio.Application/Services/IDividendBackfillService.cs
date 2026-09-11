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
    /// <summary>A full backfill actually ran, covering every in-scope stock asset;
    /// <see cref="DividendBackfillRunResult.Summary"/> is set.</summary>
    Completed,

    /// <summary>
    /// D51: a productive full run already happened today, but a run "happening" is not every asset
    /// in it succeeding — the same trap D47/D51 closed for the price backfill, one layer down. This
    /// run instead retried exactly the asset(s) whose most recent attempt failed at least
    /// <see cref="DividendBackfillOptions.FailedAssetRetryInterval"/> ago;
    /// <see cref="DividendBackfillRunResult.Summary"/> is set and scoped to only those assets —
    /// never confuse its <c>AssetsProcessed</c> count with "every stock was checked today".
    /// </summary>
    RetryCompleted,

    /// <summary>
    /// D51 follow-up (found on review, before deploy): the most recent scheduled run today
    /// processed zero assets AND failed outright — every asset in scope failed, not "there was
    /// nothing to do" (contrast <see cref="AlreadyRanToday"/>'s zero-asset case, which has
    /// <c>Success == true</c> and is never paced). Deliberately distinguished from
    /// <see cref="AlreadyRanToday"/> so it reads honestly as "attempted and failed, waiting to
    /// retry" rather than "covered" — a market that failed outright is not covered. Paced by
    /// <see cref="DividendBackfillOptions.FailedAssetRetryInterval"/>, the same interval the
    /// partial-failure retry (<see cref="RetryCompleted"/>) uses;
    /// <see cref="DividendBackfillRunResult.Summary"/> is null — nothing ran.
    /// </summary>
    RetryPending,

    /// <summary>A productive scheduled backfill has already completed once today and every asset in
    /// scope is either covered or not yet due for a retry — dividends move quarterly at most, so
    /// re-running today would only re-spend calls to insert nothing new. Note what this does NOT
    /// cover: a zero-asset "nothing to do" run (D41) and an all-failed run not yet due for its own
    /// retry (D51 follow-up) both resolve differently — see <see cref="Completed"/> and
    /// <see cref="RetryPending"/> respectively, and <c>DividendBackfillService.RunIfDueAsync</c> for
    /// exactly how the three are told apart.</summary>
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
    /// but only once per calendar day for a FULL run — dividends move quarterly, so a background
    /// poll re-checking every asset on every tick would just waste Yahoo calls for no benefit.
    /// D51: once a PRODUCTIVE full run (at least one asset succeeded) has happened today, this also
    /// offers a narrowly-scoped RETRY (see <see cref="DividendBackfillOutcome.RetryCompleted"/>) for
    /// any asset whose last attempt failed at least
    /// <see cref="DividendBackfillOptions.FailedAssetRetryInterval"/> ago — unlike the price
    /// backfill's Twelve Data retries, this has no attempt cap, since Yahoo is free and keyless. An
    /// ALL-failed run (every asset failed, so nothing succeeded) is paced by the same interval before
    /// being retried in full (see <see cref="DividendBackfillOutcome.RetryPending"/>) rather than
    /// re-run unconditionally on every poll tick — a genuinely empty run (D41: no stock has a
    /// transaction yet) is the only case retried with no delay at all.</summary>
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
