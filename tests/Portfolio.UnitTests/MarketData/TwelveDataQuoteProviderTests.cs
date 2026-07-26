using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;
using Portfolio.Infrastructure.MarketData.TwelveData;
using Portfolio.UnitTests.TestSupport;

namespace Portfolio.UnitTests.MarketData;

/// <summary>
/// Exercises <see cref="TwelveDataQuoteProvider"/> against captured real response shapes — a
/// batched two-symbol <c>/quote</c> success, and the nested per-symbol error object Twelve Data
/// returns when one symbol in a batch fails. No test in this class makes a live network call.
/// </summary>
public sealed class TwelveDataQuoteProviderTests
{
    private static readonly Asset Aapl = new()
    {
        Id = 1,
        Symbol = "AAPL",
        Name = "Apple Inc.",
        AssetClass = AssetClass.Stock,
        Currency = "USD",
        QuoteProviderKind = QuoteProviderKind.TwelveData,
        ProviderSymbol = "AAPL",
    };

    private static readonly Asset Msft = new()
    {
        Id = 2,
        Symbol = "MSFT",
        Name = "Microsoft Corporation",
        AssetClass = AssetClass.Stock,
        Currency = "USD",
        QuoteProviderKind = QuoteProviderKind.TwelveData,
        ProviderSymbol = "MSFT",
    };

    private static TwelveDataQuoteProvider CreateSut(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.twelvedata.com/") };
        var options = Options.Create(new TwelveDataOptions { ApiKey = "test-key-not-real" });
        return new TwelveDataQuoteProvider(httpClient, options, TimeProvider.System, NullLogger<TwelveDataQuoteProvider>.Instance);
    }

    [Fact]
    public async Task GetQuotesAsync_ParsesBatchedTwoSymbolResponse_AsExactDecimal()
    {
        // Captured shape: batch of >1 symbols nests each quote object under its own key. Numeric
        // fields arrive as JSON strings ("close":"333.019989") — must parse to decimal exactly.
        const string json = """
        {
          "AAPL": {
            "symbol": "AAPL",
            "currency": "USD",
            "close": "210.020000",
            "timestamp": 1785060840
          },
          "MSFT": {
            "symbol": "MSFT",
            "currency": "USD",
            "close": "333.019989",
            "timestamp": 1785060840
          }
        }
        """;

        var sut = CreateSut(new StubHttpMessageHandler(HttpStatusCode.OK, json));

        var results = await sut.GetQuotesAsync([Aapl, Msft], CancellationToken.None);

        results.Should().HaveCount(2);
        results.Should().ContainSingle(r => r.AssetId == Aapl.Id && r.Success && r.Price == 210.020000m);
        results.Should().ContainSingle(r => r.AssetId == Msft.Id && r.Success && r.Price == 333.019989m);
    }

    [Fact]
    public async Task GetQuotesAsync_OneBadSymbolInBatch_DoesNotFailTheOthers()
    {
        // Captured shape: a per-symbol failure appears as the same {"status":"error"} object
        // nested under the failing symbol's key, alongside a sibling that still succeeds.
        const string json = """
        {
          "AAPL": {
            "symbol": "AAPL",
            "currency": "USD",
            "close": "210.020000",
            "timestamp": 1785060840
          },
          "MSFT": {
            "code": 404,
            "message": "This symbol is available starting with the Pro or Venture plan.",
            "status": "error"
          }
        }
        """;

        var sut = CreateSut(new StubHttpMessageHandler(HttpStatusCode.OK, json));

        var results = await sut.GetQuotesAsync([Aapl, Msft], CancellationToken.None);

        results.Should().HaveCount(2);
        results.Should().ContainSingle(r => r.AssetId == Aapl.Id && r.Success);
        var failed = results.Should().ContainSingle(r => r.AssetId == Msft.Id && !r.Success).Subject;
        failed.Error.Should().Contain("Pro or Venture plan");
    }

    [Fact]
    public async Task GetQuotesAsync_SingleSymbol_ParsesFlatUnwrappedResponse()
    {
        // Captured shape: a single-symbol request is NOT nested under a symbol key.
        const string json = """
        {
          "symbol": "AAPL",
          "currency": "USD",
          "close": "210.020000",
          "timestamp": 1785060840
        }
        """;

        var sut = CreateSut(new StubHttpMessageHandler(HttpStatusCode.OK, json));

        var results = await sut.GetQuotesAsync([Aapl], CancellationToken.None);

        results.Should().ContainSingle();
        results[0].Success.Should().BeTrue();
        results[0].Price.Should().Be(210.020000m);
        results[0].Currency.Should().Be("USD");
    }

    [Fact]
    public async Task GetQuotesAsync_TransportFailure_ReturnsFailureForEveryRequestedAsset_WithoutThrowing()
    {
        var sut = CreateSut(new StubHttpMessageHandler(HttpStatusCode.TooManyRequests, "{}"));

        var results = await sut.GetQuotesAsync([Aapl, Msft], CancellationToken.None);

        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => !r.Success);
    }
}
