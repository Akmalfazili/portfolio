using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;

namespace Portfolio.Infrastructure.Persistence.Seed;

/// <summary>
/// Fixed-Id seed assets applied via <c>HasData</c> in the initial migration. Covers the two
/// asset classes and every provider integration named in CLAUDE.md: two US equities, the SGX
/// listing Z74, and the three CoinGecko coins.
/// </summary>
public static class SeedData
{
    /// <summary>
    /// <c>HasData</c> requires literal, deterministic values — a moving "now" would make EF detect
    /// a model change on every build. The initial migration's own date is used, which is also
    /// honest: these six rows have existed since the schema did.
    /// </summary>
    private static readonly DateTimeOffset SeededAt = new(2026, 7, 26, 0, 0, 0, TimeSpan.Zero);

    public static readonly Asset[] Assets =
    [
        new()
        {
            Id = 1,
            Symbol = "AAPL",
            Name = "Apple Inc.",
            AssetClass = AssetClass.Stock,
            Exchange = "NASDAQ",
            Currency = "USD",
            QuoteProviderKind = QuoteProviderKind.TwelveData,
            ProviderSymbol = "AAPL",
            ProviderCoinId = null,
            IsActive = true,
            CreatedAt = SeededAt,
        },
        new()
        {
            Id = 2,
            Symbol = "MSFT",
            Name = "Microsoft Corporation",
            AssetClass = AssetClass.Stock,
            Exchange = "NASDAQ",
            Currency = "USD",
            QuoteProviderKind = QuoteProviderKind.TwelveData,
            ProviderSymbol = "MSFT",
            ProviderCoinId = null,
            IsActive = true,
            CreatedAt = SeededAt,
        },
        new()
        {
            Id = 3,
            Symbol = "Z74",
            Name = "Singapore Telecommunications Limited",
            AssetClass = AssetClass.Stock,
            Exchange = "SGX",
            Currency = "SGD",
            // Twelve Data's free tier cannot serve Z74 ("available starting with the Pro or
            // Venture plan"), so it is routed to Yahoo Finance instead. "Z74:XSES" was Twelve
            // Data's (dead, for this asset) syntax; "Z74.SI" is the form Yahoo's chart endpoint
            // resolves for the SGX listing.
            QuoteProviderKind = QuoteProviderKind.Yahoo,
            ProviderSymbol = "Z74.SI",
            ProviderCoinId = null,
            IsActive = true,
            CreatedAt = SeededAt,
        },
        new()
        {
            Id = 4,
            Symbol = "ETH",
            Name = "Ethereum",
            AssetClass = AssetClass.Crypto,
            Exchange = null,
            Currency = "USD",
            QuoteProviderKind = QuoteProviderKind.CoinGecko,
            ProviderSymbol = null,
            ProviderCoinId = "ethereum",
            IsActive = true,
            CreatedAt = SeededAt,
        },
        new()
        {
            Id = 5,
            Symbol = "AMP",
            Name = "Amp",
            AssetClass = AssetClass.Crypto,
            Exchange = null,
            Currency = "USD",
            QuoteProviderKind = QuoteProviderKind.CoinGecko,
            ProviderSymbol = null,
            ProviderCoinId = "amp-token",
            IsActive = true,
            CreatedAt = SeededAt,
        },
        new()
        {
            Id = 6,
            Symbol = "ANVL",
            Name = "Anvil",
            AssetClass = AssetClass.Crypto,
            Exchange = null,
            Currency = "USD",
            QuoteProviderKind = QuoteProviderKind.CoinGecko,
            ProviderSymbol = null,
            ProviderCoinId = "anvil",
            IsActive = true,
            CreatedAt = SeededAt,
        },
    ];
}
