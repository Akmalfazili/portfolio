using FluentAssertions;
using Portfolio.Application.Services;

namespace Portfolio.UnitTests.Services;

/// <summary>
/// Pins the arithmetic from the coordinator's brief:
/// T >= sessionMinutes * N / (dailyBudget - N - 1 - reconciliationReserve). A hardcoded 5-minute
/// cadence is correct for exactly one portfolio size (~21 symbols) and wrong the moment it grows —
/// these numbers are the whole reason the interval must be derived, not picked, and match the
/// worked examples the task specified (~11 min at N=21, ~22 min at N=40, ~58 min at N=100, all off
/// a fresh 800-credit day, now that the reserve also covers the credit throttle's own
/// <c>GET /api_usage</c> seed-and-reconcile spend — see <see cref="TwelveDataCreditPolicy.DailyReconciliationCreditReserve"/>).
/// </summary>
public sealed class TwelveDataCadenceCalculatorTests
{
    private static readonly TimeSpan NyseSession = TimeSpan.FromMinutes(TwelveDataCreditPolicy.NyseSessionMinutes);
    private static readonly TimeSpan Floor = TimeSpan.FromMinutes(5);

    [Theory]
    [InlineData(21, 800, 11)]
    [InlineData(40, 800, 22)]
    [InlineData(100, 800, 58)]
    public void DeriveStockOpenInterval_MatchesTheWorkedExamples(int symbolCount, int remainingBudget, int expectedMinutes)
    {
        var interval = TwelveDataCadenceCalculator.DeriveStockOpenInterval(symbolCount, remainingBudget, NyseSession, Floor);

        interval.Should().Be(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Fact]
    public void DeriveStockOpenInterval_ReservesCreditsForTheThrottlesOwnApiUsageProbes()
    {
        // Live measured inputs (GET /api/prices/status): 21 Twelve Data symbols, 23 credits
        // already used today. Before this reserve existed, the formula ignored the ~25
        // credits/day the throttle spends probing GET /api_usage on its own behalf (D45) and
        // derived 11 minutes for these exact inputs. With the reserve, the same inputs must
        // derive a wider interval - the sweeps alone now have 25 fewer credits to divide up.
        var interval = TwelveDataCadenceCalculator.DeriveStockOpenInterval(21, 800 - 23, NyseSession, Floor);

        interval.Should().Be(TimeSpan.FromMinutes(12));
    }

    [Fact]
    public void DeriveStockOpenInterval_TheReconciliationReserveAloneCanPushAvailableForSweepsNonPositive()
    {
        // N=21 needs 22 credits for its own daily backfill reserve. A remaining budget of 30 is
        // comfortably above that (8 left over) - the pre-reserve formula would happily derive a
        // (very wide, but finite) interval from those 8 credits. Once the ~25-credit
        // reconciliation reserve is subtracted too, nothing is left (30 - 22 - 25 < 0), and the
        // guard must fall back to the floor instead of dividing by a negative number.
        var interval = TwelveDataCadenceCalculator.DeriveStockOpenInterval(21, 30, NyseSession, Floor);

        interval.Should().Be(Floor);
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
