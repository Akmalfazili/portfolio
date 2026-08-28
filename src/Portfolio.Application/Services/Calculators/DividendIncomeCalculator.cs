using Portfolio.Application.Abstractions;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services.Calculators;

/// <summary>See <see cref="IDividendIncomeCalculator"/>.</summary>
public sealed class DividendIncomeCalculator : IDividendIncomeCalculator
{
    public IReadOnlyList<DividendIncomeLine> Calculate(
        IReadOnlyList<Transaction> transactions, IReadOnlyList<DividendIncomeInput> dividendEvents)
    {
        if (dividendEvents.Count == 0)
        {
            return [];
        }

        // Stable sort, same convention as AverageCostCalculator — same-TradeDate transactions keep
        // the caller's relative order (irrelevant to the running quantity total below, since a buy
        // and a sell on the same day still net out the same way regardless of which is applied
        // first, but kept for consistency with the rest of the codebase).
        var orderedTransactions = transactions.OrderBy(t => t.TradeDate).ToList();
        var orderedEvents = dividendEvents.OrderBy(e => e.ExDate).ToList();

        var lines = new List<DividendIncomeLine>(orderedEvents.Count);
        var quantity = 0m;
        var txIndex = 0;

        foreach (var evt in orderedEvents)
        {
            // Strictly before the ex-date — a buy that trades ON the ex-date does not earn that
            // payment. Cursor only ever advances, so events must already be in ascending order
            // (enforced above) for this single merge-style pass to be correct.
            while (txIndex < orderedTransactions.Count && orderedTransactions[txIndex].TradeDate < evt.ExDate)
            {
                quantity += orderedTransactions[txIndex].Type == TransactionType.Buy
                    ? orderedTransactions[txIndex].Quantity
                    : -orderedTransactions[txIndex].Quantity;
                txIndex++;
            }

            if (quantity <= 0m)
            {
                // No entitlement (never held, or fully sold by the ex-date) — the event still
                // appears in the payment history at zero income, rather than vanishing, so a
                // caller can see the payment happened while the position was closed.
                lines.Add(new DividendIncomeLine(evt.ExDate, evt.AmountPerShareNative, evt.Currency, 0m, 0m));
                continue;
            }

            lines.Add(new DividendIncomeLine(
                evt.ExDate,
                evt.AmountPerShareNative,
                evt.Currency,
                quantity,
                quantity * evt.AmountPerShareUsd));
        }

        return lines;
    }
}
