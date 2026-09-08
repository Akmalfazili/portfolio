using FluentAssertions;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Services.Calendar;
using Portfolio.Domain.Enums;

namespace Portfolio.UnitTests.Calendar;

/// <summary>
/// <see cref="MarketCalendar"/> must convert every instant through <see cref="TimeZoneInfo"/>
/// rather than a fixed UTC offset — these tests pin exact UTC instants either side of NYSE's 2026
/// DST transitions so a regression to a hard-coded offset fails loudly. SGX has no DST of its own,
/// but is covered for ordinary hours, weekends, its lunch break, and a shared holiday.
/// </summary>
public sealed class MarketCalendarTests
{
    private readonly MarketCalendar _sut = new();

    // ---- NYSE: spring-forward boundary (2026-03-08, 2am ET -> 3am EDT) ----

    [Fact]
    public void Nyse_SpringForward_OpenJustBeforeClose_PreDst()
    {
        // Fri 2026-03-06, still EST (UTC-5): 15:59 ET = 20:59 UTC.
        var instant = new DateTimeOffset(2026, 3, 6, 20, 59, 0, TimeSpan.Zero);
        _sut.IsOpen(Market.Nyse, instant).Should().BeTrue();
    }

    [Fact]
    public void Nyse_SpringForward_ClosedAtCloseTime_PreDst()
    {
        // Fri 2026-03-06, still EST: 16:00 ET (close) = 21:00 UTC.
        var instant = new DateTimeOffset(2026, 3, 6, 21, 0, 0, TimeSpan.Zero);
        _sut.IsOpen(Market.Nyse, instant).Should().BeFalse();
    }

    [Fact]
    public void Nyse_SpringForward_OpenJustBeforeClose_PostDst()
    {
        // Mon 2026-03-09, now EDT (UTC-4) after the Sunday shift: 15:59 ET = 19:59 UTC.
        var instant = new DateTimeOffset(2026, 3, 9, 19, 59, 0, TimeSpan.Zero);
        _sut.IsOpen(Market.Nyse, instant).Should().BeTrue();
    }

    [Fact]
    public void Nyse_SpringForward_ClosedAtCloseTime_PostDst()
    {
        // Mon 2026-03-09, EDT: 16:00 ET (close) = 20:00 UTC. A hard-coded UTC-5 offset would read
        // this as 15:00 ET — still open — and fail to detect the close. That is exactly the bug
        // this test exists to catch.
        var instant = new DateTimeOffset(2026, 3, 9, 20, 0, 0, TimeSpan.Zero);
        _sut.IsOpen(Market.Nyse, instant).Should().BeFalse();
    }

    // ---- NYSE: fall-back boundary (2026-11-01, 2am EDT -> 1am EST) ----

    [Fact]
    public void Nyse_FallBack_OpenAtOpenTime_PreDst()
    {
        // Fri 2026-10-30, still EDT (UTC-4): 09:30 ET (open) = 13:30 UTC.
        var instant = new DateTimeOffset(2026, 10, 30, 13, 30, 0, TimeSpan.Zero);
        _sut.IsOpen(Market.Nyse, instant).Should().BeTrue();
    }

    [Fact]
    public void Nyse_FallBack_ClosedJustBeforeOpen_PreDst()
    {
        var instant = new DateTimeOffset(2026, 10, 30, 13, 29, 0, TimeSpan.Zero);
        _sut.IsOpen(Market.Nyse, instant).Should().BeFalse();
    }

    [Fact]
    public void Nyse_FallBack_OpenAtOpenTime_PostDst()
    {
        // Mon 2026-11-02, now EST (UTC-5) after the Sunday shift: 09:30 ET (open) = 14:30 UTC.
        var instant = new DateTimeOffset(2026, 11, 2, 14, 30, 0, TimeSpan.Zero);
        _sut.IsOpen(Market.Nyse, instant).Should().BeTrue();
    }

    [Fact]
    public void Nyse_FallBack_SameUtcClockTimeAsPreDstOpen_IsNowBeforeOpen_PostDst()
    {
        // 13:30 UTC used to be 09:30 ET (open) before the fall-back. After it, the same UTC
        // instant is only 08:30 ET — before the open. A hard-coded offset would get this wrong
        // in the opposite direction from the spring-forward case above, so both are pinned.
        var instant = new DateTimeOffset(2026, 11, 2, 13, 30, 0, TimeSpan.Zero);
        _sut.IsOpen(Market.Nyse, instant).Should().BeFalse();
    }

