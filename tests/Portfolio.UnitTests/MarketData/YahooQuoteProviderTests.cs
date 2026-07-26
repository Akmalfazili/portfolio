using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;
using Portfolio.Infrastructure.MarketData.Yahoo;
using Portfolio.UnitTests.TestSupport;

namespace Portfolio.UnitTests.MarketData;

/// <summary>
/// Exercises <see cref="YahooQuoteProvider"/> against the captured real chart response shape for
/// Z74.SI. Yahoo is unofficial and undocumented, so every failure path here must degrade to a
/// failed/empty result rather than throw — a Z74 outage must never take down the rest of a
/// refresh. No test here makes a live network call.
/// </summary>
public sealed class YahooQuoteProviderTests
{
    private static readonly Asset Z74 = new()
    {
        Id = 3,
        Symbol = "Z74",
        Name = "Singapore Telecommunications Limited",
        AssetClass = AssetClass.Stock,
        Exchange = "SGX",
        Currency = "SGD",
        QuoteProviderKind = QuoteProviderKind.Yahoo,
        ProviderSymbol = "Z74.SI",
    };

    private static YahooQuoteProvider CreateSut(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://query1.finance.yahoo.com/") };
        return new YahooQuoteProvider(httpClient, TimeProvider.System, NullLogger<YahooQuoteProvider>.Instance);
    }

    [Fact]
    public async Task GetQuotesAsync_ParsesRegularMarketPrice_InSgd()
    {
        // Captured live shape from /v8/finance/chart/Z74.SI?range=5d&interval=1d
        const string json = """
        {
          "chart": {
            "result": [
              {
                "meta": {
                  "currency": "SGD",
                  "exchangeName": "SES",
                  "regularMarketPrice": 4.39,
                  "regularMarketTime": 1785060840
                },
                "timestamp": [1785060840],
                "indicators": { "quote": [ { "close": [4.39] } ] }
              }
            ],
            "error": null
          }
        }
        """;

        var sut = CreateSut(new StubHttpMessageHandler(HttpStatusCode.OK, json));

        var results = await sut.GetQuotesAsync([Z74], CancellationToken.None);

        results.Should().ContainSingle();
        results[0].Success.Should().BeTrue();
        results[0].Price.Should().Be(4.39m);
        results[0].Currency.Should().Be("SGD");
    }

    [Fact]
    public async Task GetQuotesAsync_Http403_ReturnsFailure_WithoutThrowing()
    {
        var sut = CreateSut(new StubHttpMessageHandler(HttpStatusCode.Forbidden, "Forbidden"));

        var results = await sut.GetQuotesAsync([Z74], CancellationToken.None);

        results.Should().ContainSingle();
        results[0].Success.Should().BeFalse();
    }

    [Fact]
    public async Task GetHistoryAsync_SkipsNullCloses_AndOrdersAscendingByDate()
    {
        const string json = """
        {
          "chart": {
            "result": [
              {
                "meta": { "currency": "SGD", "exchangeName": "SES", "regularMarketPrice": 4.39 },
                "timestamp": [1784995200, 1785081600, 1785168000],
                "indicators": { "quote": [ { "close": [4.35, null, 4.440000057220459] } ] }
              }
            ],
            "error": null
          }
        }
        """;

        var sut = CreateSut(new StubHttpMessageHandler(HttpStatusCode.OK, json));

        var result = await sut.GetHistoryAsync(
            Z74, new DateOnly(2026, 7, 24), new DateOnly(2026, 7, 26), CancellationToken.None);

        // Middle (null-close) point is a non-trading day and must be skipped, not zero-filled.
        // The third point's raw JSON value (4.440000057220459) is genuine Yahoo float-round-trip
        // noise — captured live — and must come out rounded to 4.44, not stored verbatim.
        // Yahoo has no known history-window limit (unlike CoinGecko's keyless 365-day cap), so
        // nothing here should ever be truncated.
        result.Success.Should().BeTrue();
        result.Truncated.Should().BeFalse();
        var points = result.Points;
        points.Should().HaveCount(2);
        points.Should().OnlyContain(p => p.Currency == "SGD");
        points[0].Close.Should().Be(4.35m);
        points[1].Close.Should().Be(4.44m);
        (points[0].Date < points[1].Date).Should().BeTrue();
    }
}
