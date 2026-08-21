using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Portfolio.Application.Abstractions;
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

    private static TwelveDataQuoteProvider CreateSut(HttpMessageHandler handler, ITwelveDataCreditThrottle? creditThrottle = null)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.twelvedata.com/") };
        var options = Options.Create(new TwelveDataOptions { ApiKey = "test-key-not-real" });
        return new TwelveDataQuoteProvider(
            httpClient, options, creditThrottle ?? AlwaysGrantingThrottle(), TimeProvider.System, NullLogger<TwelveDataQuoteProvider>.Instance);
    }

    /// <summary>A throttle fake that grants every request instantly — the default for tests that
    /// are not themselves about the throttle's pacing/budget behaviour.</summary>
    private static ITwelveDataCreditThrottle AlwaysGrantingThrottle()
    {
        var throttle = Substitute.For<ITwelveDataCreditThrottle>();
        throttle.TryAcquireAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        return throttle;
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

    /// <summary>
    /// D38, reproduced and fixed. Before the fix, <c>GetQuotesAsync</c> built exactly ONE
    /// <c>/quote</c> request carrying every symbol in the batch — with 21 active Twelve Data
    /// symbols (the live measured count) that single request spends 21 credits at once against a
    /// per-minute limit of 8, so it 429s every time even though it is the first and only request
    /// in its minute. Against the pre-fix code (one request, no chunking, no throttle call at
    /// all) this test would have failed both assertions below: exactly one request would have been
    /// made, and it would have carried all 21 symbols. Post-fix, the batch is chunked to the
    /// credit throttle's per-minute limit and every chunk is acquired through the shared throttle
    /// before it is sent.
    /// </summary>
    [Fact]
    public async Task GetQuotesAsync_TwentyOneSymbols_ChunksIntoRequestsOfAtMostThePerMinuteCreditLimit()
    {
        var assets = Enumerable.Range(0, 21)
            .Select(i => new Asset
            {
                Id = i + 1,
                Symbol = $"SYM{i}",
                Name = $"Symbol {i}",
                AssetClass = AssetClass.Stock,
                Currency = "USD",
                QuoteProviderKind = QuoteProviderKind.TwelveData,
                ProviderSymbol = $"SYM{i}",
            })
            .ToList();

        // One combined payload every chunk's response is drawn from — each chunk only looks up
        // its own symbols' keys, so the extras are harmlessly ignored.
        var quoteObjects = assets.Select(a =>
            $$"""
            "{{a.ProviderSymbol}}": { "symbol": "{{a.ProviderSymbol}}", "currency": "USD", "close": "100.00", "timestamp": 1785060840 }
            """);
        var json = "{" + string.Join(',', quoteObjects) + "}";

        var handler = new RecordingStubHttpMessageHandler(HttpStatusCode.OK, json);
        var throttle = AlwaysGrantingThrottle();
        var sut = CreateSut(handler, throttle);

        var results = await sut.GetQuotesAsync(assets, CancellationToken.None);

        results.Should().HaveCount(21);
        results.Should().OnlyContain(r => r.Success);

        // The core D38 assertion: never one request carrying more than the per-minute credit
        // limit's worth of symbols.
        handler.Requests.Should().HaveCount(3); // ceil(21 / 8) = 3
        foreach (var request in handler.Requests)
        {
            var symbolCount = Uri.UnescapeDataString(request.RequestUri!.Query)
                .Split("symbol=")[1]
                .Split('&')[0]
                .Split(',')
                .Length;
            symbolCount.Should().BeLessThanOrEqualTo(8);
        }

        // Every chunk went through the shared throttle before being sent, with its own chunk size
        // (8, 8, 5) — never the full batch size in one call.
        await throttle.Received(2).TryAcquireAsync(8, Arg.Any<CancellationToken>());
        await throttle.Received(1).TryAcquireAsync(5, Arg.Any<CancellationToken>());
        await throttle.DidNotReceive().TryAcquireAsync(21, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetQuotesAsync_ThrottleDeniesAChunk_ReportsBudgetExhausted_WithoutCallingTheProvider()
    {
        var throttle = Substitute.For<ITwelveDataCreditThrottle>();
        throttle.TryAcquireAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);

        var handler = new RecordingStubHttpMessageHandler(HttpStatusCode.OK, "{}");
        var sut = CreateSut(handler, throttle);

        var results = await sut.GetQuotesAsync([Aapl, Msft], CancellationToken.None);

        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => !r.Success && r.Error == "Twelve Data daily credit budget exhausted.");
        handler.Requests.Should().BeEmpty("a denied chunk must never spend an HTTP call");
    }
}
