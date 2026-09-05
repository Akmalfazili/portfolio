using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services.Calculators;

/// <summary>
/// Units held as of a given date: buys minus sells with <see cref="Transaction.TradeDate"/> at or
/// before that date. A second, independent definition of "quantity held" alongside
/// <c>CostBasisStep.QuantityHeld</c> — built directly from raw transactions rather than going
/// through <c>CostBasisTransactionFactory.ToUsd</c>, because the zakat report needs quantity alone
/// and a missing FX rate must not be able to fail a quantity calculation that never needed FX in
/// the first place (a stock's unit count is currency-agnostic).
///
/// <para><b>Only safe because something proves the two agree.</b> See
/// <c>QuantityAsOfAgreesWithCostBasisStepTests</c> — without that test this is a future divergence
/// waiting to happen.</para>
/// </summary>
public static class QuantityAsOf
{
    /// <summary>
    /// <paramref name="transactions"/> may be supplied in any order — sorted here by
    /// <see cref="Transaction.TradeDate"/> then <see cref="Transaction.Id"/>, the same stable
    /// ordering <see cref="Abstractions.ICostBasisCalculator"/>'s callers use, though the final total does not
    /// actually depend on order (addition is commutative) — the ordering exists only so a caller
    /// diffing intermediate output against <c>CostBasisStep</c> sees the same processing order.
    /// </summary>
    public static decimal Calculate(IReadOnlyList<Transaction> transactions, DateOnly date)
    {
        var quantity = 0m;

        foreach (var t in transactions
                     .Where(t => t.TradeDate <= date)
                     .OrderBy(t => t.TradeDate)
                     .ThenBy(t => t.Id))
        {
            quantity += t.Type == TransactionType.Buy ? t.Quantity : -t.Quantity;
        }

        return quantity;
    }
}
