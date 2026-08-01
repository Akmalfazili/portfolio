using FluentAssertions;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Services.Calculators;

namespace Portfolio.UnitTests.Services.Calculators;

/// <summary>
/// Regression cover for cash flows dated off the valuation grid. The two series feeding
/// <see cref="AnnualReturnCalculator"/> come from different places — valuations from stored
/// <c>PriceHistory</c> dates, flows from each transaction's own <c>TradeDate</c> — so they are not
/// guaranteed to line up. Matching on exact date alone silently dropped the mismatched flows and
/// reported the deposit itself as performance.
/// </summary>
public sealed class AnnualReturnCashFlowAlignmentTests
{
    /// <summary>
    /// A $500 buy dated Saturday Jan 10, between two trading-day valuations, with no price
    /// movement at all. The portfolio goes 1000 -> 1500 purely because the caller put more money
    /// in, so the correct time-weighted return is 0%.
    /// </summary>
    [Fact]
    public void Calculate_CashFlowDatedOutsideTheValuationSeries_IsNotCountedAsAGain()
    {
        IReadOnlyList<DailyPortfolioValue> dailyValues =
        [
            new DailyPortfolioValue(new DateOnly(2026, 1, 5), 1000m),
            new DailyPortfolioValue(new DateOnly(2026, 1, 12), 1500m),
        ];

        IReadOnlyList<PortfolioCashFlow> cashFlows =
        [
            new PortfolioCashFlow(new DateOnly(2026, 1, 10), 500m),
        ];

        var results = new AnnualReturnCalculator().Calculate(dailyValues, cashFlows);

        results.Should().ContainSingle();
        results[0].TimeWeightedReturnPercent.Should().Be(0.00m);
    }

    /// <summary>The mirror case: a sell dated off-grid must not read as a loss.</summary>
    [Fact]
    public void Calculate_WithdrawalDatedOutsideTheValuationSeries_IsNotCountedAsALoss()
    {
        IReadOnlyList<DailyPortfolioValue> dailyValues =
        [
            new DailyPortfolioValue(new DateOnly(2026, 1, 5), 1000m),
            new DailyPortfolioValue(new DateOnly(2026, 1, 12), 600m),
        ];

        IReadOnlyList<PortfolioCashFlow> cashFlows =
        [
            new PortfolioCashFlow(new DateOnly(2026, 1, 10), -400m),
        ];

        var results = new AnnualReturnCalculator().Calculate(dailyValues, cashFlows);

        results.Should().ContainSingle();
        results[0].TimeWeightedReturnPercent.Should().Be(0.00m);
    }

    /// <summary>
    /// A flow dated after the last stored close is deliberately ignored: no valuation reflects the
    /// purchase either, so subtracting it would invent a loss out of a trade the series cannot see.
    /// </summary>
    [Fact]
    public void Calculate_CashFlowAfterTheLastValuation_IsIgnoredRatherThanInventingALoss()
    {
        IReadOnlyList<DailyPortfolioValue> dailyValues =
        [
            new DailyPortfolioValue(new DateOnly(2026, 1, 5), 1000m),
            new DailyPortfolioValue(new DateOnly(2026, 1, 12), 1100m),
        ];

        IReadOnlyList<PortfolioCashFlow> cashFlows =
        [
            new PortfolioCashFlow(new DateOnly(2026, 2, 20), 5000m),
        ];

        var results = new AnnualReturnCalculator().Calculate(dailyValues, cashFlows);

        results.Should().ContainSingle();
        results[0].TimeWeightedReturnPercent.Should().Be(10.00m);
    }
}
