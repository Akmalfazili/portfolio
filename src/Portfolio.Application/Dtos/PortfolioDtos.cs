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

    /// <summary>
    /// The blended average cost <i>per unit</i> of <see cref="QuantityHeld"/>, in USD —
    /// <see cref="CostBasisUsd"/> divided by <see cref="QuantityHeld"/>, using the same
    /// (already <see cref="Calculators.DisplayRounding.Money"/>-rounded) <see cref="CostBasisUsd"/>
    /// value carried on this DTO, so a reader who divides the two displayed columns by hand gets
    /// this figure back exactly. Rounded with <see cref="Calculators.DisplayRounding.Price"/> (10 dp),
    /// not <c>Money</c> (4 dp) — this is a per-unit price, not a monetary total, and a sub-cent
    /// asset like ANVL (~$0.0005326) would round to <c>0.00</c> at 4 dp, the exact class of lie the
    /// price-honesty rules in tracker.md exist to prevent.
    ///
    /// <para><c>null</c> when <see cref="QuantityHeld"/> is zero — a fully sold-down position has
    /// no average cost, only realised P&amp;L (<see cref="RealizedPnlUsd"/>), and
    /// <see cref="PortfolioSummaryDto.Holdings"/> deliberately includes those closed positions.
    /// Emitting <c>0m</c> there would read as "average cost of $0.00", not "not applicable".</para>
    /// </summary>
    decimal? AverageCostUsd,
    decimal? CurrentPriceNative,
    decimal? CurrentPriceUsd,
    DateTimeOffset? PriceAsOf,
    PriceSource? PriceSource,
    decimal MarketValueUsd,
    decimal UnrealizedPnlUsd,
    decimal? UnrealizedPnlPercent,
    decimal RealizedPnlUsd,

    /// <summary>
    /// Trailing-12-month dividend income, in USD, estimated from ex-date holdings (see
    /// <c>IDividendIncomeCalculator</c>) — never recorded cash actually received. Null for
    /// <see cref="Domain.Enums.AssetClass.Crypto"/> (which pays no dividends and is out of scope
    /// entirely — this DTO is shared across both asset classes, and crypto reports null here,
    /// never <c>0</c>) and null for a stock whose <see cref="DividendCoverageStatus"/> is
    /// <see cref="DividendCoverageStatus.NotYetFetched"/>, so ignorance is never mistaken for
    /// a real zero.
    /// </summary>
    decimal? DividendsTrailing12MonthUsd,

    /// <summary>All-time dividend income, in USD. Same null rules as <see cref="DividendsTrailing12MonthUsd"/>.</summary>
    decimal? DividendsAllTimeUsd,

    /// <summary>Null for crypto (the concept does not apply); always set for a stock — see
    /// <see cref="DividendCoverageStatus"/> for what each value means.</summary>
    DividendCoverageStatus? DividendCoverageStatus);

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

    /// <summary>Portfolio-level trailing-12-month dividend income, in USD. Null for
    /// <see cref="AssetClass.Crypto"/> (the concept does not apply there); for
    /// <see cref="AssetClass.Stock"/> it is a real, summed total (holdings with no figure yet
    /// contribute zero to it — the same "zero contribution, separate caveat count" pattern
    /// <see cref="UnpricedHoldingsCount"/> already uses for market value) — see
    /// <see cref="DividendsUncoveredCount"/> for the caveat.</summary>
    decimal? TotalDividendsTrailing12MonthUsd,

    /// <summary>Portfolio-level all-time dividend income, in USD. Same null rule as
    /// <see cref="TotalDividendsTrailing12MonthUsd"/>.</summary>
    decimal? TotalDividendsAllTimeUsd,

    /// <summary>How many <see cref="AssetClass.Stock"/> holdings do not have
    /// <see cref="DividendCoverageStatus.Covered"/> dividend data — mirrors
    /// <see cref="UnpricedHoldingsCount"/>'s role: lets a caller caveat the dividend totals rather
    /// than presenting them as complete when some assets have never been fetched or last failed.
    /// Always <c>0</c> for a <see cref="AssetClass.Crypto"/> summary.</summary>
    int DividendsUncoveredCount,
    IReadOnlyList<HoldingDto> Holdings);

/// <summary>One slice of the allocation pie. Only currently-held (quantity &gt; 0) assets appear —
/// a fully closed position has zero market value and nothing to allocate.</summary>
public sealed record AllocationItemDto(
    int AssetId,
    string Symbol,
    string Name,
    decimal MarketValueUsd,
    decimal PercentageOfTotal,

    /// <summary>
    /// D17 residual — false when this holding has no price from any source, so its
    /// <see cref="MarketValueUsd"/> is <c>0</c> because the value is <i>unknown</i>, not because
    /// the position is worthless.
    ///
    /// <para><see cref="PortfolioSummaryDto.UnpricedHoldingsCount"/> let the summary tiles caveat
    /// their totals, but nothing equivalent reached here, so an unpriced holding rendered as a
    /// silent <c>0%</c> slice — which reads as "you hold none of this" rather than "we don't know
    /// what this is worth". A 0% that means ignorance and a 0% that means a genuinely tiny position
    /// must not look identical.</para>
    /// </summary>
    bool HasPrice);

public sealed record PortfolioAllocationDto(
    AssetClass AssetClass,
    decimal TotalMarketValueUsd,
    IReadOnlyList<AllocationItemDto> Items,

    /// <summary>D17 residual — how many of <see cref="Items"/> have <c>HasPrice == false</c>, so a
    /// caller can caveat the whole pie without scanning it. Zero in the common case.</summary>
    int UnpricedHoldingsCount);

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
