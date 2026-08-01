using Portfolio.Application.Common;
using Portfolio.Application.Dtos;

namespace Portfolio.Application.Services;

/// <summary>
/// Stocks-only analytics that need daily price history — the crypto scope decision means neither
/// method here has an equivalent for crypto. <see cref="GetAssetPerformanceAsync"/> rejects a
/// crypto asset id with a validation error rather than silently returning an empty series, which
/// would render as a flat line at zero and look like "no gain" instead of "not applicable".
/// </summary>
public interface IPortfolioPerformanceService
{
    /// <summary>
    /// <see cref="ServiceErrorKind.NotFound"/> if <paramref name="assetId"/> does not exist,
    /// <see cref="ServiceErrorKind.Validation"/> if it exists but is not a
    /// <see cref="Domain.Enums.AssetClass.Stock"/>.
    /// </summary>
    Task<ServiceResult<AssetPerformanceDto>> GetAssetPerformanceAsync(int assetId, CancellationToken cancellationToken);

    /// <summary>Time-weighted return per calendar year, across the whole stock portfolio.</summary>
    Task<AnnualReturnsDto> GetAnnualReturnsAsync(CancellationToken cancellationToken);
}