    // ---- Weekends ----

    [Fact]
    public void Nyse_ClosedOnSaturday()
    {
        // 2026-08-01 is a Saturday; 15:00 UTC would be a normal Friday trading-hour instant.
        var instant = new DateTimeOffset(2026, 8, 1, 15, 0, 0, TimeSpan.Zero);
        _sut.IsOpen(Market.Nyse, instant).Should().BeFalse();
    }

    [Fact]
    public void Sgx_ClosedOnSaturday()
    {
        // 2026-08-01 10:00 SGT = 02:00 UTC, otherwise well inside the SGX morning session.
        var instant = new DateTimeOffset(2026, 8, 1, 2, 0, 0, TimeSpan.Zero);
        _sut.IsOpen(Market.Sgx, instant).Should().BeFalse();
    }

    // ---- Holidays ----

    [Fact]
    public void Nyse_ClosedOnThanksgiving2026()
    {
        // Thu 2026-11-26, 10:00 ET (a normal trading hour) = 15:00 UTC (EST, post fall-back).
        var instant = new DateTimeOffset(2026, 11, 26, 15, 0, 0, TimeSpan.Zero);
        _sut.IsOpen(Market.Nyse, instant).Should().BeFalse();
    }

    [Fact]
    public void Nyse_OpenOnOrdinaryThursdayOneWeekBeforeThanksgiving()
    {
        // Same weekday and time as the Thanksgiving test, one week earlier — proves the closure
        // above is the holiday rule, not a bug that closes every Thursday.
        var instant = new DateTimeOffset(2026, 11, 19, 15, 0, 0, TimeSpan.Zero);
        _sut.IsOpen(Market.Nyse, instant).Should().BeTrue();
    }

