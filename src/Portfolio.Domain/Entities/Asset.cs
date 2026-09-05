using Portfolio.Domain.Enums;

namespace Portfolio.Domain.Entities;

/// <summary>
/// A tradeable stock or crypto asset. <see cref="AssetClass"/> is the field that keeps stocks
/// and crypto segregated everywhere else in the system — never query across both classes.
/// </summary>
public class Asset
{
    public int Id { get; set; }

    /// <summary>Display ticker, e.g. "AAPL", "Z74", "ANVL".</summary>
    public required string Symbol { get; set; }

    public required string Name { get; set; }

    public AssetClass AssetClass { get; set; }

    /// <summary>Exchange the asset trades on, e.g. "NASDAQ", "SGX". Null for crypto.</summary>
    public string? Exchange { get; set; }

    /// <summary>ISO 4217 currency the asset natively trades in, e.g. "USD", "SGD".</summary>
    public required string Currency { get; set; }

    /// <summary>
    /// Which market-data provider quotes this asset. The explicit dispatch key for
    /// <c>IQuoteProviderRouter</c> — stocks are no longer a single provider (Twelve Data covers
    /// US equities, Yahoo Finance covers SGX), so <see cref="AssetClass"/> alone cannot decide.
    /// </summary>
    public QuoteProviderKind QuoteProviderKind { get; set; }

    /// <summary>
    /// The provider-specific symbol: Twelve Data form (e.g. "AAPL") when
    /// <see cref="QuoteProviderKind"/> is <see cref="QuoteProviderKind.TwelveData"/>, or the
    /// Yahoo Finance form (e.g. "Z74.SI") when it is <see cref="QuoteProviderKind.Yahoo"/>.
    /// Null for crypto assets, which are keyed by <see cref="ProviderCoinId"/> instead.
    /// </summary>
    public string? ProviderSymbol { get; set; }

    /// <summary>CoinGecko coin id, e.g. "ethereum", "amp-token", "anvil". Null for stocks.</summary>
    public string? ProviderCoinId { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Calendar month of this company's financial year end, 1-12. Null means "not configured" —
    /// a distinct, reported state (see <c>ZakatAssetStatus.FiscalYearEndNotConfigured</c>), never
    /// silently assumed to be 31 December. Always null for <see cref="Domain.Enums.AssetClass.Crypto"/>,
    /// which has no financial year. Either both this and <see cref="FiscalYearEndDay"/> are set, or
    /// both are null — enforced at write time in <c>AssetService</c>, not by a database constraint.
    /// A recurring month/day rather than a stored date: it is self-maintaining across years rather
    /// than needing re-entry, at the cost of drifting by a few days for a handful of 52/53-week
    /// fiscal calendars — see zakat.md §8 for which assets that affects and why the drift is
    /// accepted.
    /// </summary>
    public int? FiscalYearEndMonth { get; set; }

    /// <summary>
    /// Day of month of this company's financial year end, 1-31. See <see cref="FiscalYearEndMonth"/>
    /// for the null/pairing rules. 29 is allowed even though most years are not leap years — it is
    /// clamped to 28 February at resolution time (<c>FiscalYearEndResolver</c>), never rejected at
    /// write time. A day that is never valid for its month (2/30, 4/31, ...) is rejected at write
    /// time instead.
    /// </summary>
    public int? FiscalYearEndDay { get; set; }

    /// <summary>
    /// When this asset was added. Exists for D27: a well-formed but <i>wrong</i> provider
    /// identifier (<c>APPL</c> for <c>AAPL</c>, or a CoinGecko id that does not exist) is accepted
    /// at creation and then renders "Awaiting price" forever, giving no hint that the record rather
    /// than the market is the cause. Verifying the symbol against the provider costs a credit per
    /// creation and needs its own provider-unavailable design, so instead the age of an asset that
    /// has still never received any price is surfaced — which needs no provider call at all, and
    /// separates "added a minute ago, the next cycle has not run" from "added last week and the
    /// symbol is wrong".
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();

    public ICollection<PriceHistory> PriceHistories { get; set; } = new List<PriceHistory>();

    public PriceQuote? PriceQuote { get; set; }

    /// <summary>Dividend payment history — stocks only, never populated for crypto.</summary>
    public ICollection<DividendEvent> DividendEvents { get; set; } = new List<DividendEvent>();

    /// <summary>Dividend backfill state — stocks only, never populated for crypto.</summary>
    public AssetDividendState? DividendState { get; set; }
}
