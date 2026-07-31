using System.Collections.Concurrent;

namespace Portfolio.Application.Services.Calendar;

/// <summary>
/// SGX public holidays — deliberately partial. Only the Gregorian, fixed-date holidays are
/// modeled: New Year's Day, Good Friday, Labour Day (1 May), National Day (9 August), and
/// Christmas Day. A holiday that falls on a Sunday is observed the following Monday, per
/// Singapore's public holiday rule; a Saturday holiday is left in place (Saturday is already a
/// non-trading day, so there is nothing to shift).
///
/// NOT modeled: Singapore's lunar/lunisolar holidays — Chinese New Year (2 days), Hari Raya
/// Puasa, Hari Raya Haji, Vesak Day, and Deepavali. Their dates move every year and would need a
/// maintained per-year lookup table rather than a rule; that table does not exist yet. The
/// practical consequence: this calendar will incorrectly report SGX as open on those days, so
/// the refresh service may make a wasted (but harmless — Yahoo is free and unmetered) Z74 poll
/// on a handful of days each year. Flagged here rather than silently accepted as correct.
/// </summary>
internal static class SgxHolidayCalendar
{
    private static readonly ConcurrentDictionary<int, HashSet<DateOnly>> Cache = new();

    public static bool IsHoliday(DateOnly date) => Cache.GetOrAdd(date.Year, ComputeForYear).Contains(date);

    private static HashSet<DateOnly> ComputeForYear(int year) =>
    [
        ObservedSundayOnly(new DateOnly(year, 1, 1)), // New Year's Day
        HolidayMath.GoodFriday(year),
        ObservedSundayOnly(new DateOnly(year, 5, 1)), // Labour Day
        ObservedSundayOnly(new DateOnly(year, 8, 9)), // National Day
        ObservedSundayOnly(new DateOnly(year, 12, 25)), // Christmas Day
    ];

    private static DateOnly ObservedSundayOnly(DateOnly date) =>
        date.DayOfWeek == DayOfWeek.Sunday ? date.AddDays(1) : date;
}