    [Fact]
    public void Sgx_ClosedOnNewYearsDay2026()
    {
        // Thu 2026-01-01, 10:00 SGT = 02:00 UTC.
        var instant = new DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);
        _sut.IsOpen(Market.Sgx, instant).Should().BeFalse();
    }

    [Fact]
    public void Sgx_OpenOnOrdinaryFridayAfterNewYear()
    {
        var instant = new DateTimeOffset(2026, 1, 2, 2, 0, 0, TimeSpan.Zero);
        _sut.IsOpen(Market.Sgx, instant).Should().BeTrue();
    }

    [Fact]
    public void Nyse_ClosedOnGoodFriday2026()
    {
        // Good Friday 2026 = 2026-04-03 (Easter Sunday 2026-04-05). 10:00 ET = 14:00 UTC (EDT).
        var instant = new DateTimeOffset(2026, 4, 3, 14, 0, 0, TimeSpan.Zero);
        _sut.IsOpen(Market.Nyse, instant).Should().BeFalse();
    }

    [Fact]
    public void Sgx_ClosedOnGoodFriday2026()
    {
        // Both exchanges observe Good Friday. 10:00 SGT = 02:00 UTC.
        var instant = new DateTimeOffset(2026, 4, 3, 2, 0, 0, TimeSpan.Zero);
        _sut.IsOpen(Market.Sgx, instant).Should().BeFalse();
    }

    // ---- SGX intraday lunch break ----

    // All instants below are Wed 2026-07-29, an ordinary SGX trading day (SGT = UTC+8 year-round).
    [Theory]
    [InlineData(1, 0, true)]   // 09:00 SGT — morning session opens
    [InlineData(3, 0, true)]   // 11:00 SGT — mid-morning
    [InlineData(4, 0, false)]  // 12:00 SGT — lunch break starts
    [InlineData(4, 30, false)] // 12:30 SGT — lunch break
    [InlineData(5, 0, true)]   // 13:00 SGT — afternoon session opens
    [InlineData(6, 0, true)]   // 14:00 SGT — mid-afternoon
    [InlineData(9, 0, false)]  // 17:00 SGT — close
    public void Sgx_LunchBreak_And_SessionBoundaries(int utcHour, int utcMinute, bool expectedOpen)
    {
        var instant = new DateTimeOffset(2026, 7, 29, utcHour, utcMinute, 0, TimeSpan.Zero);
        _sut.IsOpen(Market.Sgx, instant).Should().Be(expectedOpen);
    }

    [Fact]
    public void Sgx_ClosedJustBeforeMorningOpen()
    {
        // 08:59 SGT = 00:59 UTC.
        var instant = new DateTimeOffset(2026, 7, 29, 0, 59, 0, TimeSpan.Zero);
        _sut.IsOpen(Market.Sgx, instant).Should().BeFalse();
    }

    // ---- LastSessionCloseAt (D47) ----
    //
    // Unlike LocalDateOn, this DOES consult the holiday table — see the method's own remarks on
    // IMarketCalendar for why that's the correct side to be wrong on here.

    [Fact]
    public void LastSessionCloseAt_Nyse_ReturnsTodaysCloseWhenCalledAfterCloseOnANormalTradingDay()
    {
        // 2026-08-04 is an ordinary Tuesday (August is EDT, UTC-4). 21:00 UTC = 17:00 ET, one hour
        // after NYSE's 16:00 ET close — no walk-back needed at all.
        var instant = new DateTimeOffset(2026, 8, 4, 21, 0, 0, TimeSpan.Zero);
        var expectedClose = new DateTimeOffset(2026, 8, 4, 20, 0, 0, TimeSpan.Zero); // 16:00 EDT = 20:00 UTC
        _sut.LastSessionCloseAt(Market.Nyse, instant).Should().Be(expectedClose);
    }

    [Fact]
    public void LastSessionCloseAt_Nyse_WalksBackOverAWeekend()
    {
        // Sunday 2026-08-02, 23:00 UTC = 19:00 EDT — well after Friday's close would have been,
        // with Saturday and Sunday in between having no session at all. Must land on the PRECEDING
        // Friday (2026-07-31), not on the Saturday or Sunday themselves.
        var instant = new DateTimeOffset(2026, 8, 2, 23, 0, 0, TimeSpan.Zero);
        var expectedClose = new DateTimeOffset(2026, 7, 31, 20, 0, 0, TimeSpan.Zero); // Fri 16:00 EDT = 20:00 UTC
        _sut.LastSessionCloseAt(Market.Nyse, instant).Should().Be(expectedClose);
    }

    [Fact]
    public void LastSessionCloseAt_Nyse_WalksBackOverLaborDay2026AndTheWeekendBeforeIt()
    {
        // Labor Day 2026 = 2026-09-07, the first Monday of September. Calling early Tuesday
        // 2026-09-08 (before NYSE's own close that day, so the walk starts at Monday, not Tuesday)
        // must walk Mon (holiday) -> Sun -> Sat -> land on Fri 2026-09-04.
        var instant = new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero); // 06:00 EDT, before close
        var expectedClose = new DateTimeOffset(2026, 9, 4, 20, 0, 0, TimeSpan.Zero); // Fri 16:00 EDT = 20:00 UTC
        _sut.LastSessionCloseAt(Market.Nyse, instant).Should().Be(expectedClose);
    }

    [Fact]
    public void LastSessionCloseAt_Sgx_WalksBackOverAnObservedHolidayAndTheWeekendBeforeIt()
    {
        // SGX's National Day (9 August) falls on Sunday in 2026 and is observed the following
        // Monday, 2026-08-10 (SgxHolidayCalendar's Sunday-only observance rule). Calling on the
        // holiday itself, after its would-be close, must walk Mon (observed holiday) -> Sun (the
        // actual 9 Aug) -> Sat -> land on Fri 2026-08-07.
        var instant = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero); // 20:00 SGT
        var expectedClose = new DateTimeOffset(2026, 8, 7, 9, 0, 0, TimeSpan.Zero); // Fri 17:00 SGT = 09:00 UTC
        _sut.LastSessionCloseAt(Market.Sgx, instant).Should().Be(expectedClose);
    }

    [Fact]
    public void LastSessionCloseAt_Nyse_IsCorrectAcrossTheSpringForwardBoundary()
    {
        // The reason this class exists. "Now" is Monday 2026-03-09, the first trading day AFTER
        // the spring-forward (2026-03-08, 2am EST -> 3am EDT) — early enough (06:00 EDT) that the
        // walk-back must skip the weekend and land on the PRECEDING Friday, 2026-03-06, which is
        // still EST (UTC-5) — a DIFFERENT UTC offset than "now" itself is in. A hard-coded offset
        // would compute Friday's close using Monday's own (EDT) offset and be off by an hour.
        var instant = new DateTimeOffset(2026, 3, 9, 10, 0, 0, TimeSpan.Zero); // 06:00 EDT
        var expectedClose = new DateTimeOffset(2026, 3, 6, 21, 0, 0, TimeSpan.Zero); // Fri 16:00 EST = 21:00 UTC
        _sut.LastSessionCloseAt(Market.Nyse, instant).Should().Be(expectedClose);
    }
}
