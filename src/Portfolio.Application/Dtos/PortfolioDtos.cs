using Portfolio.Domain.Enums;

namespace Portfolio.Application.Dtos;

/// <summary>
/// One asset's position within a portfolio summary. All monetary fields are USD, converted at
/// each transaction's own historical FX rate for cost/realised figures and at today's rate (see
/// <c>Services.Calculators.FxRateResolver</c>) for the live market value. Assets with no current
/// <see cref="Domain.Entities.PriceQuote"/> yet report <see cref="CurrentPriceUsd"/> as null and
/// <see cref="MarketValueUsd"/> as zero rather than guessing — this should only be momentary, right
/// after an asset is created and before the next refresh cycle.
/// </summary>
public sealed record HoldingDto(
    int AssetId,
    string Symbol,
    string Name,
    AssetClass AssetClass,
    string Currency,
    decimal QuantityHeld,
    decimal CostBasisUsd,
    decimal? CurrentPriceNative,
    decimal? CurrentPriceUsd,
    DateTimeOffset? PriceAsOf,
    decimal MarketValueUsd,
    decimal UnrealizedPnlUsd,
    decimal? UnrealizedPnlPercent,
    decimal RealizedPnlUsd);

/// <summary>
/// Portfolio-level totals for one <see cref="Domain.Enums.AssetClass"/> — stocks and crypto never
/// aggregate together. Includes every asset in the class that has at least one transaction, even
/// one fully sold down to zero quantity, since its realised P&amp;L still counts toward
/// <see cref="TotalRealizedPnlUsd"/>.
/// </summary>
public sealed record PortfolioSummaryDto(
    AssetClass AssetClass,
    decimal TotalCostBasisUsd,
    decimal TotalMarketValueUsd,
    decimal TotalUnrealizedPnlUsd,
    decimal? TotalUnrealizedPnlPercent,
    decimal TotalRealizedPnlUsd,
    IReadOnlyList<HoldingDto> Holdings);

/// <summary>One slice of the allocation pie. Only currently-held (quantity &gt; 0) assets appear —
/// a fully closed position has zero market value and nothing to allocate.</summary>
public sealed record AllocationItemDto(
    int AssetId,
    string Symbol,
    string Name,
    decimal MarketValueUsd,
    decimal PercentageOfTotal);

public sealed record PortfolioAllocationDto(
    AssetClass AssetClass,
    decimal TotalMarketValueUsd,
    IReadOnlyList<AllocationItemDto> Items);

/// <summary>One point on a cost-basis-vs-market-value chart, both USD.</summary>
public sealed record PerformancePointDto(DateOnly Date, decimal CostBasisUsd, decimal MarketValueUsd);

/// <summary>
/// Stocks only. Requesting this for a crypto asset id is rejected by the service before this DTO
/// is ever built — see <c>IPortfolioPerformanceService</c> — so a caller can never receive an
/// empty series that would render as a flat line at zero and be mistaken for "no gain".
/// </summary>
public sealed record AssetPerformanceDto(
    int AssetId,
    string Symbol,
    string Name,
    string Currency,
    IReadOnlyList<PerformancePointDto> Points);

/// <summary>Time-weighted return for one calendar year, as a percentage.</summary>
public sealed record AnnualReturnDto(int Year, decimal TimeWeightedReturnPercent);

/// <summary>Stocks only, across the whole stock portfolio — not per asset.</summary>
public sealed record AnnualReturnsDto(IReadOnlyList<AnnualReturnDto> Years);
