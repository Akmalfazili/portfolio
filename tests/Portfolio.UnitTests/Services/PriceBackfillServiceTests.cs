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
        IQuoteProviderRouter router,
        IFxRateProvider fxProvider,
        int maxCallsPerRun = 20,
        IMarketCalendar? calendar = null) =>
        new(
            _db,
            router,
            fxProvider,
            calendar ?? AlwaysClosedCalendar(),
            _timeProvider,
            Options.Create(new PriceBackfillOptions { MaxProviderCallsPerRun = maxCallsPerRun }),
            NullLogger<PriceBackfillService>.Instance);

    /// <summary>Most tests here exercise <c>RunAsync</c> directly, which never consults the
    /// calendar - only <c>RunIfDueAsync</c> does. Default to "always closed" so a test that forgot
    /// to pass one would still see a due <c>RunIfDueAsync</c> rather than a silently-gated one.</summary>
    private static IMarketCalendar AlwaysClosedCalendar()
    {
        var calendar = Substitute.For<IMarketCalendar>();
        calendar.IsOpen(Arg.Any<Market>(), Arg.Any<DateTimeOffset>()).Returns(false);
        return calendar;
    }

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
            .Returns(FxHistoryFetchResult.Ok(
            [
                new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m),
                new FxRatePoint(new DateOnly(2026, 7, 21), 1.291m),
            ]));

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider);

        var summary = await sut.RunAsync(RefreshTrigger.BackfillManual, CancellationToken.None);

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
            .Returns(FxHistoryFetchResult.Ok(
            [
                new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m),
            ]));

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider);

        await sut.RunAsync(RefreshTrigger.BackfillManual, CancellationToken.None);
        var secondRun = await sut.RunAsync(RefreshTrigger.BackfillManual, CancellationToken.None);

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
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]));

        // Budget of zero: nothing should be fetched, both assets skipped, no exception.
        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider, maxCallsPerRun: 0);

        var summary = await sut.RunAsync(RefreshTrigger.BackfillManual, CancellationToken.None);

        summary.ProviderCallsUsed.Should().Be(0);
        summary.AssetsProcessed.Should().BeEmpty();
        // FX now claims the budget first (see PriceBackfillService.RunAsync), so with a budget of
        // zero it is skipped alongside both assets, not just them.
        summary.AssetsSkippedForBudget.Should().Contain(["AAPL", "Z74", "FX:USD/SGD"]);
        // A budget skip is not a failure - the coordinator-reported defect this test set guards
        // against is exactly these two lists being conflated.
        summary.AssetsFailed.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_ProviderCallFails_IsReportedAsAssetsFailed_NotAssetsSkippedForBudget()
    {
        // The coordinator-reported defect: a provider rejection (e.g. Twelve Data's real HTTP 400
        // for a same-day range) must not be indistinguishable from "budget exhausted" - raising
        // MaxProviderCallsPerRun would do nothing for an asset that actually failed.
        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(_aapl, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new HistoryFetchResult([], new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 20), Truncated: false, Success: false, Error: "Twelve Data returned HTTP 400."));
        stockProvider.GetHistoryAsync(_z74, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 4m, "SGD")],
                callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]));

        var router = Substitute.For<IQuoteProviderRouter>();
        router.GetProvider(_aapl).Returns(stockProvider);
        router.GetProvider(_z74).Returns(stockProvider);

        var sut = CreateSut(router, fxProvider);

        var summary = await sut.RunAsync(RefreshTrigger.BackfillManual, CancellationToken.None);

        summary.AssetsFailed.Should().ContainSingle(f => f.Symbol == "AAPL" && f.Error == "Twelve Data returned HTTP 400.");
        summary.AssetsSkippedForBudget.Should().BeEmpty();
        summary.ProviderCallsUsed.Should().Be(3); // the SGD FX call (now first), AAPL's failed attempt, and Z74's own call
        summary.AssetsProcessed.Should().Contain("Z74");
    }

    [Fact]
    public async Task RunAsync_ProviderThrows_IsReportedAsAssetsFailed_WithTheExceptionMessage()
    {
        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns<Task<HistoryFetchResult>>(_ => throw new InvalidOperationException("transport blew up"));

        // Z74 is SGD, so it still queues an FX fetch even though its own history call throws -
        // give that a benign result so this test isolates the stock-side failure.
        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]));

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider);

        var summary = await sut.RunAsync(RefreshTrigger.BackfillManual, CancellationToken.None);

        summary.AssetsFailed.Should().Contain(f => f.Symbol == "AAPL" && f.Error == "transport blew up");
        summary.AssetsFailed.Should().Contain(f => f.Symbol == "Z74" && f.Error == "transport blew up");
        summary.AssetsSkippedForBudget.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_AssetsEarliestTradeDateIsToday_SkipsWithoutSpendingACall_AndIsNotReportedAsAFailure()
    {
        // The other half of the coordinator's fix request: short-circuit a guaranteed-to-fail
        // same-day range entirely, rather than spend a call to discover it fails.
        var probeAsset = new Asset
        {
            Id = 9,
            Symbol = "NEWCO",
            Name = "New Co",
            AssetClass = AssetClass.Stock,
            Currency = "USD",
            QuoteProviderKind = QuoteProviderKind.TwelveData,
            ProviderSymbol = "NEWCO",
        };
        _db.Assets.Add(probeAsset);
        _db.Transactions.Add(new Transaction
        {
            AssetId = probeAsset.Id,
            Type = TransactionType.Buy,
            TradeDate = new DateOnly(2026, 7, 26), // matches _timeProvider's fixed "today"
            Quantity = 1,
            PricePerUnit = 10m,
            Fees = 0m,
            Currency = "USD",
        });
        await _db.SaveChangesAsync();

        // AAPL/Z74 (from the fixture, earliest trade date 2026-07-20, not "today") must still
        // succeed normally - only NEWCO's same-day range should be short-circuited.
        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 200m, "USD")],
                callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]));

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider);

        var summary = await sut.RunAsync(RefreshTrigger.BackfillManual, CancellationToken.None);

        summary.AssetsSkippedTodayNotClosed.Should().Contain("NEWCO");
        summary.AssetsFailed.Should().BeEmpty();
        summary.AssetsSkippedForBudget.Should().BeEmpty();
        summary.AssetsProcessed.Should().Contain(["AAPL", "Z74"]).And.NotContain("NEWCO");
        await stockProvider.DidNotReceive().GetHistoryAsync(
            probeAsset, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ProviderReportsTruncation_SurfacesItOnTheSummary_NotAsSilentSuccess()
    {
        // Simulates CoinGecko's keyless 365-day window clamping AAPL's requested start date -
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
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]));

        var router = Substitute.For<IQuoteProviderRouter>();
        router.GetProvider(_aapl).Returns(stockProvider);
        router.GetProvider(_z74).Returns(stockProvider);

        var sut = CreateSut(router, fxProvider);

        var summary = await sut.RunAsync(RefreshTrigger.BackfillManual, CancellationToken.None);

        // Truncation is not a failure - the asset is still processed and its (partial) history
        // is still inserted - but it must be visibly flagged, not indistinguishable from a clean run.
        summary.AssetsProcessed.Should().Contain("AAPL");
        summary.AssetsWithTruncatedHistory.Should().ContainSingle(s => s.Contains("AAPL"));
        summary.AssetsWithTruncatedHistory.Should().ContainSingle(s =>
            s.Contains(requestedFrom.ToString("yyyy-MM-dd")) && s.Contains(effectiveFrom.ToString("yyyy-MM-dd")));
        summary.AssetsWithTruncatedHistory.Should().NotContain(s => s.Contains("Z74"));
    }

    [Fact]
    public async Task RunAsync_ExcludesCryptoAssets_EvenWhenTheyHaveTransactions()
    {
        // Crypto is gain/loss only, by decision - it keeps no PriceHistory at all, so a crypto
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
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]));

        var router = Substitute.For<IQuoteProviderRouter>();
        router.GetProvider(Arg.Any<Asset>()).Returns(stockProvider);

        var sut = CreateSut(router, fxProvider);

        var summary = await sut.RunAsync(RefreshTrigger.BackfillManual, CancellationToken.None);

        summary.AssetsProcessed.Should().NotContain("ETH");
        summary.AssetsSkippedForBudget.Should().NotContain("ETH");
        router.DidNotReceive().GetProvider(eth);
        (await _db.PriceHistories.Where(p => p.AssetId == eth.Id).CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_FxClaimsTheBudgetBeforeAssets_NotAfter()
    {
        // The bug this guards against: with the asset loop running first (as it used to), a
        // budget of exactly one call would be consumed by AAPL, and the sole FX call at the end
        // would starve - which is exactly what 500s every USD-reporting endpoint for a portfolio
        // with more assets than spare budget. FX must win the single call here, not the assets.
        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 200m, "USD")],
                callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]));

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider, maxCallsPerRun: 1);

        var summary = await sut.RunAsync(RefreshTrigger.BackfillManual, CancellationToken.None);

        summary.ProviderCallsUsed.Should().Be(1);
        summary.FxRatePointsInserted.Should().Be(1);
        summary.AssetsProcessed.Should().BeEmpty();
        summary.AssetsSkippedForBudget.Should().Contain(["AAPL", "Z74"]);
        await fxProvider.Received(1).GetHistoryAsync(
            "USD", "SGD", Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_FxProviderFails_IsReportedAsAssetsFailed_NotSilentZeroInserted()
    {
        // The other half of the false-success bug: TwelveDataFxProvider used to swallow a 429
        // into an empty list, which is indistinguishable from "no rates in range" - the run would
        // report 200 OK with fxRatePointsInserted: 0 and nobody would notice the page was still
        // going to 500 on the missing rate. FX failures must show up in AssetsFailed like any
        // other provider failure.
        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 200m, "USD")],
                callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Failed("Twelve Data returned HTTP 429."));

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider);

        var summary = await sut.RunAsync(RefreshTrigger.BackfillManual, CancellationToken.None);

        summary.AssetsFailed.Should().ContainSingle(f => f.Symbol == "FX:USD/SGD" && f.Error == "Twelve Data returned HTTP 429.");
        summary.FxRatePointsInserted.Should().Be(0);
        (await _db.FxRates.CountAsync()).Should().Be(0);
        // A failed FX fetch must not block assets that don't need this pair (or, as here, even
        // the SGD asset itself - its own price history is independent of whether the FX rate to
        // convert it to USD landed).
        summary.AssetsProcessed.Should().Contain(["AAPL", "Z74"]);
    }

    // --- RunIfDueAsync: the market-calendar gate and once-per-day throttle behind the scheduled
    // path (D12). RunAsync itself is exercised above; these tests cover only the extra gating.

    [Fact]
    public async Task RunIfDueAsync_NyseOpen_SkipsWithoutCallingAnyProvider()
    {
        var stockProvider = Substitute.For<IQuoteProvider>();
        var router = RouterAlwaysReturning(stockProvider);
        var fxProvider = Substitute.For<IFxRateProvider>();

        var openCalendar = Substitute.For<IMarketCalendar>();
        openCalendar.IsOpen(Market.Nyse, Arg.Any<DateTimeOffset>()).Returns(true);

        var sut = CreateSut(router, fxProvider, calendar: openCalendar);

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.Outcome.Should().Be(PriceBackfillOutcome.MarketOpen);
        result.Summary.Should().BeNull();
        await stockProvider.DidNotReceive().GetHistoryAsync(
            Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        (await _db.RefreshRuns.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RunIfDueAsync_NyseClosed_NoPriorRunToday_RunsAndRecordsAScheduledRefreshRun()
    {
        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 200m, "USD")],
                callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]));

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider); // AlwaysClosedCalendar

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.Outcome.Should().Be(PriceBackfillOutcome.Completed);
        result.Summary.Should().NotBeNull();
        result.Summary!.AssetsProcessed.Should().Contain(["AAPL", "Z74"]);

        var run = (await _db.RefreshRuns.ToListAsync()).Should().ContainSingle().Subject;
        run.Trigger.Should().Be(RefreshTrigger.BackfillScheduled);
        run.AssetClass.Should().Be(AssetClass.Stock);
    }

    [Fact]
    public async Task RunIfDueAsync_AlreadyRanScheduledToday_SkipsWithoutCallingAnyProvider()
    {
        var stockProvider = Substitute.For<IQuoteProvider>();
        var router = RouterAlwaysReturning(stockProvider);
        var fxProvider = Substitute.For<IFxRateProvider>();

        // Same UTC day as _timeProvider's fixed "now" (2026-07-26).
        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.BackfillScheduled,
            AssetClass = AssetClass.Stock,
            StartedAt = new DateTimeOffset(2026, 7, 26, 21, 0, 0, TimeSpan.Zero),
            CompletedAt = new DateTimeOffset(2026, 7, 26, 21, 0, 1, TimeSpan.Zero),
            Success = true,
            SymbolsRefreshed = 2,
        });
        await _db.SaveChangesAsync();

        var sut = CreateSut(router, fxProvider); // AlwaysClosedCalendar

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.Outcome.Should().Be(PriceBackfillOutcome.AlreadyRanToday);
        result.Summary.Should().BeNull();
        await stockProvider.DidNotReceive().GetHistoryAsync(
            Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        (await _db.RefreshRuns.CountAsync()).Should().Be(1); // the seeded row only, nothing new added
    }

    [Fact]
    public async Task RunIfDueAsync_ManualRunEarlierToday_DoesNotCountAsTheScheduledRun()
    {
        // Only RefreshTrigger.BackfillScheduled counts toward the once-per-day throttle - a
        // manual click earlier today must not suppress the scheduled run, or a user who tests the
        // manual endpoint would accidentally starve the automatic one for the rest of the day.
        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 200m, "USD")],
                callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]));

        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.BackfillManual,
            AssetClass = AssetClass.Stock,
            StartedAt = new DateTimeOffset(2026, 7, 26, 5, 0, 0, TimeSpan.Zero),
            CompletedAt = new DateTimeOffset(2026, 7, 26, 5, 0, 1, TimeSpan.Zero),
            Success = true,
            SymbolsRefreshed = 2,
        });
        await _db.SaveChangesAsync();

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider); // AlwaysClosedCalendar

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.Outcome.Should().Be(PriceBackfillOutcome.Completed);
    }
}
