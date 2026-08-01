namespace Portfolio.Application.Services.Calculators;

/// <summary>
/// Rounds a USD-denominated figure to a sane number of decimal places for the wire, matching the
/// persisted precision conventions (<c>decimal(19,4)</c> for money, <c>decimal(28,10)</c> for
/// prices) — applied only at the DTO boundary, in <c>PortfolioSummaryService</c> and
/// <c>PortfolioPerformanceService</c>, never inside <see cref="ICostBasisCalculator"/>,
/// <see cref="Abstractions.IPerformanceSeriesBuilder"/>, <see cref="Abstractions.IAnnualReturnCalculator"/>
/// or <see cref="FxRateResolver"/> — those keep full, unrounded precision so a chain of
/// conversions never compounds rounding error.
///
/// Found live: a native amount divided by a historical FX rate (e.g. SGD cost basis / 1.29109)
/// can leave 20+ decimal digits in a C# <c>decimal</c> — exact, but meaningless past a few
/// significant digits of a dollar figure, and more digits than a JS client's double can even
/// represent, let alone needs (see tracker.md D8). Rounding only the values actually sent to a
/// caller avoids both without ever touching an intermediate sum.
/// </summary>
public static class DisplayRounding
{
    public static decimal Money(decimal value) => Math.Round(value, 4, MidpointRounding.ToEven);

    public static decimal? Money(decimal? value) => value is { } v ? Math.Round(v, 4, MidpointRounding.ToEven) : null;

    public static decimal Price(decimal value) => Math.Round(value, 10, MidpointRounding.ToEven);

    public static decimal? Price(decimal? value) => value is { } v ? Math.Round(v, 10, MidpointRounding.ToEven) : null;

    public static decimal Percent(decimal value) => Math.Round(value, 4, MidpointRounding.ToEven);

    public static decimal? Percent(decimal? value) => value is { } v ? Math.Round(v, 4, MidpointRounding.ToEven) : null;
}
