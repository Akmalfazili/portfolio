using FluentAssertions;
using Portfolio.Application.Services;

namespace Portfolio.UnitTests.Services;

/// <summary>
/// Pins the arithmetic from the coordinator's brief: T >= sessionMinutes * N / (dailyBudget - N - 1).
/// A hardcoded 5-minute cadence is correct for exactly one portfolio size (~21 symbols) and wrong
/// the moment it grows — these numbers are the whole reason the interval must be derived, not
/// picked, and match the worked examples the task specified (~11 min at N=21, ~21 min at N=40,
/// ~56 min at N=100, all off a fresh 800-credit day).
/// </summary>
public sealed class TwelveDataCadenceCalculatorTests
{
    private static readonly TimeSpan NyseSession = TimeSpan.FromMinutes(TwelveDataCreditPolicy.NyseSessionMinutes);
    private static readonly TimeSpan Floor = TimeSpan.FromMinutes(5);

    [Theory]
    [InlineData(21, 800, 11)]
    [InlineData(40, 800, 21)]
    [InlineData(100, 800, 56)]
    public void DeriveStockOpenInterval_MatchesTheWorkedExamples(int symbolCount, int remainingBudget, int expectedMinutes)
    {
        var interval = TwelveDataCadenceCalculator.DeriveStockOpenInterval(symbolCount, remainingBudget, NyseSession, Floor);

        interval.Should().Be(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Fact]
    public void DeriveStockOpenInterval_NeverGoesBelowTheFloor_EvenForATinySymbolCount()
    {
        // At N=1 with a full day's budget the raw formula would derive well under 5 minutes -
        // the floor (the pre-existing hardcoded StockOpenInterval) must still win.
        var interval = TwelveDataCadenceCalculator.DeriveStockOpenInterval(1, 800, NyseSession, Floor);

        interval.Should().Be(Floor);
    }

    [Fact]
    public void DeriveStockOpenInterval_ZeroActiveSymbols_ReturnsTheFloor_WithoutDividingByZero()
    {
        var interval = TwelveDataCadenceCalculator.DeriveStockOpenInterval(0, 800, NyseSession, Floor);

        interval.Should().Be(Floor);
    }

    [Fact]
    public void DeriveStockOpenInterval_NoBudgetLeftForASweepToday_FallsBackToTheFloor()
    {
        // remainingDailyBudget smaller than N + 1 (the day's own backfill reserve) - nothing is
        // safely spendable on a sweep at all; the credit throttle's own daily gate is what
        // actually stops the spend, so this just avoids an invalid (non-positive) divisor.
        var interval = TwelveDataCadenceCalculator.DeriveStockOpenInterval(21, 10, NyseSession, Floor);

        interval.Should().Be(Floor);
    }

    [Fact]
    public void DeriveStockOpenInterval_WidensAsSymbolCountGrows_ForTheSameBudget()
    {
        var small = TwelveDataCadenceCalculator.DeriveStockOpenInterval(21, 800, NyseSession, Floor);
        var large = TwelveDataCadenceCalculator.DeriveStockOpenInterval(100, 800, NyseSession, Floor);

        large.Should().BeGreaterThan(small, "adding symbols must widen the interval automatically, not silently start 429ing");
    }
}
