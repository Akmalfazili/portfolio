using FluentAssertions;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Services.Calculators;

namespace Portfolio.UnitTests.Services.Calculators;

/// <summary>
/// <see cref="AnnualReturnCalculator"/> — time-weighted return, so a deposit is never counted as
/// a gain. <see cref="Calculate_HandComputedTwoYearExample_WithMidYearContributionAndWithdrawal"/>
/// is the load-bearing test: every number in it is worked out by hand in the comment, not just
/// checked for self-consistency with the algorithm under test.
/// </summary>
public sealed class AnnualReturnCalculatorTests
{
    private readonly AnnualReturnCalculator _sut = new();

    /// <summary>
    /// Hand-computed two-year example with a mid-year contribution, a mid-year withdrawal, and a
    /// position that carries across the year boundary.
    ///
    /// 2024:
    ///   Jan 2  V=1000  CF=+1000  (initial buy; previous V=0, so no return is computed — no
    ///                             capital was at risk before the funding event)
    ///   Jun 30 V=1100  CF=0      r = (1100 - 0)   / 1000 - 1 = 0.10   (+10%, pure price gain)
    ///   Jul 1  V=1600  CF=+500   r = (1600 - 500) / 1100 - 1 = 0.00   (mid-year top-up: the extra
    ///                             500 is the caller's own money, not a gain)
    ///   Dec 31 V=1760  CF=0      r = (1760 - 0)   / 1600 - 1 = 0.10   (+10%)
    ///   2024 TWR = 1.10 * 1.00 * 1.10 - 1 = 0.21  -> +21.00%
    ///
    /// 2025 (continuing from the same 1760 position, carried across the year boundary):
    ///   Jan 2  V=1584  CF=0      r = (1584 - 0)      / 1760 - 1 = -0.10  (-10%, price drop)
    ///   Jun 15 V=784   CF=-800   r = (784 - (-800))  / 1584 - 1 = 0.00   (mid-year withdrawal of
    ///                             800 at no price change: 1584 - 800 = 784)
    ///   Dec 31 V=980   CF=0      r = (980 - 0)       / 784  - 1 = 0.25   (+25%)
    ///   2025 TWR = 0.90 * 1.00 * 1.25 - 1 = 0.125 -> +12.50%
    /// </summary>
    [Fact]
    public void Calculate_HandComputedTwoYearExample_WithMidYearContributionAndWithdrawal()
    {
        IReadOnlyList<DailyPortfolioValue> dailyValues =
        [
            new DailyPortfolioValue(new DateOnly(2024, 1, 2), 1000m),
            new DailyPortfolioValue(new DateOnly(2024, 6, 30), 1100m),
            new DailyPortfolioValue(new DateOnly(2024, 7, 1), 1600m),
            new DailyPortfolioValue(new DateOnly(2024, 12, 31), 1760m),
            new DailyPortfolioValue(new DateOnly(2025, 1, 2), 1584m),
            new DailyPortfolioValue(new DateOnly(2025, 6, 15), 784m),
            new DailyPortfolioValue(new DateOnly(2025, 12, 31), 980m),
        ];

        IReadOnlyList<PortfolioCashFlow> cashFlows =
        [
            new PortfolioCashFlow(new DateOnly(2024, 1, 2), 1000m),
            new PortfolioCashFlow(new DateOnly(2024, 7, 1), 500m),
            new PortfolioCashFlow(new DateOnly(2025, 6, 15), -800m),
        ];

        var results = _sut.Calculate(dailyValues, cashFlows);

        results.Should().HaveCount(2);

        results[0].Year.Should().Be(2024);
        results[0].TimeWeightedReturnPercent.Should().Be(21.00m);

        results[1].Year.Should().Be(2025);
        results[1].TimeWeightedReturnPercent.Should().Be(12.50m);
    }

    [Fact]
    public void Calculate_NoCashFlows_SimpleCompounding()
    {
        // 100 -> 110 -> 121, a clean 10% then 10% with no external flows: (1.1 * 1.1) - 1 = 0.21.
        IReadOnlyList<DailyPortfolioValue> dailyValues =
        [
            new DailyPortfolioValue(new DateOnly(2026, 1, 1), 100m),
            new DailyPortfolioValue(new DateOnly(2026, 6, 1), 110m),
            new DailyPortfolioValue(new DateOnly(2026, 12, 31), 121m),
        ];

        var results = _sut.Calculate(dailyValues, []);

        results.Should().ContainSingle();
        results[0].Year.Should().Be(2026);
        results[0].TimeWeightedReturnPercent.Should().Be(21.00m);
    }

    [Fact]
    public void Calculate_PositionFullyClosedThenReopened_SkipsTheZeroBaseSubPeriod()
    {
        // Fully sold to zero on Mar 1 (no return computable leaving a zero base), then a fresh
        // position opened Jun 1 (also no return computable — nothing existed before it), then
        // grows 10% by year end.
        IReadOnlyList<DailyPortfolioValue> dailyValues =
        [
            new DailyPortfolioValue(new DateOnly(2026, 1, 1), 500m),
            new DailyPortfolioValue(new DateOnly(2026, 3, 1), 0m),
            new DailyPortfolioValue(new DateOnly(2026, 6, 1), 200m),
            new DailyPortfolioValue(new DateOnly(2026, 12, 31), 220m),
        ];
        IReadOnlyList<PortfolioCashFlow> cashFlows =
        [
            new PortfolioCashFlow(new DateOnly(2026, 3, 1), -500m),
            new PortfolioCashFlow(new DateOnly(2026, 6, 1), 200m),
        ];

        var results = _sut.Calculate(dailyValues, cashFlows);

        results.Should().ContainSingle();
        // Only the Jun 1 -> Dec 31 sub-period contributes a return: (220 - 0) / 200 - 1 = 0.10.
        results[0].TimeWeightedReturnPercent.Should().Be(10.00m);
    }

    [Fact]
    public void Calculate_FewerThanTwoValuationPoints_ReturnsEmpty()
    {
        _sut.Calculate([new DailyPortfolioValue(new DateOnly(2026, 1, 1), 100m)], []).Should().BeEmpty();
        _sut.Calculate([], []).Should().BeEmpty();
    }

    [Fact]
    public void Calculate_UnsortedInput_IsSortedBeforeProcessing()
    {
        IReadOnlyList<DailyPortfolioValue> dailyValues =
        [
            new DailyPortfolioValue(new DateOnly(2026, 12, 31), 110m),
            new DailyPortfolioValue(new DateOnly(2026, 1, 1), 100m),
        ];

        var results = _sut.Calculate(dailyValues, []);

        results.Should().ContainSingle();
        results[0].TimeWeightedReturnPercent.Should().Be(10.00m);
    }
}
