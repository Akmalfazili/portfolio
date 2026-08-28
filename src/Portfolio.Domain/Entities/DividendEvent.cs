namespace Portfolio.Domain.Entities;

/// <summary>
/// One dividend ex-date payment for a <see cref="Domain.Entities.Asset"/>, sourced from Yahoo
/// Finance (see <c>Infrastructure.MarketData.Yahoo.YahooDividendSymbolResolver</c>) — the only
/// free, keyless source of dividend history for this project, and used for every stock regardless
/// of which provider quotes its price. Unique per (AssetId, ExDate), mirroring
/// <see cref="PriceHistory"/>'s (AssetId, Date) index.
///
/// Stocks only — crypto pays no dividends and is out of scope entirely (never written for a
/// <see cref="Enums.AssetClass.Crypto"/> asset).
///
/// These figures are <b>estimated from ex-date holdings</b>, not recorded cash actually received:
/// no withholding tax, no DRIP/scrip reinvestment, no brokerage-reported payment date is modelled.
/// </summary>
public class DividendEvent
{
    public int Id { get; set; }

    public int AssetId { get; set; }

    public Asset? Asset { get; set; }

    /// <summary>
    /// The ex-dividend date. A unit bought <i>on</i> this date does not earn this payment — the
    /// real market rule <see cref="Services.Calculators.DividendIncomeCalculator"/> (in
    /// Portfolio.Application) enforces via a strict "TradeDate &lt; ExDate" comparison.
    /// </summary>
    public DateOnly ExDate { get; set; }

    /// <summary>Per-share payment amount, in <see cref="Currency"/>.</summary>
    public decimal AmountPerShare { get; set; }

    /// <summary>ISO 4217 currency the payment was declared in.</summary>
    public required string Currency { get; set; }
}
