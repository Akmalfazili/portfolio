using Portfolio.Application.Abstractions;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services.Calculators;

/// <summary>
/// Average-cost implementation of <see cref="ICostBasisCalculator"/>. On a buy, the transaction's
/// gross amount and fees are both capitalised into the running cost basis (fees increase the
/// average cost per unit). On a sell, the units sold are costed out at the average cost per unit
/// immediately before the sale, fees reduce the sale's net proceeds (not the remaining basis —
/// they relate to the units that just left, not the units still held), and the difference is
/// booked as realised P&amp;L.
/// </summary>
public sealed class AverageCostCalculator : ICostBasisCalculator
{
    public IReadOnlyList<CostBasisStep> Calculate(IReadOnlyList<CostBasisTransaction> transactions)
    {
        if (transactions.Count == 0)
        {
            return [];
        }

        // OrderBy is a stable sort, so transactions sharing a TradeDate keep the caller's
        // original relative order rather than being reshuffled.
        var ordered = transactions.OrderBy(t => t.TradeDate).ToList();

        var steps = new List<CostBasisStep>(ordered.Count);
        var quantity = 0m;
        var costBasisUsd = 0m;
        var realizedPnlUsd = 0m;

        foreach (var t in ordered)
        {
            if (t.Type == TransactionType.Buy)
            {
                quantity += t.Quantity;
                costBasisUsd += t.GrossAmountUsd + t.FeesUsd;
            }
            else
            {
                var averageCostPerUnit = quantity == 0m ? 0m : costBasisUsd / quantity;
                var costOfUnitsSold = averageCostPerUnit * t.Quantity;
                var netProceeds = t.GrossAmountUsd - t.FeesUsd;

                realizedPnlUsd += netProceeds - costOfUnitsSold;
                quantity -= t.Quantity;
                costBasisUsd -= costOfUnitsSold;

                // A sell that closes the position exactly should leave both at precisely zero,
                // but decimal division/multiplication can leave a sub-unit remainder (e.g.
                // averageCostPerUnit carrying more implied precision than costBasisUsd /
                // quantity can exactly represent) — snap to zero rather than let a fully closed
                // position show a phantom fractional quantity or cost basis.
                if (quantity == 0m)
                {
                    costBasisUsd = 0m;
                }
            }

            steps.Add(new CostBasisStep(t.TradeDate, quantity, costBasisUsd, realizedPnlUsd));
        }

        return steps;
    }
}
