using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Portfolio.Application.Abstractions;
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
    private static TwelveDataFxProvider CreateSut(HttpMessageHandler handler, ITwelveDataCreditThrottle? creditThrottle = null)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.twelvedata.com/") };
        var options = Options.Create(new TwelveDataOptions { ApiKey = "test-key-not-real" });
        return new TwelveDataFxProvider(
            httpClient, options, creditThrottle ?? AlwaysGrantingThrottle(), TimeProvider.System, NullLogger<TwelveDataFxProvider>.Instance);
    }

    private static ITwelveDataCreditThrottle AlwaysGrantingThrottle()
    {
        var throttle = Substitute.For<ITwelveDataCreditThrottle>();
        throttle.TryAcquireAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        return throttle;
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

        var result = await sut.GetHistoryAsync(
            "USD", "SGD", new DateOnly(2026, 7, 19), new DateOnly(2026, 7, 20), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Points.Should().HaveCount(2);
        result.Points[0].Date.Should().Be(new DateOnly(2026, 7, 19));
        result.Points[0].Rate.Should().Be(1.289500m);
        result.Points[1].Date.Should().Be(new DateOnly(2026, 7, 20));
        result.Points[1].Rate.Should().Be(1.290100m);
    }

    [Fact]
    public async Task GetHistoryAsync_NonSuccessStatus_ReturnsFailed_WithTheStatusCode()
    {
        // The false-success bug this guards against: a bare empty list here is indistinguishable
        // from a genuinely empty range, and let a 429-starved FX backfill report itself as a clean
        // success with fxRatePointsInserted: 0.
        var sut = CreateSut(new StubHttpMessageHandler(HttpStatusCode.TooManyRequests, "{}"));

        var result = await sut.GetHistoryAsync(
            "USD", "SGD", new DateOnly(2026, 7, 19), new DateOnly(2026, 7, 20), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Points.Should().BeEmpty();
        result.Error.Should().Be("Twelve Data returned HTTP 429.");
    }

    [Fact]
    public async Task GetHistoryAsync_ErrorPayload_ReturnsFailed_WithTheProviderMessage()
    {
        const string json = """{"code":400,"message":"symbol not found","status":"error"}""";

        var sut = CreateSut(new StubHttpMessageHandler(HttpStatusCode.OK, json));

        var result = await sut.GetHistoryAsync(
            "USD", "XYZ", new DateOnly(2026, 7, 19), new DateOnly(2026, 7, 20), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Points.Should().BeEmpty();
        result.Error.Should().Be("symbol not found");
    }

    [Fact]
    public async Task GetHistoryAsync_ThrottleDenies_ReturnsFailed_WithoutCallingTheProvider()
    {
        var throttle = Substitute.For<ITwelveDataCreditThrottle>();
        throttle.TryAcquireAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);

        var handler = new RecordingStubHttpMessageHandler(HttpStatusCode.OK, "{}");
        var sut = CreateSut(handler, throttle);

        var result = await sut.GetHistoryAsync(
            "USD", "SGD", new DateOnly(2026, 7, 19), new DateOnly(2026, 7, 20), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Twelve Data daily credit budget exhausted.");
        handler.Requests.Should().BeEmpty();
        await throttle.Received(1).TryAcquireAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSpotRateAsync_ThrottleDenies_ReturnsNull_WithoutCallingTheProvider()
    {
        var throttle = Substitute.For<ITwelveDataCreditThrottle>();
        throttle.TryAcquireAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);

        var handler = new RecordingStubHttpMessageHandler(HttpStatusCode.OK, "{}");
        var sut = CreateSut(handler, throttle);

        var result = await sut.GetSpotRateAsync("USD", "SGD", CancellationToken.None);

        result.Should().BeNull();
        handler.Requests.Should().BeEmpty();
    }
}
