namespace Portfolio.Application.Abstractions;

/// <summary>One point on a cost-basis-vs-market-value series, both already in USD.</summary>
public sealed record PerformanceSeriesPoint(DateOnly Date, decimal CostBasisUsd, decimal MarketValueUsd);

/// <summary>
/// Builds the cost-basis step series vs daily market value used by an asset's performance chart.
/// Stocks only — see the crypto scope decision; nothing stops this type being called for a crypto
/// asset, but the orchestrating service is responsible for rejecting that before it ever reaches
/// here, since crypto has no <c>PriceHistory</c> to build a series from in the first place.
/// Pure and currency-agnostic like <see cref="ICostBasisCalculator"/>: both inputs are already
/// USD-denominated, so all FX resolution happens once, in the caller.
/// </summary>
public interface IPerformanceSeriesBuilder
{
    /// <summary>
    /// <paramref name="costBasisSteps"/> is the per-transaction running state from
    /// <see cref="ICostBasisCalculator"/>, in transaction order. <paramref name="dailyCloseUsd"/>
    /// is one entry per day price history exists for the asset, USD-converted, in any order.
    /// Points are only emitted for dates on or after the first transaction and for which a close
    /// is available — market value cannot be computed for a date with no close, and cost basis is
    /// undefined before the first transaction.
    /// </summary>
    IReadOnlyList<PerformanceSeriesPoint> Build(
        IReadOnlyList<CostBasisStep> costBasisSteps,
        IReadOnlyList<(DateOnly Date, decimal CloseUsd)> dailyCloseUsd);
}
