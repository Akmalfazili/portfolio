using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;
using Portfolio.Infrastructure.MarketData.CoinGecko;
using Portfolio.UnitTests.TestSupport;

namespace Portfolio.UnitTests.MarketData;

/// <summary>
/// Exercises <see cref="CoinGeckoQuoteProvider"/> against the captured real keyless
/// <c>/simple/price</c> and <c>/market_chart/range</c> response shapes, including ANVL's
/// sub-cent price (<c>0.00042282</c>), which must survive as an exact decimal, never a rounded
/// double, and the keyless API's real 365-day history window limit. No test here makes a live
/// network call.
/// </summary>
public sealed class CoinGeckoQuoteProviderTests
{
    private static readonly Asset Ethereum = new()
    {
        Id = 4,
        Symbol = "ETH",
        Name = "Ethereum",
        AssetClass = AssetClass.Crypto,
        Currency = "USD",
        QuoteProviderKind = QuoteProviderKind.CoinGecko,
        ProviderCoinId = "ethereum",
    };

    private static readonly Asset Amp = new()
    {
        Id = 5,
        Symbol = "AMP",
        Name = "Amp",
        AssetClass = AssetClass.Crypto,
        Currency = "USD",
        QuoteProviderKind = QuoteProviderKind.CoinGecko,
        ProviderCoinId = "amp-token",
    };

    private static readonly Asset Anvil = new()
    {
        Id = 6,
        Symbol = "ANVL",
        Name = "Anvil",
        AssetClass = AssetClass.Crypto,
        Currency = "USD",
        QuoteProviderKind = QuoteProviderKind.CoinGecko,
        ProviderCoinId = "anvil",
    };

