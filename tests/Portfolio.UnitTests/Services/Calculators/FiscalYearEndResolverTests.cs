using FluentAssertions;
using Portfolio.Application.Services.Calculators;

namespace Portfolio.UnitTests.Services.Calculators;

/// <summary>
/// <see cref="FiscalYearEndResolver"/> — zakat.md §4.1's recurring month/day resolution, and §3.1's
/// write-time validation, including the 29 February trap: allowed at write time, clamped to 28 in
/// a non-leap year at resolution time.
/// </summary>
public sealed class FiscalYearEndResolverTests
{
    [Fact]
    public void Resolve_MonthDayAlreadyPassedThisYear_ReturnsThisYearsOccurrence()
    {
        // Reference date 2026-09-03; a 30 June year end has already passed this year.
        var result = FiscalYearEndResolver.Resolve(6, 30, new DateOnly(2026, 9, 3));

        result.Should().Be(new DateOnly(2026, 6, 30));
    }

    [Fact]
    public void Resolve_MonthDayNotYetReachedThisYear_ReturnsLastYearsOccurrence()
    {
        // Reference date 2026-09-03; a 31 December year end has not happened yet this year.
        var result = FiscalYearEndResolver.Resolve(12, 31, new DateOnly(2026, 9, 3));

        result.Should().Be(new DateOnly(2025, 12, 31));
    }

    [Fact]
    public void Resolve_MonthDayExactlyToday_ReturnsToday()
    {
        // zakat.md §4.1: candidate == today is allowed and correct.
        var today = new DateOnly(2026, 9, 3);
        var result = FiscalYearEndResolver.Resolve(9, 3, today);

        result.Should().Be(today);
    }

    [Fact]
    public void Resolve_29February_ClampsTo28_InANonLeapYear()
    {
        // 2025 and 2026 are not leap years — the last occurrence of "2/29" must clamp to 2/28.
        var result = FiscalYearEndResolver.Resolve(2, 29, new DateOnly(2026, 9, 3));

        result.Should().Be(new DateOnly(2026, 2, 28));
    }

    [Fact]
    public void Resolve_29February_ResolvesExactly_InALeapYear()
    {
        // 2028 is a leap year: resolving as of a date inside it should NOT clamp.
        var result = FiscalYearEndResolver.Resolve(2, 29, new DateOnly(2028, 3, 1));

        result.Should().Be(new DateOnly(2028, 2, 29));
    }

    [Fact]
    public void Resolve_NullMonth_ReturnsNull()
    {
        FiscalYearEndResolver.Resolve(null, 15, new DateOnly(2026, 9, 3)).Should().BeNull();
    }

    [Fact]
    public void Resolve_NullDay_ReturnsNull()
    {
        FiscalYearEndResolver.Resolve(6, null, new DateOnly(2026, 9, 3)).Should().BeNull();
    }

    [Fact]
    public void Resolve_BothNull_ReturnsNull()
    {
        FiscalYearEndResolver.Resolve(null, null, new DateOnly(2026, 9, 3)).Should().BeNull();
    }

    [Theory]
    [InlineData(1, 31, true)]
    [InlineData(2, 28, true)]
    [InlineData(2, 29, true)] // the named exception
    [InlineData(4, 30, true)]
    [InlineData(12, 31, true)]
    [InlineData(2, 30, false)] // never valid in any year
    [InlineData(4, 31, false)]
    [InlineData(6, 31, false)]
    [InlineData(9, 31, false)]
    [InlineData(11, 31, false)]
    [InlineData(0, 15, false)]
    [InlineData(13, 15, false)]
    [InlineData(6, 0, false)]
    [InlineData(6, 32, false)]
    public void IsValidMonthDay_MatchesExpected(int month, int day, bool expected)
    {
        FiscalYearEndResolver.IsValidMonthDay(month, day).Should().Be(expected);
    }
}
