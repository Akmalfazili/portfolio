namespace Portfolio.Application.Abstractions;

/// <summary>Total stock-portfolio market value, in USD, as of one date.</summary>
public sealed record DailyPortfolioValue(DateOnly Date, decimal MarketValueUsd);

/// <summary>
/// A net external cash flow into (positive) or out of (negative) the portfolio on one date, in
/// USD — a buy's cost (including fees) is a positive flow, a sell's net proceeds (after fees) is a
/// negative one. Same-date flows are summed by the calculator, so callers may supply one entry per
/// transaction.
/// </summary>
public sealed record PortfolioCashFlow(DateOnly Date, decimal AmountUsd);

/// <summary>Time-weighted return for one calendar year, as a percentage (5.2 means +5.2%).</summary>
public sealed record AnnualReturn(int Year, decimal TimeWeightedReturnPercent);

/// <summary>
/// Computes time-weighted return (TWR) per calendar year from a daily portfolio valuation series
/// and the external cash flows into/out of it — TWR is the agreed method precisely so that a
/// deposit (buying more) is not itself counted as a gain. Stocks only, by decision; the
/// orchestrating service is responsible for restricting the assets that feed this to
/// <c>AssetClass.Stock</c> before calling in.
/// </summary>
public interface IAnnualReturnCalculator
{
    /// <summary>
    /// Uses the "daily valuation" method: for each pair of consecutive valuation dates (t-1, t),
    /// the sub-period return is <c>(V(t) - CF(t)) / V(t-1) - 1</c>, where <c>CF(t)</c> is the net
    /// cash flow falling inside that sub-period. A flow need <em>not</em> be dated on a valuation
    /// date: each is attributed to the first valuation on or after its own date, so a trade dated a
    /// weekend or a market holiday still cancels correctly instead of being counted as a gain.
    /// This trusts the caller's dates completely: a flow dated earlier than the point its own
    /// asset can actually be reflected in <paramref name="dailyValues"/> — a trade preceding its
    /// own asset's first stored close — corrupts that sub-period, or, if it lands on or before the
    /// very first valuation, is silently absorbed into the opening balance instead of distorting a
    /// return (D50). The caller is responsible for dating each flow no earlier than the point its
    /// own asset can be reflected in the value series, never the raw trade date.
    /// Sub-period returns are geometrically linked (compounded, not summed)
    /// within each calendar year. A sub-period starting from a zero valuation (no position existed
    /// yet, or a position was fully closed) contributes no return for that step — there is no rate
    /// to compute from a zero base — and the chain simply resumes from the next valuation.
    /// <paramref name="dailyValues"/> and <paramref name="cashFlows"/> may be supplied in any
    /// order. Returns one <see cref="AnnualReturn"/> per calendar year that has at least one
    /// computable sub-period return, ordered by year ascending.
    /// </summary>
    IReadOnlyList<AnnualReturn> Calculate(
        IReadOnlyList<DailyPortfolioValue> dailyValues,
        IReadOnlyList<PortfolioCashFlow> cashFlows);
}
