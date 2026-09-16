namespace Portfolio.Infrastructure.MarketData.TwelveData;

/// <summary>
/// Shared <c>/time_series</c> request constants for <see cref="TwelveDataQuoteProvider"/> and
/// <see cref="TwelveDataFxProvider"/> — one place for the number so the two call sites cannot drift.
/// </summary>
internal static class TwelveDataTimeSeriesDefaults
{
    /// <summary>Twelve Data's documented maximum for <c>outputsize</c>. Both <c>/time_series</c>
    /// callers pin the request to this value explicitly rather than relying on the documented
    /// 30-row default being overridden by a date range — that override is observed, live behaviour
    /// (D39: a 1,668-row AAPL response from a 2020-01-01 <c>start_date</c> with no
    /// <c>outputsize</c> sent at all), not a documented guarantee, and stating what the request
    /// needs costs nothing extra (a <c>/time_series</c> call is one credit regardless of range,
    /// the same D39 measurement establishes).</summary>
    public const int MaxOutputSize = 5000;
}