    private static CoinGeckoQuoteProvider CreateSut(HttpMessageHandler handler, CoinGeckoOptions? options = null)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.coingecko.com/api/v3/") };
        return new CoinGeckoQuoteProvider(
            httpClient,
            Options.Create(options ?? new CoinGeckoOptions()),
            TimeProvider.System,
            NullLogger<CoinGeckoQuoteProvider>.Instance);
    }

    [Fact]
    public async Task GetQuotesAsync_AllThreeCoinsInOneCall_SubCentPriceSurvivesExactly()
    {
        // Captured live shape from /simple/price?ids=ethereum,amp-token,anvil&vs_currencies=usd&include_last_updated_at=true
        const string json = """
        {
          "ethereum": { "usd": 1883.85, "last_updated_at": 1785060840 },
          "anvil": { "usd": 0.00042282, "last_updated_at": 1785060840 },
          "amp-token": { "usd": 0.00042122, "last_updated_at": 1785060840 }
        }
        """;

        var sut = CreateSut(new StubHttpMessageHandler(HttpStatusCode.OK, json));

        var results = await sut.GetQuotesAsync([Ethereum, Amp, Anvil], CancellationToken.None);

        results.Should().HaveCount(3);
        results.Should().ContainSingle(r => r.AssetId == Ethereum.Id && r.Price == 1883.85m);
        results.Should().ContainSingle(r => r.AssetId == Anvil.Id && r.Price == 0.00042282m);
        results.Should().ContainSingle(r => r.AssetId == Amp.Id && r.Price == 0.00042122m);
        results.Should().OnlyContain(r => r.Success && r.Currency == "USD");
    }

    [Fact]
    public async Task GetQuotesAsync_MissingCoinInResponse_FailsOnlyThatCoin()
    {
        const string json = """{ "ethereum": { "usd": 1883.85, "last_updated_at": 1785060840 } }""";

        var sut = CreateSut(new StubHttpMessageHandler(HttpStatusCode.OK, json));

        var results = await sut.GetQuotesAsync([Ethereum, Anvil], CancellationToken.None);

        results.Should().ContainSingle(r => r.AssetId == Ethereum.Id && r.Success);
        results.Should().ContainSingle(r => r.AssetId == Anvil.Id && !r.Success);
    }

    [Fact]
    public async Task GetHistoryAsync_WithinWindow_ParsesMarketChartRangePrices_AsExactDecimal_NeverThroughDouble()
    {
        // [unixMillis, price] pairs, captured live: [1784631600000, 0.0004059826574851183].
        const string json = """
        {
          "prices": [
            [1784995200000, 0.0004059826574851183],
            [1785081600000, 0.00042500]
          ]
        }
        """;

        var sut = CreateSut(new StubHttpMessageHandler(HttpStatusCode.OK, json));

        // A 1-day range is well inside the 365-day keyless window, so nothing is clamped.
        var result = await sut.GetHistoryAsync(
            Anvil, new DateOnly(2026, 7, 25), new DateOnly(2026, 7, 26), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Truncated.Should().BeFalse();
        result.Points.Should().HaveCount(2);
        result.Points.Should().Contain(p => p.Close == 0.0004059826574851183m);
        result.Points.Should().Contain(p => p.Close == 0.00042500m);
        result.Points.Should().OnlyContain(p => p.Currency == "USD");
    }

    [Fact]
    public async Task GetHistoryAsync_RangeOlderThan365Days_ClampsRequest_AndReportsTruncation()
    {
        const string json = """{"prices":[[1784995200000,0.00042282]]}""";
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, json);

        var sut = CreateSut(handler, new CoinGeckoOptions { ApiKey = null, KeylessMaxHistoryDays = 365 });

        var to = new DateOnly(2026, 7, 26);
        var requestedFrom = to.AddYears(-2); // 730+ days back — over the keyless window.

        var result = await sut.GetHistoryAsync(Anvil, requestedFrom, to, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Truncated.Should().BeTrue();
        result.RequestedFrom.Should().Be(requestedFrom);
        result.EffectiveFrom.Should().Be(to.AddDays(-364));

        // The clamp must actually reach the outgoing request, not just the reported result.
        handler.LastRequest.Should().NotBeNull();
        var expectedFromUnix = new DateTimeOffset(result.EffectiveFrom.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        handler.LastRequest!.RequestUri!.Query.Should().Contain($"from={expectedFromUnix}");
    }

    [Fact]
    public async Task GetHistoryAsync_EmptyApiKey_IsTreatedAsKeyless_AndStaysClamped()
    {
        // D32: an empty-but-present ApiKey (the shape a compose .env with a blank
        // CoinGecko__ApiKey= line produces) must be treated exactly like a null one — still
        // clamped to the keyless window, not treated as "a key is configured".
        const string json = """{"prices":[[1784995200000,0.00042282]]}""";

        var sut = CreateSut(
            new StubHttpMessageHandler(HttpStatusCode.OK, json),
            new CoinGeckoOptions { ApiKey = "", KeylessMaxHistoryDays = 365 });

        var to = new DateOnly(2026, 7, 26);
        var requestedFrom = to.AddYears(-2);

        var result = await sut.GetHistoryAsync(Anvil, requestedFrom, to, CancellationToken.None);

        result.Truncated.Should().BeTrue();
        result.EffectiveFrom.Should().Be(to.AddDays(-364));
    }

    [Fact]
    public async Task GetHistoryAsync_ConfiguredApiKey_LiftsTheClamp()
    {
        const string json = """{"prices":[[1784995200000,0.00042282]]}""";

        var sut = CreateSut(
            new StubHttpMessageHandler(HttpStatusCode.OK, json),
            new CoinGeckoOptions { ApiKey = "pro-key", KeylessMaxHistoryDays = 365 });

        var to = new DateOnly(2026, 7, 26);
        var requestedFrom = to.AddYears(-2);

        var result = await sut.GetHistoryAsync(Anvil, requestedFrom, to, CancellationToken.None);

        result.Truncated.Should().BeFalse();
        result.EffectiveFrom.Should().Be(requestedFrom);
    }

    [Fact]
    public async Task GetHistoryAsync_RealCaptured401Body_IsReportedAsFailure_NotSwallowedAsEmpty()
    {
        // Captured live: a keyless request older than 365 days 401s with this exact body.
        const string errorJson = """
        {"error":{"status":{"error_code":10012,"error_message":"Your request exceeds the allowed time range. Public API users are limited to querying historical data within the past 365 days. Upgrade to a paid plan..."}}}
        """;

        var sut = CreateSut(new StubHttpMessageHandler(HttpStatusCode.Unauthorized, errorJson));

        var result = await sut.GetHistoryAsync(
            Anvil, new DateOnly(2020, 1, 1), new DateOnly(2026, 7, 26), CancellationToken.None);

        // Even though the request was clamped before sending, if the provider still rejects it
        // (e.g. clamp/provider disagreement), that must surface as an explicit failure — not the
        // same bare empty list a truncation or a "nothing requested" case would produce.
        result.Success.Should().BeFalse();
        result.Points.Should().BeEmpty();
        result.Error.Should().Contain("365 days");
    }
}
