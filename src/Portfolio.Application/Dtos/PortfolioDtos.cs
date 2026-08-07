using Portfolio.Domain.Enums;

namespace Portfolio.Application.Dtos;

/// <summary>
/// D20: distinguishes a holding's <see cref="HoldingDto.CurrentPriceUsd"/> as a freshly refreshed
/// quote versus a fallback to the last stored daily close (used when a market is closed, or the
/// asset was only just added and no refresh cycle has ticked yet). Never conflate these — a stale
/// close wearing a fresh-looking "as of now" timestamp is the exact D4 mistake this exists to
/// avoid, so <see cref="HoldingDto.PriceAsOf"/> always carries the close's own date when
/// <see cref="Close"/> applies, never "now". Null on <see cref="HoldingDto.PriceSource"/> means
/// neither is available — see <see cref="PortfolioSummaryDto.UnpricedHoldingsCount"/> (D17).
/// </summary>
public enum PriceSource
{
    /// <summary>From the live <see cref="Domain.Entities.PriceQuote"/>, written by the refresh
    /// service on its normal 5/60/2-minute cadence.</summary>
    Live,

    /// <summary>No live quote yet; falling back to the newest <see cref="Domain.Entities.PriceHistory"/>
    /// row for this asset. Only ever populated for stocks — crypto keeps no price history at all
    /// and its quote is never gated by a closed market, so this case should not arise for it.</summary>
    Close,
}

/// <summary>
/// One asset's position within a portfolio summary. All monetary fields are USD, converted at
/// each transaction's own historical FX rate for cost/realised figures. The live market value is
/// converted at today's rate (see <c>Services.Calculators.FxRateResolver</c>) when
/// <see cref="PriceSource"/> is <see cref="Dtos.PriceSource.Live"/>, or at the close's own date's
/// rate when it is <see cref="Dtos.PriceSource.Close"/> (D20) — never at today's rate for a price
/// that is not from today. Assets with neither a live quote nor any stored close yet report
/// <see cref="CurrentPriceUsd"/> as null, <see cref="PriceSource"/> as null, and
/// <see cref="MarketValueUsd"/> as zero rather than guessing — this should only be momentary,
/// right after an asset is created and before the next refresh/backfill cycle.
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
    PriceSource? PriceSource,
    decimal MarketValueUsd,
    decimal UnrealizedPnlUsd,
    decimal? UnrealizedPnlPercent,
    decimal RealizedPnlUsd);

/// <summary>
/// Portfolio-level totals for one <see cref="Domain.Enums.AssetClass"/> — stocks and crypto never
/// aggregate together. Includes every asset in the class that has at least one transaction, even
/// one fully sold down to zero quantity, since its realised P&amp;L still counts toward
/// <see cref="TotalRealizedPnlUsd"/>.
///
/// <see cref="UnpricedHoldingsCount"/> is D17's residual fix: a holding with a real cost basis but
/// neither a live quote nor a stored close still contributes zero to <see cref="TotalMarketValueUsd"/>
/// (nothing is guessed), so this count lets a caller caveat the totals rather than present a
/// possibly-large unrealised loss that is really just a missing price. Zero in the common case.
/// </summary>
public sealed record PortfolioSummaryDto(
    AssetClass AssetClass,
    decimal TotalCostBasisUsd,
    decimal TotalMarketValueUsd,
    decimal TotalUnrealizedPnlUsd,
    decimal? TotalUnrealizedPnlPercent,
    decimal TotalRealizedPnlUsd,
    int UnpricedHoldingsCount,
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
