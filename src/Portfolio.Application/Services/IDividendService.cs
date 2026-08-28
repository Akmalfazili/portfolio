using Portfolio.Application.Common;
using Portfolio.Application.Dtos;

namespace Portfolio.Application.Services;

/// <summary>
/// One stock asset's trailing-12-month and all-time dividend income, plus its coverage status —
/// the shape <see cref="PortfolioSummaryService"/> merges onto each stock <see cref="HoldingDto"/>.
/// Deliberately not a wire DTO: it is flattened onto <see cref="HoldingDto"/> and
/// <see cref="AssetDividendHistoryDto"/> rather than nested, so it lives here rather than in
/// <c>Dtos</c>.
/// </summary>
public sealed record AssetDividendSummary(
    decimal? Trailing12MonthIncomeUsd,
    decimal? AllTimeIncomeUsd,
    DividendCoverageStatus CoverageStatus);

/// <summary>
/// Dividend income for stock assets, computed from <see cref="Domain.Entities.DividendEvent"/> rows
/// (see <c>DividendBackfillService</c> for how they get there) and each asset's own transaction
/// history. Stocks only — crypto pays no dividends and is out of scope entirely.
/// </summary>
public interface IDividendService
{
    /// <summary>
    /// Full payment history for the asset detail page. <see cref="ServiceErrorKind.NotFound"/> if
    /// <paramref name="assetId"/> does not exist, <see cref="ServiceErrorKind.Validation"/> if it
    /// exists but is not a <see cref="Domain.Enums.AssetClass.Stock"/> — the same shape as
    /// <see cref="IPortfolioPerformanceService.GetAssetPerformanceAsync"/>, so a crypto asset id
    /// never comes back as an empty list that would render as "no dividends ever paid".
    /// </summary>
    Task<ServiceResult<AssetDividendHistoryDto>> GetAssetDividendHistoryAsync(
        int assetId, CancellationToken cancellationToken);

    /// <summary>
    /// Bulk per-asset summaries for every id in <paramref name="stockAssetIds"/>, used by
    /// <see cref="PortfolioSummaryService"/> to enrich each stock <see cref="HoldingDto"/> without
    /// duplicating the ex-date/FX/coverage logic in two places. Callers are expected to have
    /// already filtered to <see cref="Domain.Enums.AssetClass.Stock"/> ids that have at least one
    /// transaction — an id with neither is simply absent from the result rather than erroring.
    /// </summary>
    Task<IReadOnlyDictionary<int, AssetDividendSummary>> GetSummariesAsync(
        IReadOnlyCollection<int> stockAssetIds, CancellationToken cancellationToken);
}
