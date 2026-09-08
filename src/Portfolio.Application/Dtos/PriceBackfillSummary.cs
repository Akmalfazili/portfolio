using Portfolio.Domain.Enums;

namespace Portfolio.Application.Dtos;

/// <summary>One asset that was requested from the provider but did not come back successfully —
/// either the call threw (network/transport failure) or the provider itself reported a non-success
/// result (e.g. Twelve Data's <c>{"status":"error"}</c> body, or a bare HTTP failure). Distinct
/// from <see cref="PriceBackfillSummary.AssetsSkippedForBudget"/>, which never even attempted a
/// call — collapsing "we chose not to call the provider" and "we called it and it failed" into one
/// list is exactly the reporting-layer bug this type exists to avoid (see the D12 follow-up in
/// tracker.md): raising <c>MaxProviderCallsPerRun</c> does nothing for an asset in this list.
/// </summary>
public sealed record AssetBackfillFailure(string Symbol, string Error);

/// <summary>
/// Report of one <c>IPriceBackfillService.RunAsync</c> invocation. Every asset ends up in exactly
/// one of <see cref="AssetsProcessed"/>, <see cref="AssetsSkippedForBudget"/>,
/// <see cref="AssetsFailed"/>, or <see cref="AssetsSkippedTodayNotClosed"/> — never two, and never
/// silently absent from all four.
///
/// <paramref name="AssetsWithTruncatedHistory"/> lists assets whose history was fetched
/// successfully but not from the full requested start date — a provider-side window limit (e.g.
/// CoinGecko's keyless 365-day cap) forced a later start. Each entry names the asset and the
/// requested vs. effective start date, so a consumer of this summary can tell "the series starts
/// late by policy" apart from "nothing went wrong" without having to notice a gap in
/// <c>PriceHistory</c> after the fact. An asset can appear in both <see cref="AssetsProcessed"/>
/// and here (truncated is not a failure).
/// </summary>
public sealed record PriceBackfillSummary(
    IReadOnlyList<string> AssetsProcessed,
    /// <summary>Never attempted — <c>MaxProviderCallsPerRun</c> was already exhausted before this
    /// asset's turn. Raising the budget is the correct fix for an asset in this list, and only
    /// this list.</summary>
    IReadOnlyList<string> AssetsSkippedForBudget,
    /// <summary>Attempted and did not succeed — the asset and the provider's own error text, so a
    /// caller does not have to go spelunking in logs to learn e.g. "Twelve Data returned HTTP 400."
    /// Raising the budget will not fix anything in this list.</summary>
    IReadOnlyList<AssetBackfillFailure> AssetsFailed,
    /// <summary>Never attempted, and not a failure or a budget skip — the asset's earliest trade
    /// date is today, and a same-day request cannot succeed regardless of the provider (an equity
    /// daily close does not exist until the session ends). Costs zero calls; picked up
    /// automatically once "today" becomes a past date on a later run.</summary>
    IReadOnlyList<string> AssetsSkippedTodayNotClosed,
    int PriceHistoryPointsInserted,
    int FxRatePointsInserted,
    int ProviderCallsUsed,
    IReadOnlyList<string> AssetsWithTruncatedHistory);

/// <summary>
/// Why <c>IPriceBackfillService.RunIfDueAsync</c> decided one particular market was not due this
/// tick — see D47. Replaces the old NYSE-global <c>PriceBackfillOutcome.MarketOpen</c> /
/// <c>AlreadyRanToday</c> scalars, which asserted a single reason for the whole run even though
/// NYSE and SGX keep entirely separate session calendars: NYSE being open must never explain why
/// SGX's close backfill didn't run, and vice versa. A skip list whose name asserts a single
/// reason is the most repeated defect family in this project (D10, D26, D33, D35, D38, D45) —
/// this enum exists so "why" is carried per market rather than collapsed into one label.
/// </summary>
public enum PriceBackfillSkipReason
{
    /// <summary>This market's regular session is still open at the instant this tick ran, so
    /// today's close is not yet on the wire — fetching now would just re-return yesterday's close,
    /// already on file from an earlier run. Not an error; the next poll tick after this market's
    /// own close will pick it up.</summary>
    SessionOpen,

    /// <summary>A scheduled backfill has already completed for this market since its own last
    /// session close (see <c>IMarketCalendar.LastSessionCloseAt</c>). Running again would only
    /// re-spend a provider call to insert nothing new, since a market publishes exactly one close
    /// per session and <c>PriceHistory</c> gains at most one new row per asset per session.</summary>
    AlreadyCoveredSinceLastClose,
}

/// <summary>One market <c>RunIfDueAsync</c> decided not to cover this tick, and why — see
/// <see cref="PriceBackfillSkipReason"/>.</summary>
public sealed record PriceBackfillMarketSkip(Market Market, PriceBackfillSkipReason Reason);

/// <summary>
/// Result of one <c>IPriceBackfillService.RunIfDueAsync</c> check, called by
/// <c>PriceBackfillBackgroundService</c> on every poll tick. Per-market by design (D47): a single
/// tick can run one market, skip the other, run both, or skip both, and every market ends up in
/// exactly one of <see cref="MarketsRun"/> or <see cref="MarketsSkipped"/> — never both, never
/// neither. <see cref="Summary"/> is non-null if and only if <see cref="MarketsRun"/> is
/// non-empty, and (when set) covers exactly the markets in <see cref="MarketsRun"/> — see
/// <c>IPriceBackfillService.RunAsync</c>'s <c>markets</c> parameter.
/// </summary>
public sealed record PriceBackfillRunResult(
    IReadOnlyList<Market> MarketsRun,
    IReadOnlyList<PriceBackfillMarketSkip> MarketsSkipped,
    PriceBackfillSummary? Summary);

/// <summary>
/// Response for <c>POST /api/prices/backfill</c>. Found live while verifying D37/D38: once every
/// Twelve Data call is paced through the shared credit throttle, a full backfill pass (22 calls at
/// 8 credits/minute) takes over two minutes — long enough that nginx's default proxy read timeout
/// 504s the request while the API keeps running it, and the aborted connection's own
/// <c>CancellationToken</c> then cancels every remaining provider call mid-run, each one
/// misreported as a genuine provider failure. The endpoint now detaches the run instead of
/// returning <see cref="PriceBackfillSummary"/> synchronously — poll <c>GET /api/prices/status</c>
/// or the <see cref="Domain.Entities.RefreshRun"/> audit trail for the outcome once it completes.
/// </summary>
public sealed record BackfillQueuedResult(bool Queued);
