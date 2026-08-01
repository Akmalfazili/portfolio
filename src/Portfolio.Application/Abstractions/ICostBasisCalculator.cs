using Portfolio.Domain.Enums;

namespace Portfolio.Application.Abstractions;

/// <summary>
/// One buy/sell fill already converted to USD — <see cref="GrossAmountUsd"/> is
/// <c>Quantity * PricePerUnit</c> converted at the historical FX rate for <see cref="TradeDate"/>
/// (1:1 for USD-native assets), and <see cref="FeesUsd"/> is the fee converted the same way.
/// <see cref="ICostBasisCalculator"/> is deliberately currency-agnostic — all FX resolution
/// happens once, in the caller, via <c>FxRateResolver</c> — so the calculator itself is pure
/// arithmetic and trivially testable without any FX fixtures.
/// </summary>
public sealed record CostBasisTransaction(
    DateOnly TradeDate,
    TransactionType Type,
    decimal Quantity,
    decimal GrossAmountUsd,
    decimal FeesUsd);

/// <summary>
/// Running cost-basis state immediately after one transaction has been applied. A step per
/// transaction (rather than only a final snapshot) is what lets <c>PerformanceSeriesBuilder</c>
/// answer "what was the cost basis and quantity held as of date X" for any date, not just today.
/// </summary>
public sealed record CostBasisStep(
    DateOnly TradeDate,
    decimal QuantityHeld,
    decimal CostBasisUsd,
    decimal RealizedPnlUsd);

/// <summary>
/// Computes running cost basis, currently-held quantity, and realised P&amp;L from a chronological
/// list of buy/sell transactions (already USD-denominated — see <see cref="CostBasisTransaction"/>).
/// Behind an interface, per the recorded decision, so a FIFO calculator can be dropped in later
/// without any caller changing; <c>AverageCostCalculator</c> is the only implementation today.
/// </summary>
public interface ICostBasisCalculator
{
    /// <summary>
    /// <paramref name="transactions"/> may be supplied in any order — the calculator sorts by
    /// <see cref="CostBasisTransaction.TradeDate"/> itself (a stable sort, so equal dates keep the
    /// caller's relative order — pass same-day transactions pre-ordered, e.g. by transaction id,
    /// if that order matters). Returns one <see cref="CostBasisStep"/> per input transaction, in
    /// the order applied. Returns an empty list for an empty input.
    /// </summary>
    IReadOnlyList<CostBasisStep> Calculate(IReadOnlyList<CostBasisTransaction> transactions);
}
