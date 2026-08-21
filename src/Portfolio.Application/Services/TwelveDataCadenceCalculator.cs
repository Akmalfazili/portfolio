namespace Portfolio.Application.Services;

/// <summary>
/// Derives how often a full Twelve Data quote sweep of N symbols can safely repeat, instead of
/// the cadence being a constant tuned for one portfolio size. A hardcoded 5-minute interval is
/// exactly right at N=21 and exactly wrong at N=100 — that mismatch is how D38 (a batch that
/// always 429s) was born in the first place, and it recurs the moment the portfolio grows unless
/// the interval is computed from the live symbol count and the live remaining daily budget.
///
/// <para>Reasoning: over a <c>sessionLength</c>-minute session, a sweep every <c>T</c> minutes
/// costs <c>(sessionLength / T) * N</c> credits. The daily backfill needs <c>N + 1</c> more
/// credits (one history call per symbol, plus one FX call) out of whatever the day's remaining
/// budget is, so the sweeps alone must fit inside <c>remainingDailyBudget - (N + 1)</c>. Solving
/// for <c>T</c>: <c>T &gt;= sessionLength * N / (remainingDailyBudget - N - 1)</c>. At N=21 and a
/// fresh 800-credit day this comes out to roughly 11 minutes; at N=40, roughly 21 minutes; at
/// N=100, roughly 56 minutes — all comfortably above the 5-minute floor this replaces at small N,
/// and widening automatically as N grows past it.</para>
/// </summary>
public static class TwelveDataCadenceCalculator
{
    /// <summary>
    /// The minimum safe interval between full sweeps of <paramref name="activeSymbolCount"/>
    /// Twelve Data symbols, given <paramref name="remainingDailyBudget"/> credits left today,
    /// never less than <paramref name="floor"/> (the previously-hardcoded 5-minute
    /// <c>PriceRefreshOptions.StockOpenInterval</c>, kept as an explicit lower bound rather than
    /// removed).
    /// </summary>
    public static TimeSpan DeriveStockOpenInterval(
        int activeSymbolCount, int remainingDailyBudget, TimeSpan sessionLength, TimeSpan floor)
    {
        if (activeSymbolCount <= 0)
        {
            return floor;
        }

        var reserveForDailyBackfill = activeSymbolCount + 1;
        var availableForSweeps = remainingDailyBudget - reserveForDailyBackfill;

        if (availableForSweeps <= 0)
        {
            // No budget left today for even one more sweep — the credit throttle's own daily-
            // budget gate is what actually stops the spend; this just avoids dividing by a
            // non-positive number and falls back to the floor rather than an infinite interval.
            return floor;
        }

        var minutesPerSweep = (double)activeSymbolCount / availableForSweeps * sessionLength.TotalMinutes;
        var derived = TimeSpan.FromMinutes(Math.Ceiling(minutesPerSweep));

        return derived > floor ? derived : floor;
    }
}
