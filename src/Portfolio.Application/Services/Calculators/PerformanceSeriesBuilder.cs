using Portfolio.Application.Abstractions;

namespace Portfolio.Application.Services.Calculators;

/// <summary>
/// Merges the per-transaction cost-basis steps with the daily USD close series via a two-pointer
/// walk (both inputs are sorted first), so it is O(n + m) rather than re-scanning the steps for
/// every day.
/// </summary>
public sealed class PerformanceSeriesBuilder : IPerformanceSeriesBuilder
{
    public IReadOnlyList<PerformanceSeriesPoint> Build(
        IReadOnlyList<CostBasisStep> costBasisSteps,
        IReadOnlyList<(DateOnly Date, decimal CloseUsd)> dailyCloseUsd)
    {
        if (costBasisSteps.Count == 0 || dailyCloseUsd.Count == 0)
        {
            return [];
        }

        var steps = costBasisSteps.OrderBy(s => s.TradeDate).ToList();
        var closes = dailyCloseUsd.OrderBy(c => c.Date).ToList();
        var firstTransactionDate = steps[0].TradeDate;

        var points = new List<PerformanceSeriesPoint>();
        var stepIndex = 0;
        CostBasisStep? current = null;

        foreach (var (date, closeUsd) in closes)
        {
            if (date < firstTransactionDate)
            {
                // No position existed yet — cost basis and market value are both undefined, not
                // zero-and-worth-charting.
                continue;
            }

            while (stepIndex < steps.Count && steps[stepIndex].TradeDate <= date)
            {
                current = steps[stepIndex];
                stepIndex++;
            }

            if (current is null)
            {
                continue;
            }

            points.Add(new PerformanceSeriesPoint(date, current.CostBasisUsd, current.QuantityHeld * closeUsd));
        }

        return points;
    }
}
