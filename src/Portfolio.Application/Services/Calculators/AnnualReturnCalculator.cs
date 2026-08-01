using Portfolio.Application.Abstractions;

namespace Portfolio.Application.Services.Calculators;

/// <summary>
/// Daily-valuation time-weighted return. See <see cref="IAnnualReturnCalculator"/> for the
/// formula; this class is pure arithmetic over already-USD-converted, already-assembled series —
/// no DB access, no FX resolution, no asset-class filtering. That belongs to the orchestrating
/// service, which is what makes this class cheap to prove correct against a hand-computed example.
/// </summary>
public sealed class AnnualReturnCalculator : IAnnualReturnCalculator
{
    public IReadOnlyList<AnnualReturn> Calculate(
        IReadOnlyList<DailyPortfolioValue> dailyValues,
        IReadOnlyList<PortfolioCashFlow> cashFlows)
    {
        var values = dailyValues.OrderBy(v => v.Date).ToList();
        if (values.Count < 2)
        {
            return [];
        }

        // Attribute each flow to the first valuation on or after its own date — the sub-period it
        // physically happened inside, whose closing V(t) already reflects it. Matching on exact
        // date alone dropped any flow dated a weekend, a market holiday, or any day with no stored
        // close, and a dropped deposit reads as a pure gain: the one thing TWR exists to prevent.
        var cashFlowByValuationDate = new Dictionary<DateOnly, decimal>();
        foreach (var flow in cashFlows)
        {
            var index = values.FindIndex(v => v.Date >= flow.Date);

            // index < 0 — dated after the last valuation, so no valuation reflects it either and
            // there is no sub-period for it to belong to. index == 0 — dated at or before the
            // opening valuation, which is the base of the chain rather than a sub-period, so no
            // return exists for it to distort. Both are correctly ignored rather than dropped.
            if (index <= 0)
            {
                continue;
            }

            var valuationDate = values[index].Date;
            cashFlowByValuationDate[valuationDate] =
                cashFlowByValuationDate.GetValueOrDefault(valuationDate, 0m) + flow.AmountUsd;
        }

        var dailyReturnsByYear = new SortedDictionary<int, List<decimal>>();

        for (var i = 1; i < values.Count; i++)
        {
            var previous = values[i - 1];
            var current = values[i];

            if (previous.MarketValueUsd == 0m)
            {
                // No capital was at risk over this sub-period — either the very first funding
                // event (nothing existed before it) or a position sitting fully closed between
                // trades. There is no rate to compute from a zero base; skip it and let the chain
                // resume from the next valuation rather than dividing by zero or inventing a 0%
                // "return" that would understate volatility either side of it.
                continue;
            }

            var cashFlow = cashFlowByValuationDate.GetValueOrDefault(current.Date, 0m);
            var dailyReturn = (current.MarketValueUsd - cashFlow) / previous.MarketValueUsd - 1m;

            var year = current.Date.Year;
            if (!dailyReturnsByYear.TryGetValue(year, out var returnsForYear))
            {
                returnsForYear = [];
                dailyReturnsByYear[year] = returnsForYear;
            }

            returnsForYear.Add(dailyReturn);
        }

        var results = new List<AnnualReturn>(dailyReturnsByYear.Count);
        foreach (var (year, returns) in dailyReturnsByYear)
        {
            var compounded = returns.Aggregate(1m, (acc, r) => acc * (1m + r));
            results.Add(new AnnualReturn(year, (compounded - 1m) * 100m));
        }

        return results;
    }
}
