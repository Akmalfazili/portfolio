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

    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();

    public ICollection<PriceHistory> PriceHistories { get; set; } = new List<PriceHistory>();

    public PriceQuote? PriceQuote { get; set; }
}
