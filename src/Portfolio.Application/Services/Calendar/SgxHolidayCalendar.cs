using System.Collections.Concurrent;

namespace Portfolio.Application.Services.Calendar;

/// <summary>
/// SGX public holidays — deliberately partial. Only the Gregorian, fixed-date holidays are
/// modeled: New Year's Day, Good Friday, Labour Day (1 May), National Day (9 August), and
/// Christmas Day. A holiday that falls on a Sunday is observed the following Monday, per
/// Singapore's public holiday rule; a Saturday holiday is left in place (Saturday is already a
/// non-trading day, so there is nothing to shift).
///
/// NOT modeled, and deliberately never will be (D4, decided 2026-08-08): Singapore's
/// lunar/lunisolar holidays — Chinese New Year (2 days), Hari Raya Puasa, Hari Raya Haji, Vesak
/// Day, and Deepavali. Their dates move every year and would need a maintained per-year lookup
/// table rather than a rule. A table nobody refreshes silently rots into the same wrong answer it
/// was added to fix, so the wrongness is contained downstream instead of papered over here.
///
/// <para>What this calendar therefore still gets wrong: it reports SGX open on those days, and the
/// refresh service makes a wasted (but harmless — Yahoo is free and unmetered) Z74 poll on a
/// handful of days each year.</para>
///
/// <para>What that no longer causes: Yahoo answers such a poll with the <i>previous</i> session's
/// close, and the stored quote used to be reported to the user as a live price — Z74 silently
/// looked current on a day it had not traded. <c>PortfolioSummaryService.IsQuoteLive</c> now
/// compares a quote's own timestamp against the current exchange-local trading day <b>without
/// consulting this table</b>, so a quote from an earlier session is reported as
/// <c>PriceSource.Close</c> carrying that session's date. The calendar stays wrong on lunar
/// holidays; the price the user sees does not. Pinned by <c>QuoteStalenessTests</c>, which asserts
/// this table's wrongness as an explicit premise.</para>
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
