namespace Portfolio.Application.Services.Calendar;

/// <summary>Small, dependency-free date-rule helpers shared by the NYSE and SGX holiday
/// calendars — nth-weekday-of-month, last-weekday-of-month, and the Gregorian Easter
/// computation that both exchanges' Good Friday closure derives from.</summary>
internal static class HolidayMath
{
    /// <summary>The nth occurrence of <paramref name="dayOfWeek"/> in <paramref name="month"/>
    /// of <paramref name="year"/> — e.g. the 3rd Monday of January (MLK Day).</summary>
    public static DateOnly NthWeekdayOfMonth(int year, int month, DayOfWeek dayOfWeek, int n)
    {
        var first = new DateOnly(year, month, 1);
        var offset = ((int)dayOfWeek - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + (7 * (n - 1)));
    }

    /// <summary>The last occurrence of <paramref name="dayOfWeek"/> in <paramref name="month"/>
    /// of <paramref name="year"/> — e.g. the last Monday of May (Memorial Day).</summary>
    public static DateOnly LastWeekdayOfMonth(int year, int month, DayOfWeek dayOfWeek)
    {
        var lastDay = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        var offset = ((int)lastDay.DayOfWeek - (int)dayOfWeek + 7) % 7;
        return lastDay.AddDays(-offset);
    }

    /// <summary>Easter Sunday for <paramref name="year"/> in the Gregorian calendar, via the
    /// Meeus/Jones/Butcher algorithm. Both NYSE and SGX close for Good Friday (two days before).</summary>
    public static DateOnly EasterSunday(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = ((h + l - 7 * m + 114) % 31) + 1;
        return new DateOnly(year, month, day);
    }

    public static DateOnly GoodFriday(int year) => EasterSunday(year).AddDays(-2);
}
