namespace Portfolio.Application.Abstractions;

/// <summary>
/// One dividend ex-date event, with its per-share amount already converted to USD by the caller —
/// via <c>FxRateResolver</c>, at the ex-date's own historical rate, never today's — mirrors
/// <see cref="CostBasisTransaction"/>'s "FX resolved by the caller, calculator stays pure
/// arithmetic" split, so this calculator needs no FX fixtures to test the ex-date boundary itself.
/// </summary>
public sealed record DividendIncomeInput(
    DateOnly ExDate,
    decimal AmountPerShareNative,
    string Currency,
    decimal AmountPerShareUsd);

/// <summary>
/// One priced dividend payment line — the units actually entitled to it (see
/// <see cref="IDividendIncomeCalculator"/>'s remarks on the ex-date boundary) and the resulting USD
/// income, left unrounded (<c>DisplayRounding</c> is applied only at the DTO boundary, never here).
/// </summary>
public sealed record DividendIncomeLine(
    DateOnly ExDate,
    decimal AmountPerShareNative,
    string Currency,
    decimal UnitsHeldAtExDate,
    decimal IncomeUsd);

/// <summary>
/// Computes, for each dividend ex-date event, how many units of the asset were actually entitled
/// to that payment and the resulting USD income.
///
/// <para>A unit bought <i>on</i> the ex-date does not earn that dividend — the real market rule.
/// Entitlement is therefore net buys minus sells with <see cref="Domain.Entities.Transaction.TradeDate"/>
/// strictly <i>before</i> the ex-date, never on-or-before. A position at zero or negative units
/// (fully sold by the ex-date) earns nothing from that event — but the event is still returned as a
/// zero-income line, so a payment history can show the payment happened while the position was
/// closed rather than silently vanishing from the list.</para>
///
/// <para>These figures are <b>estimated from ex-date holdings</b>, not recorded cash actually
/// received: no withholding tax, no DRIP/scrip reinvestment, and no brokerage-reported payment date
/// is modelled — only the ex-date entitlement calculation described above.</para>
/// </summary>
public interface IDividendIncomeCalculator
{
    /// <summary>
    /// <paramref name="transactions"/> and <paramref name="dividendEvents"/> may be supplied in any
    /// order — both are sorted ascending internally. Returns one <see cref="DividendIncomeLine"/>
    /// per input event, in ex-date order. Returns an empty list when
    /// <paramref name="dividendEvents"/> is empty.
    /// </summary>
    IReadOnlyList<DividendIncomeLine> Calculate(
        IReadOnlyList<Domain.Entities.Transaction> transactions,
        IReadOnlyList<DividendIncomeInput> dividendEvents);
}
