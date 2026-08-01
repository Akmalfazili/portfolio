using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;
using Portfolio.Infrastructure.Persistence;
using Portfolio.UnitTests.TestSupport;

namespace Portfolio.UnitTests.Services;

/// <summary>
/// <see cref="PriceBackfillService"/> must be idempotent against the unique indexes on
/// <c>(AssetId, Date)</c> and <c>(Date, Base, Quote)</c> (re-running must not throw or
/// duplicate), and must bound how many upstream calls one run makes so a multi-year backfill
/// cannot blow a provider's daily credit budget in a single invocation.
/// </summary>
public sealed class PriceBackfillServiceTests : IDisposable
{
    private readonly PortfolioDbContext _db;
    private readonly FixedTimeProvider _timeProvider;
    private readonly Asset _aapl;
    private readonly Asset _z74;

    public PriceBackfillServiceTests()
    {
        var options = new DbContextOptionsBuilder<PortfolioDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _db = new PortfolioDbContext(options);

        _aapl = new Asset
        {
            Id = 1,
            Symbol = "AAPL",
            Name = "Apple Inc.",
            AssetClass = AssetClass.Stock,
            Currency = "USD",
            QuoteProviderKind = QuoteProviderKind.TwelveData,
            ProviderSymbol = "AAPL",
        };
        _z74 = new Asset
        {
            Id = 3,
            Symbol = "Z74",
            Name = "Singtel",
            AssetClass = AssetClass.Stock,
            Currency = "SGD",
            QuoteProviderKind = QuoteProviderKind.Yahoo,
            ProviderSymbol = "Z74.SI",
        };
        _db.Assets.AddRange(_aapl, _z74);

        _db.Transactions.Add(new Transaction
        {
            AssetId = _aapl.Id,
            Type = TransactionType.Buy,
            TradeDate = new DateOnly(2026, 7, 20),
            Quantity = 10,
            PricePerUnit = 200m,
            Fees = 1m,
            Currency = "USD",
        });
        _db.Transactions.Add(new Transaction
        {
            AssetId = _z74.Id,
            Type = TransactionType.Buy,
            TradeDate = new DateOnly(2026, 7, 20),
            Quantity = 100,
            PricePerUnit = 4m,
            Fees = 1m,
            Currency = "SGD",
        });
        _db.SaveChanges();

        _timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 7, 26, 0, 0, 0, TimeSpan.Zero));
    }

    public void Dispose() => _db.Dispose();

    private PriceBackfillService CreateSut(
        IQuoteProviderRouter router, IFxRateProvider fxProvider, int maxCallsPerRun = 20) =>
        new(_db, router, fxProvider, _timeProvider, Options.Create(new PriceBackfillOptions { MaxProviderCallsPerRun = maxCallsPerRun }), NullLogger<PriceBackfillService>.Instance);

    private static IQuoteProviderRouter RouterAlwaysReturning(IQuoteProvider provider)
    {
        var router = Substitute.For<IQuoteProviderRouter>();
        router.GetProvider(Arg.Any<Asset>()).Returns(provider);
        return router;
    }

    [Fact]
    public async Task RunAsync_InsertsPriceHistoryAndFxRate_ForAssetsWithTransactions()
    {
        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var asset = callInfo.Arg<Asset>()!;
                var from = callInfo.ArgAt<DateOnly>(1);
                var currency = asset.Currency;
                IReadOnlyList<PriceHistoryPoint> points =
                [
                    new PriceHistoryPoint(new DateOnly(2026, 7, 20), asset.Id == _aapl.Id ? 200m : 4m, currency),
                    new PriceHistoryPoint(new DateOnly(2026, 7, 21), asset.Id == _aapl.Id ? 201m : 4.01m, currency),
                ];
                return Task.FromResult(HistoryFetchResult.Ok(points, from));
            });

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync("USD", "SGD", Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<FxRatePoint>)
            [
                new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m),
                new FxRatePoint(new DateOnly(2026, 7, 21), 1.291m),
            ]);

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider);

        var summary = await sut.RunAsync(CancellationToken.None);

        summary.PriceHistoryPointsInserted.Should().Be(4); // 2 dates x 2 assets
        summary.FxRatePointsInserted.Should().Be(2);
        summary.AssetsProcessed.Should().Contain(["AAPL", "Z74"]);
        summary.AssetsWithTruncatedHistory.Should().BeEmpty();

        (await _db.PriceHistories.CountAsync()).Should().Be(4);
        (await _db.FxRates.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task RunAsync_IsIdempotent_RerunningDoesNotDuplicateOrThrow()
    {
        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 200m, "USD")],
                callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<FxRatePoint>)
            [
                new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m),
            ]);

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider);

        await sut.RunAsync(CancellationToken.None);
        var secondRun = await sut.RunAsync(CancellationToken.None);

        // Second run finds every date already present, so it inserts nothing more.
        secondRun.PriceHistoryPointsInserted.Should().Be(0);
        secondRun.FxRatePointsInserted.Should().Be(0);

        (await _db.PriceHistories.CountAsync()).Should().Be(2); // one per asset, not duplicated
        (await _db.FxRates.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_BoundsCallsPerRun_AndSkipsAssetsOverBudget()
    {
        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 200m, "USD")],
                callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<FxRatePoint>)[new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]);

        // Budget of zero: nothing should be fetched, both assets skipped, no exception.
        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider, maxCallsPerRun: 0);

        var summary = await sut.RunAsync(CancellationToken.None);

        summary.ProviderCallsUsed.Should().Be(0);
        summary.AssetsProcessed.Should().BeEmpty();
        summary.AssetsSkippedForBudget.Should().Contain("AAPL", "Z74");
    }

    [Fact]
    public async Task RunAsync_ProviderReportsTruncation_SurfacesItOnTheSummary_NotAsSilentSuccess()
    {
        // Simulates CoinGecko's keyless 365-day window clamping AAPL's requested start date —
        // the summary must let a caller tell this apart from "everything came back as asked".
        var requestedFrom = new DateOnly(2024, 1, 1);
        var effectiveFrom = new DateOnly(2025, 7, 27);

        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(_aapl, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new HistoryFetchResult(
                [new PriceHistoryPoint(effectiveFrom, 200m, "USD")],
                requestedFrom,
                effectiveFrom,
                Truncated: true,
                Success: true,
                Error: null));
        stockProvider.GetHistoryAsync(_z74, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 4m, "SGD")],
                callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<FxRatePoint>)[new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]);

        var router = Substitute.For<IQuoteProviderRouter>();
        router.GetProvider(_aapl).Returns(stockProvider);
        router.GetProvider(_z74).Returns(stockProvider);

        var sut = CreateSut(router, fxProvider);

        var summary = await sut.RunAsync(CancellationToken.None);

        // Truncation is not a failure — the asset is still processed and its (partial) history
        // is still inserted — but it must be visibly flagged, not indistinguishable from a clean run.
        summary.AssetsProcessed.Should().Contain("AAPL");
        summary.AssetsWithTruncatedHistory.Should().ContainSingle(s => s.Contains("AAPL"));
        summary.AssetsWithTruncatedHistory.Should().ContainSingle(s =>
            s.Contains(requestedFrom.ToString("yyyy-MM-dd")) && s.Contains(effectiveFrom.ToString("yyyy-MM-dd")));
        summary.AssetsWithTruncatedHistory.Should().NotContain(s => s.Contains("Z74"));
    }

    [Fact]
    public async Task RunAsync_ExcludesCryptoAssets_EvenWhenTheyHaveTransactions()
    {
        // Crypto is gain/loss only, by decision — it keeps no PriceHistory at all, so a crypto
        // asset with transactions must be skipped entirely, not merely deprioritised.
        var eth = new Asset
        {
            Id = 4,
            Symbol = "ETH",
            Name = "Ethereum",
            AssetClass = AssetClass.Crypto,
            Currency = "USD",
            QuoteProviderKind = QuoteProviderKind.CoinGecko,
            ProviderCoinId = "ethereum",
        };
        _db.Assets.Add(eth);
        _db.Transactions.Add(new Transaction
        {
            AssetId = eth.Id,
            Type = TransactionType.Buy,
            TradeDate = new DateOnly(2026, 7, 20),
            Quantity = 1m,
            PricePerUnit = 2000m,
            Fees = 0m,
            Currency = "USD",
        });
        await _db.SaveChangesAsync();

        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 200m, "USD")],
                callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<FxRatePoint>)[new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]);

        var router = Substitute.For<IQuoteProviderRouter>();
        router.GetProvider(Arg.Any<Asset>()).Returns(stockProvider);

        var sut = CreateSut(router, fxProvider);

        var summary = await sut.RunAsync(CancellationToken.None);

        summary.AssetsProcessed.Should().NotContain("ETH");
        summary.AssetsSkippedForBudget.Should().NotContain("ETH");
        router.DidNotReceive().GetProvider(eth);
        (await _db.PriceHistories.Where(p => p.AssetId == eth.Id).CountAsync()).Should().Be(0);
    }
}
