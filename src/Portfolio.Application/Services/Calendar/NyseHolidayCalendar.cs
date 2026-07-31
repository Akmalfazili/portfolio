using System.Collections.Concurrent;

namespace Portfolio.Application.Services.Calendar;

/// <summary>
/// NYSE's published full-day market holidays: New Year's Day, Martin Luther King Jr. Day,
/// Washington's Birthday, Good Friday, Memorial Day, Juneteenth (observed since 2022), Independence
/// Day, Labor Day, Thanksgiving, and Christmas Day. A fixed-date holiday that falls on a Saturday
/// is observed the preceding Friday; one that falls on a Sunday is observed the following Monday —
/// NYSE's actual observed-holiday rule, not just "skip weekends".
/// Not modeled: early/1pm closes (e.g. the day after Thanksgiving, Christmas Eve when it falls on
/// a weekday) and ad-hoc closures (e.g. September 11, 2001, presidential funerals). Those affect
/// session length, not whether the market is open at all, which is all <see cref="IMarketCalendar"/>
/// answers.
/// </summary>
internal static class NyseHolidayCalendar
{
    private static readonly ConcurrentDictionary<int, HashSet<DateOnly>> Cache = new();

    public static bool IsHoliday(DateOnly date) => Cache.GetOrAdd(date.Year, ComputeForYear).Contains(date);

    private static HashSet<DateOnly> ComputeForYear(int year)
    {
        var holidays = new HashSet<DateOnly>
        {
            Observed(new DateOnly(year, 1, 1)), // New Year's Day
            HolidayMath.NthWeekdayOfMonth(year, 1, DayOfWeek.Monday, 3), // MLK Day
            HolidayMath.NthWeekdayOfMonth(year, 2, DayOfWeek.Monday, 3), // Washington's Birthday
            HolidayMath.GoodFriday(year),
            HolidayMath.LastWeekdayOfMonth(year, 5, DayOfWeek.Monday), // Memorial Day
            Observed(new DateOnly(year, 7, 4)), // Independence Day
            HolidayMath.NthWeekdayOfMonth(year, 9, DayOfWeek.Monday, 1), // Labor Day
            HolidayMath.NthWeekdayOfMonth(year, 11, DayOfWeek.Thursday, 4), // Thanksgiving
            Observed(new DateOnly(year, 12, 25)), // Christmas Day
        };

        if (year >= 2022)
        {
            holidays.Add(Observed(new DateOnly(year, 6, 19))); // Juneteenth National Independence Day
        }

        return holidays;
    }

    private static DateOnly Observed(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Saturday => date.AddDays(-1),
        DayOfWeek.Sunday => date.AddDays(1),
        _ => date,
    };
}
