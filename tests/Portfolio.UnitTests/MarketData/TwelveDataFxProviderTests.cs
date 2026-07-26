using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Portfolio.Infrastructure.MarketData.TwelveData;
using Portfolio.UnitTests.TestSupport;

namespace Portfolio.UnitTests.MarketData;

/// <summary>
/// Twelve Data is inconsistent between endpoints: <c>/quote</c> encodes numerics as JSON strings,
/// but <c>/exchange_rate</c> returns a bare JSON number (captured live:
/// <c>{"symbol":"USD/SGD","rate":1.29073,"timestamp":1785060840}</c>). This must still land in a
/// decimal exactly. No test here makes a live network call.
/// </summary>
public sealed class TwelveDataFxProviderTests
{
    private static TwelveDataFxProvider CreateSut(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.twelvedata.com/") };
        var options = Options.Create(new TwelveDataOptions { ApiKey = "test-key-not-real" });
        return new TwelveDataFxProvider(httpClient, options, TimeProvider.System, NullLogger<TwelveDataFxProvider>.Instance);
    }

    [Fact]
    public async Task GetSpotRateAsync_ParsesBareNumericRate_AsExactDecimal()
    {
        const string json = """{"symbol":"USD/SGD","rate":1.29073,"timestamp":1785060840}""";

        var sut = CreateSut(new StubHttpMessageHandler(HttpStatusCode.OK, json));

        var result = await sut.GetSpotRateAsync("USD", "SGD", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Rate.Should().Be(1.29073m);
        result.AsOf.ToUnixTimeSeconds().Should().Be(1785060840);
    }

    [Fact]
    public async Task GetSpotRateAsync_ErrorBody_ReturnsNull_WithoutThrowing()
    {
        const string json = """{"code":404,"message":"symbol not found","status":"error"}""";

        var sut = CreateSut(new StubHttpMessageHandler(HttpStatusCode.OK, json));

        var result = await sut.GetSpotRateAsync("USD", "XYZ", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetHistoryAsync_ParsesTimeSeriesCloses_AscendingByDate()
    {
        const string json = """
        {
          "meta": { "symbol": "USD/SGD", "currency": "SGD" },
          "values": [
            { "datetime": "2026-07-20", "close": "1.290100" },
            { "datetime": "2026-07-19", "close": "1.289500" }
          ],
          "status": "ok"
        }
        """;

        var sut = CreateSut(new StubHttpMessageHandler(HttpStatusCode.OK, json));

        var points = await sut.GetHistoryAsync(
            "USD", "SGD", new DateOnly(2026, 7, 19), new DateOnly(2026, 7, 20), CancellationToken.None);

        points.Should().HaveCount(2);
        points[0].Date.Should().Be(new DateOnly(2026, 7, 19));
        points[0].Rate.Should().Be(1.289500m);
        points[1].Date.Should().Be(new DateOnly(2026, 7, 20));
        points[1].Rate.Should().Be(1.290100m);
    }
}
