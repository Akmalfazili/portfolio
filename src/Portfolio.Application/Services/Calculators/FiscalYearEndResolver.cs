namespace Portfolio.Application.Services.Calculators;

/// <summary>
/// Resolves a recurring "month/day" financial year end (<c>Domain.Entities.Asset.FiscalYearEndMonth</c>
/// / <c>FiscalYearEndDay</c>) into the most recently completed occurrence on or before a reference
/// date — never a future one. Also validates the raw month/day pair at write time, independently of
/// any particular year.
/// </summary>
public static class FiscalYearEndResolver
{
    private static readonly int[] DaysInMonthNonLeapYear = [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];

    /// <summary>
    /// Null in, null out — "not configured" is a distinct, reported state
    /// (<c>ZakatAssetStatus.FiscalYearEndNotConfigured</c>), never guessed at as 31 December.
    /// Otherwise returns the most recent occurrence of month/day at or before
    /// <paramref name="referenceDate"/>: this year's if it has already passed (or falls exactly on
    /// <paramref name="referenceDate"/> — that is allowed and correct), otherwise last year's. The
    /// day is clamped to the last real day of that month in that year — this is where 29 February
    /// becomes 28 in a non-leap year, never rejected here (rejection happens at write time, in
    /// <see cref="IsValidMonthDay"/>).
    /// </summary>
    public static DateOnly? Resolve(int? month, int? day, DateOnly referenceDate)
    {
        if (month is not { } m || day is not { } d)
        {
            return null;
        }

        var candidate = ClampedDate(referenceDate.Year, m, d);
        if (candidate > referenceDate)
        {
            candidate = ClampedDate(referenceDate.Year - 1, m, d);
        }

        return candidate;
    }

    /// <summary>
    /// True for a month/day pair that is a real fiscal year end in at least one calendar year — 1-12
    /// for the month, and a day that is valid for that month in a non-leap year, with one named
    /// exception: 29 February is allowed (some companies do report a 2/29 fiscal year end; it
    /// resolves to 28 February in a non-leap year via <see cref="Resolve"/>, never rejected here).
    /// Rejects the combinations that are never valid in any year, such as 2/30 or 4/31.
    /// </summary>
    public static bool IsValidMonthDay(int month, int day)
    {
        if (month is < 1 or > 12)
        {
            return false;
        }

        if (day is < 1 or > 31)
        {
            return false;
        }

        if (month == 2 && day == 29)
        {
            return true;
        }

        return day <= DaysInMonthNonLeapYear[month - 1];
    }

    private static DateOnly ClampedDate(int year, int month, int day)
    {
        var clampedDay = Math.Min(day, DateTime.DaysInMonth(year, month));
        return new DateOnly(year, month, clampedDay);
    }
}
