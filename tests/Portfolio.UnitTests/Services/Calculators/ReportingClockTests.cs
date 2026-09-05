using FluentAssertions;
using Portfolio.Application.Services.Calculators;
using Portfolio.UnitTests.TestSupport;

namespace Portfolio.UnitTests.Services.Calculators;

/// <summary>
/// <see cref="ReportingClock"/> — pins the exact boundary this helper exists to fix: the eight
/// hours between SGT midnight and UTC midnight in which the old UTC-derived "today" disagreed
/// with the browser's local (SGT) notion of "today".
/// </summary>
public sealed class ReportingClockTests
{
    [Fact]
    public void Today_AtOneMinutePastSgtMidnight_ReturnsTheNewSgtDay()
    {
        // 2026-09-03T16:30:00Z = 2026-09-04 00:30 SGT — thirty minutes into the new SGT day,
        // while UTC still reads 2026-09-03.
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 9, 3, 16, 30, 0, TimeSpan.Zero));

        ReportingClock.Today(timeProvider).Should().Be(new DateOnly(2026, 9, 4));
    }

    [Fact]
    public void Today_OneHourBeforeSgtMidnight_StillReturnsTheOldSgtDay()
    {
        // 2026-09-03T15:30:00Z = 2026-09-03 23:30 SGT — still the old SGT day, thirty minutes
        // before the boundary.
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 9, 3, 15, 30, 0, TimeSpan.Zero));

        ReportingClock.Today(timeProvider).Should().Be(new DateOnly(2026, 9, 3));
    }

    [Fact]
    public void DateFor_AgreesWithToday_ForTheSameInstant()
    {
        var instant = new DateTimeOffset(2026, 9, 3, 16, 30, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(instant);

        ReportingClock.DateFor(instant).Should().Be(ReportingClock.Today(timeProvider));
    }

    [Fact]
    public void DateFor_ExactlyAtSgtMidnight_ReturnsTheNewDay()
    {
        // 2026-09-03T16:00:00Z = 2026-09-04 00:00:00 SGT exactly.
        ReportingClock.DateFor(new DateTimeOffset(2026, 9, 3, 16, 0, 0, TimeSpan.Zero))
            .Should().Be(new DateOnly(2026, 9, 4));
    }
}
