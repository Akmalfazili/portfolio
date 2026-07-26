namespace Portfolio.Application.Dtos;

/// <summary>
/// Report of one <c>IPriceBackfillService.RunAsync</c> invocation.
/// <paramref name="AssetsWithTruncatedHistory"/> lists assets whose history was fetched
/// successfully but not from the full requested start date — a provider-side window limit (e.g.
/// CoinGecko's keyless 365-day cap) forced a later start. Each entry names the asset and the
/// requested vs. effective start date, so a consumer of this summary can tell "the series starts
/// late by policy" apart from "nothing went wrong" without having to notice a gap in
/// <c>PriceHistory</c> after the fact.
/// </summary>
public sealed record PriceBackfillSummary(
    IReadOnlyList<string> AssetsProcessed,
    IReadOnlyList<string> AssetsSkippedForBudget,
    int PriceHistoryPointsInserted,
    int FxRatePointsInserted,
    int ProviderCallsUsed,
    IReadOnlyList<string> AssetsWithTruncatedHistory);
