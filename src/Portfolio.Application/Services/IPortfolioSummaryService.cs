using Portfolio.Application.Dtos;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services;

/// <summary>
/// Cost basis, P&amp;L and allocation for one asset class. Works for stocks <i>and</i> crypto —
/// neither endpoint needs price history, only the current quote and the transaction log.
/// </summary>
public interface IPortfolioSummaryService
{
    Task<PortfolioSummaryDto> GetSummaryAsync(AssetClass assetClass, CancellationToken cancellationToken);

    Task<PortfolioAllocationDto> GetAllocationAsync(AssetClass assetClass, CancellationToken cancellationToken);
}
