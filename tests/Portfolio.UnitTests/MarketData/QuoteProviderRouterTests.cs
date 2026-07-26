using FluentAssertions;
using NSubstitute;
using Portfolio.Application.Abstractions;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;
using Portfolio.Infrastructure.MarketData;

namespace Portfolio.UnitTests.MarketData;

/// <summary>
/// The router's dispatch key is <see cref="Asset.QuoteProviderKind"/>, not <c>AssetClass</c> —
/// stocks alone now span two providers (Twelve Data for US equities, Yahoo for SGX), so
/// <c>AssetClass.Stock</c> is not enough on its own to pick a provider.
/// </summary>
public sealed class QuoteProviderRouterTests
{
    private static IQuoteProvider FakeProvider(QuoteProviderKind kind)
    {
        var provider = Substitute.For<IQuoteProvider>();
        provider.Kind.Returns(kind);
        return provider;
    }

    [Fact]
    public void GetProvider_RoutesBothStockAssets_ToTheirOwnProvider_NotByAssetClassAlone()
    {
        var twelveData = FakeProvider(QuoteProviderKind.TwelveData);
        var yahoo = FakeProvider(QuoteProviderKind.Yahoo);
        var coinGecko = FakeProvider(QuoteProviderKind.CoinGecko);
        var router = new QuoteProviderRouter([twelveData, yahoo, coinGecko]);

        var aapl = new Asset { Id = 1, Symbol = "AAPL", Name = "Apple", AssetClass = AssetClass.Stock, Currency = "USD", QuoteProviderKind = QuoteProviderKind.TwelveData, ProviderSymbol = "AAPL" };
        var z74 = new Asset { Id = 3, Symbol = "Z74", Name = "Singtel", AssetClass = AssetClass.Stock, Currency = "SGD", QuoteProviderKind = QuoteProviderKind.Yahoo, ProviderSymbol = "Z74.SI" };

        router.GetProvider(aapl).Should().BeSameAs(twelveData);
        router.GetProvider(z74).Should().BeSameAs(yahoo);
    }

    [Fact]
    public void GetProvider_NoProviderRegisteredForKind_Throws()
    {
        var router = new QuoteProviderRouter([]);
        var asset = new Asset { Id = 1, Symbol = "AAPL", Name = "Apple", AssetClass = AssetClass.Stock, Currency = "USD", QuoteProviderKind = QuoteProviderKind.TwelveData };

        var act = () => router.GetProvider(asset);

        act.Should().Throw<InvalidOperationException>();
    }
}
