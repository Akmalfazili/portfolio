using Portfolio.Application.Abstractions;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services.Calendar;

/// <summary>
/// See <see cref="IMarketCalendar"/>. Converts the UTC instant into each exchange's local
/// wall-clock time via <see cref="TimeZoneInfo"/> (IANA ids — resolvable on both Windows and
/// Linux since .NET 6's ICU-backed lookup, so this works unchanged in the Docker container)
/// rather than a hard-coded offset, which is what makes it safe across NYSE's twice-yearly DST
/// shift. SGX has no DST, but NYSE's shift still changes the *gap* between the two markets'
/// local time twice a year, so both conversions go through the same mechanism rather than one
/// being "trusted" as fixed.
///
/// SGX's intraday session is modeled as two windows — 09:00–12:00 and 13:00–17:00 SGT — with the
/// midday hour treated as closed, matching SGX's actual lunch-break structure. NYSE has a single
/// continuous 09:30–16:00 ET session with no intraday break.
/// </summary>
public sealed class MarketCalendar : IMarketCalendar
{
    private static readonly TimeZoneInfo NyseZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly TimeZoneInfo SgxZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Singapore");

    private static readonly TimeOnly NyseOpen = new(9, 30);
    private static readonly TimeOnly NyseClose = new(16, 0);

    private static readonly TimeOnly SgxMorningOpen = new(9, 0);
    private static readonly TimeOnly SgxMorningClose = new(12, 0);
    private static readonly TimeOnly SgxAfternoonOpen = new(13, 0);
    private static readonly TimeOnly SgxAfternoonClose = new(17, 0);

    public bool IsOpen(Market market, DateTimeOffset instant) => market switch
    {
        Market.Nyse => IsNyseOpen(instant),
        Market.Sgx => IsSgxOpen(instant),
        _ => throw new ArgumentOutOfRangeException(nameof(market), market, "Unknown market."),
    };

    public DateOnly LocalDateOn(Market market, DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, ZoneFor(market)).DateTime);

    /// <summary>See <see cref="IMarketCalendar.LastSessionCloseAt"/> — deliberately does consult
    /// the holiday table, unlike <see cref="LocalDateOn"/>.</summary>
    public DateTimeOffset LastSessionCloseAt(Market market, DateTimeOffset instant)
    {
        var zone = ZoneFor(market);
        var closeTime = CloseTimeFor(market);
        var local = TimeZoneInfo.ConvertTime(instant, zone);

        var date = DateOnly.FromDateTime(local.DateTime);
        var timeOfDay = TimeOnly.FromDateTime(local.DateTime);

        // If the instant falls before today's close, today's session (even if it is otherwise a
        // trading day) has not finished yet, so the most recently COMPLETED close is an earlier
        // day's — start the walk-back from yesterday. If the instant is at or after today's close,
        // today's own close (once confirmed to be an actual trading day below) is the answer.
        if (timeOfDay < closeTime)
        {
            date = date.AddDays(-1);
        }

        while (IsWeekend(date.DayOfWeek) || IsHoliday(market, date))
        {
            date = date.AddDays(-1);
        }

        var closeLocal = new DateTime(date.Year, date.Month, date.Day, closeTime.Hour, closeTime.Minute, 0, DateTimeKind.Unspecified);
        var closeUtc = TimeZoneInfo.ConvertTimeToUtc(closeLocal, zone);
        return new DateTimeOffset(closeUtc, TimeSpan.Zero);
    }

    private static TimeZoneInfo ZoneFor(Market market) => market switch
    {
        Market.Nyse => NyseZone,
        Market.Sgx => SgxZone,
        _ => throw new ArgumentOutOfRangeException(nameof(market), market, "Unknown market."),
    };

    private static TimeOnly CloseTimeFor(Market market) => market switch
    {
        Market.Nyse => NyseClose,
        Market.Sgx => SgxAfternoonClose,
        _ => throw new ArgumentOutOfRangeException(nameof(market), market, "Unknown market."),
    };

    private static bool IsHoliday(Market market, DateOnly date) => market switch
    {
        Market.Nyse => NyseHolidayCalendar.IsHoliday(date),
        Market.Sgx => SgxHolidayCalendar.IsHoliday(date),
        _ => throw new ArgumentOutOfRangeException(nameof(market), market, "Unknown market."),
    };

    private static bool IsNyseOpen(DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTime(instant, NyseZone);

        if (IsWeekend(local.DayOfWeek))
        {
            return false;
        }

        var date = DateOnly.FromDateTime(local.DateTime);
        if (NyseHolidayCalendar.IsHoliday(date))
        {
            return false;
        }

        var time = TimeOnly.FromDateTime(local.DateTime);
        return time >= NyseOpen && time < NyseClose;
    }

    private static bool IsSgxOpen(DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTime(instant, SgxZone);

        if (IsWeekend(local.DayOfWeek))
        {
            return false;
        }

        var date = DateOnly.FromDateTime(local.DateTime);
        if (SgxHolidayCalendar.IsHoliday(date))
        {
            return false;
        }

        var time = TimeOnly.FromDateTime(local.DateTime);
        return (time >= SgxMorningOpen && time < SgxMorningClose)
            || (time >= SgxAfternoonOpen && time < SgxAfternoonClose);
    }

    private static bool IsWeekend(DayOfWeek dayOfWeek) => dayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
}
