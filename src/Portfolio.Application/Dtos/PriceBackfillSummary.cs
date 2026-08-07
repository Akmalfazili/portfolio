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

/// <summary>How one <c>IPriceBackfillService.RunIfDueAsync</c> check concluded — see D12.</summary>
public enum PriceBackfillOutcome
{
    /// <summary>A backfill actually ran; <see cref="PriceBackfillRunResult.Summary"/> is set.</summary>
    Completed,

    /// <summary>NYSE — the later-closing of the two exchanges backfill covers — is still in its
    /// regular session, so a close for today is not yet available. Not an error; the next poll
    /// tick after the close will run it.</summary>
    MarketOpen,

    /// <summary>A scheduled backfill has already completed once today; running again would only
    /// re-spend provider calls to insert nothing new, since <c>PriceHistory</c> gains at most one
    /// new row per asset per calendar day.</summary>
    AlreadyRanToday,
}

/// <summary>Result of one <c>IPriceBackfillService.RunIfDueAsync</c> check, called by
/// <c>PriceBackfillBackgroundService</c> on every poll tick.</summary>
public sealed record PriceBackfillRunResult(PriceBackfillOutcome Outcome, PriceBackfillSummary? Summary);
