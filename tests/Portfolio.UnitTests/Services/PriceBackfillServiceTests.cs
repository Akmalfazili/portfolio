using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services;
using Portfolio.Application.Services.Calendar;
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
///
/// The <c>RunIfDueAsync_*</c> section covers D47 — each market's due-ness is evaluated entirely
/// on its own session and its own last close, never on the other market's. The regression this
/// closed (<c>D47_Regression_...</c> below) was confirmed to fail against the pre-fix code before
/// the fix was written — see tracker.md.
/// </summary>
public sealed class PriceBackfillServiceTests : IDisposable
{
    private static readonly IReadOnlyCollection<Market> BothMarkets = ProviderMarkets.All;

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
        int maxCallsPerRun = 800,
        IMarketCalendar? calendar = null,
        ITwelveDataCreditThrottle? creditThrottle = null,
        TimeProvider? timeProvider = null,
        TimeSpan? failedRunRetryDelay = null,
        int? maxFailedRunRetriesPerClose = null) =>
        new(
            _db,
            router,
            fxProvider,
            calendar ?? AlwaysClosedCalendar(),
            timeProvider ?? _timeProvider,
            Options.Create(new PriceBackfillOptions
            {
                MaxProviderCallsPerRun = maxCallsPerRun,
                FailedRunRetryDelay = failedRunRetryDelay ?? TimeSpan.FromMinutes(15),
                MaxFailedRunRetriesPerClose = maxFailedRunRetriesPerClose ?? 3,
            }),
            creditThrottle ?? ThrottleWithRemainingBudget(800),
            NullLogger<PriceBackfillService>.Instance);

    /// <summary>Default credit-throttle fake reporting a full, untouched daily budget — the
    /// hardcoded 20 this project used to bound a run has moved to <paramref name="maxCallsPerRun"/>
    /// in <see cref="CreateSut"/> above for tests that are about that specific ceiling; this fake
    /// is for tests that are instead about the derived-from-remaining-credits path (D37).</summary>
    private static ITwelveDataCreditThrottle ThrottleWithRemainingBudget(int remaining)
    {
        var throttle = Substitute.For<ITwelveDataCreditThrottle>();
        throttle.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(new TwelveDataCreditStatus(800 - remaining, 800, remaining));
        return throttle;
    }

    /// <summary>Most tests here exercise <c>RunAsync</c> directly, which never consults the
    /// calendar - only <c>RunIfDueAsync</c> does. Default to "always closed" so a test that forgot
    /// to pass one would still see both markets due from <c>RunIfDueAsync</c> rather than a
    /// silently-gated one.</summary>
    private static IMarketCalendar AlwaysClosedCalendar()
    {
        var calendar = Substitute.For<IMarketCalendar>();
        calendar.IsOpen(Arg.Any<Market>(), Arg.Any<DateTimeOffset>()).Returns(false);
        return calendar;
    }

    /// <summary>
    /// D51: a calendar substitute with a controllable, fixed "last session close" (and its
    /// exchange-local date) for one market, and IsOpen always false — used by the retry tests below
    /// so the exact close instant/date driving the narrowing and due-ness decisions is explicit and
    /// stable, rather than depending on the real calendar's weekend/holiday walking.
    /// </summary>
    private static IMarketCalendar CalendarWithFixedClose(Market market, DateTimeOffset closeInstant, DateOnly closeLocalDate)
    {
        var calendar = Substitute.For<IMarketCalendar>();
        calendar.IsOpen(Arg.Any<Market>(), Arg.Any<DateTimeOffset>()).Returns(false);
        calendar.LastSessionCloseAt(market, Arg.Any<DateTimeOffset>()).Returns(closeInstant);
        calendar.LocalDateOn(market, closeInstant).Returns(closeLocalDate);
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

        var summary = await sut.RunAsync(RefreshTrigger.BackfillManual, BothMarkets, CancellationToken.None);

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

        await sut.RunAsync(RefreshTrigger.BackfillManual, BothMarkets, CancellationToken.None);
        var secondRun = await sut.RunAsync(RefreshTrigger.BackfillManual, BothMarkets, CancellationToken.None);

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

        var summary = await sut.RunAsync(RefreshTrigger.BackfillManual, BothMarkets, CancellationToken.None);

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

        var summary = await sut.RunAsync(RefreshTrigger.BackfillManual, BothMarkets, CancellationToken.None);

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

        var summary = await sut.RunAsync(RefreshTrigger.BackfillManual, BothMarkets, CancellationToken.None);

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

        var summary = await sut.RunAsync(RefreshTrigger.BackfillManual, BothMarkets, CancellationToken.None);

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

        var summary = await sut.RunAsync(RefreshTrigger.BackfillManual, BothMarkets, CancellationToken.None);

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

        var summary = await sut.RunAsync(RefreshTrigger.BackfillManual, BothMarkets, CancellationToken.None);

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

        var summary = await sut.RunAsync(RefreshTrigger.BackfillManual, BothMarkets, CancellationToken.None);

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

        var summary = await sut.RunAsync(RefreshTrigger.BackfillManual, BothMarkets, CancellationToken.None);

        summary.AssetsFailed.Should().ContainSingle(f => f.Symbol == "FX:USD/SGD" && f.Error == "Twelve Data returned HTTP 429.");
        summary.FxRatePointsInserted.Should().Be(0);
        (await _db.FxRates.CountAsync()).Should().Be(0);
        // A failed FX fetch must not block assets that don't need this pair (or, as here, even
        // the SGD asset itself - its own price history is independent of whether the FX rate to
        // convert it to USD landed).
        summary.AssetsProcessed.Should().Contain(["AAPL", "Z74"]);
    }

    [Fact]
    public async Task RunAsync_OrdersByLeastRecentlyBackfilled_SoNoAssetIsPermanentlyStarvedAcrossRuns()
    {
        // D37, reproduced and fixed. With no ORDER BY, SQL Server returns assets in Id (clustered
        // index) order every run, so a budget smaller than the full asset count always serves the
        // same head and starves the same tail — forever, not just once. Three USD assets (no FX
        // pair in play, so the FX loop can never interfere with this test) and a budget of exactly
        // two prove it: against the pre-fix ordering (implicit Id order, unchanged run to run),
        // CCC (highest Id) would be skipped on EVERY run and the second run's assertions below
        // would fail — CCC would be skipped again and BBB would be processed again.
        var toRemove = _db.Transactions.Where(t => t.AssetId == _aapl.Id || t.AssetId == _z74.Id).ToList();
        _db.Transactions.RemoveRange(toRemove);
        _db.Assets.RemoveRange(_aapl, _z74);

        var assetA = new Asset { Id = 101, Symbol = "AAA", Name = "A Co", AssetClass = AssetClass.Stock, Currency = "USD", QuoteProviderKind = QuoteProviderKind.TwelveData, ProviderSymbol = "AAA" };
        var assetB = new Asset { Id = 102, Symbol = "BBB", Name = "B Co", AssetClass = AssetClass.Stock, Currency = "USD", QuoteProviderKind = QuoteProviderKind.TwelveData, ProviderSymbol = "BBB" };
        var assetC = new Asset { Id = 103, Symbol = "CCC", Name = "C Co", AssetClass = AssetClass.Stock, Currency = "USD", QuoteProviderKind = QuoteProviderKind.TwelveData, ProviderSymbol = "CCC" };
        _db.Assets.AddRange(assetA, assetB, assetC);

        foreach (var asset in new[] { assetA, assetB, assetC })
        {
            _db.Transactions.Add(new Transaction
            {
                AssetId = asset.Id,
                Type = TransactionType.Buy,
                TradeDate = new DateOnly(2026, 7, 20),
                Quantity = 1,
                PricePerUnit = 10m,
                Fees = 0m,
                Currency = "USD",
            });
        }

        await _db.SaveChangesAsync();

        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 10m, "USD")],
                callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>(); // never called — no non-USD currency in play

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider, maxCallsPerRun: 2);

        var firstRun = await sut.RunAsync(RefreshTrigger.BackfillManual, BothMarkets, CancellationToken.None);

        firstRun.AssetsProcessed.Should().Contain(["AAA", "BBB"]);
        firstRun.AssetsSkippedForBudget.Should().Contain(["CCC"]);

        var secondRun = await sut.RunAsync(RefreshTrigger.BackfillManual, BothMarkets, CancellationToken.None);

        // The asset skipped last time must be served first this time — not skipped again.
        secondRun.AssetsProcessed.Should().Contain("CCC", "CCC was left behind last run and must not be starved forever");
        secondRun.AssetsSkippedForBudget.Should().Contain("BBB", "the ordering rotates — BBB (most recently backfilled) now waits, not CCC again");
        await fxProvider.DidNotReceive().GetHistoryAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_BudgetIsDerivedFromRemainingDailyCredits_NotJustTheConfiguredCeiling()
    {
        // D37's third compounding cause: the budget used to be a flat hardcoded 20 (now defaults
        // to the full 800 daily ceiling — see PriceBackfillOptions) with no regard for how much of
        // TODAY's credit budget Twelve Data has already spent elsewhere (e.g. a quote-refresh
        // sweep earlier the same day). MaxProviderCallsPerRun is left at its generous default here
        // — the only thing constraining this run is the throttle reporting just 1 credit left.
        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 200m, "USD")],
                callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]));

        var sut = CreateSut(
            RouterAlwaysReturning(stockProvider),
            fxProvider,
            creditThrottle: ThrottleWithRemainingBudget(1)); // only 1 credit left today, regardless of the 800 default ceiling

        var summary = await sut.RunAsync(RefreshTrigger.BackfillManual, BothMarkets, CancellationToken.None);

        summary.ProviderCallsUsed.Should().Be(1);
        summary.FxRatePointsInserted.Should().Be(1); // FX still claims the single available call first
        summary.AssetsProcessed.Should().BeEmpty();
        summary.AssetsSkippedForBudget.Should().Contain(["AAPL", "Z74"]);
    }

    [Fact]
    public async Task RunAsync_ScopedToOneMarket_TouchesNothingOutsideIt_AndSpendsNoCallsOnTheOtherMarket()
    {
        // D47: the whole point of scoping RunAsync per market — an SGX-only run (Z74/Yahoo) must
        // never call the router for AAPL (NYSE/Twelve Data), never insert anything for AAPL, and
        // must not even load AAPL into the ordering/budget bookkeeping. FX for SGD is still fetched
        // (Z74 is SGD-denominated — converting it to USD is unrelated to which equity provider was
        // polled), so only the equity-provider call count is asserted at zero for NYSE.
        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 4m, "SGD")],
                callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]));

        var router = Substitute.For<IQuoteProviderRouter>();
        router.GetProvider(Arg.Any<Asset>()).Returns(stockProvider);

        var sut = CreateSut(router, fxProvider);

        var summary = await sut.RunAsync(RefreshTrigger.BackfillManual, [Market.Sgx], CancellationToken.None);

        summary.AssetsProcessed.Should().Contain("Z74").And.NotContain("AAPL");
        summary.FxRatePointsInserted.Should().Be(1); // USD/SGD still fetched — Z74 needs it regardless of scope
        router.DidNotReceive().GetProvider(_aapl); // zero Twelve Data equity calls for the out-of-scope NYSE asset
        await stockProvider.DidNotReceive().GetHistoryAsync(
            _aapl, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        (await _db.PriceHistories.Where(p => p.AssetId == _aapl.Id).CountAsync()).Should().Be(0);

        // One RefreshRun row, scoped to SGX — not one row for "the run" with no market, and
        // certainly not a row for NYSE, which was never touched.
        var runs = await _db.RefreshRuns.ToListAsync();
        runs.Should().ContainSingle();
        runs[0].Market.Should().Be(Market.Sgx);
        runs[0].SymbolsRefreshed.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_CoveringBothMarkets_WritesOneRefreshRunRowPerMarket()
    {
        // D47: "one row for the whole run" is exactly what leaves the per-market due-ness query
        // with nothing of its own to read for one of the two markets.
        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 200m, "USD")],
                callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]));

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider);

        await sut.RunAsync(RefreshTrigger.BackfillManual, BothMarkets, CancellationToken.None);

        var runs = await _db.RefreshRuns.ToListAsync();
        runs.Should().HaveCount(2);
        runs.Should().ContainSingle(r => r.Market == Market.Nyse && r.SymbolsRefreshed == 1); // AAPL only
        runs.Should().ContainSingle(r => r.Market == Market.Sgx && r.SymbolsRefreshed == 1); // Z74 only
    }

    // --- RunIfDueAsync: per-market due-ness (D47). RunAsync itself is exercised above; these
    // tests cover only the extra gating.

    [Fact]
    public async Task D47_Regression_SgxDueAt1705Sgt_EvenThoughAScheduledRunCompletedAt0405SameSgtDay()
    {
        // THE regression this closed. Confirmed (see tracker.md and this session's own probe) to
        // FAIL against the pre-fix code with PriceBackfillOutcome.AlreadyRanToday: the old gate was
        // ONE NYSE-keyed "is the market open" check plus ONE once-per-Singapore-day latch shared by
        // every asset. A run completing at 04:05 SGT (right after NYSE's ~04:00 SGT close) used to
        // suppress a second run for the rest of the SGT day — even though SGX's OWN session for
        // that day had not even opened yet at 04:05, and its 17:00 SGT close (published at 17:00,
        // never fetched) then had to wait until the following day's 04:05 run, ~11 hours late.
        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 4m, "SGD")],
                callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]));

        // 2026-08-04 is a Tuesday, 2026-08-05 a Wednesday — an ordinary consecutive weekday pair,
        // clear of every modelled NYSE/SGX holiday (Labor Day is September; SGX's National Day is
        // 9 August). A scheduled run for BOTH markets completed 04:05 SGT on 2026-08-05
        // (2026-08-04T20:05:00Z) — NYSE's own close landing at that instant, and (pre-fix) also
        // mislabelled as covering SGX. Post-fix, this only latches NYSE: the SGX row only ever
        // covers what SGX's OWN last close was at that same instant — 2026-08-04's 17:00 SGT
        // close, one calendar day earlier — which is exactly what LastSessionCloseAt-based
        // due-ness reads instead of trusting a shared timestamp.
        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.BackfillScheduled,
            AssetClass = AssetClass.Stock,
            Market = Market.Nyse,
            StartedAt = new DateTimeOffset(2026, 8, 4, 20, 5, 0, TimeSpan.Zero),
            CompletedAt = new DateTimeOffset(2026, 8, 4, 20, 5, 1, TimeSpan.Zero),
            Success = true,
            SymbolsRefreshed = 1,
        });
        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.BackfillScheduled,
            AssetClass = AssetClass.Stock,
            Market = Market.Sgx,
            StartedAt = new DateTimeOffset(2026, 8, 4, 20, 5, 0, TimeSpan.Zero),
            CompletedAt = new DateTimeOffset(2026, 8, 4, 20, 5, 1, TimeSpan.Zero),
            Success = true,
            SymbolsRefreshed = 1,
        });
        await _db.SaveChangesAsync();

        // "Now" = 17:05 SGT on 2026-08-05 (2026-08-05T09:05:00Z) — SGX has just closed and
        // published TODAY's (2026-08-05) close, which the seeded SGX row above (completed the
        // previous SGT day, before SGX's 2026-08-05 session even opened) has never covered.
        var probeTime = new FixedTimeProvider(new DateTimeOffset(2026, 8, 5, 9, 5, 0, TimeSpan.Zero));

        // Real calendar so SGX genuinely reads "closed" at 17:05 SGT (after its own 17:00 close)
        // and NYSE genuinely reads "closed" too (09:05 UTC = 05:05 ET, before NYSE's 09:30 open) —
        // both markets closed, exactly like the live scenario this was diagnosed from.
        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider, calendar: new MarketCalendar(), timeProvider: probeTime);

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.MarketsRun.Should().Contain(Market.Sgx, "SGX's own 2026-08-05 17:00 close was never covered by any SGX-scoped run");
        result.MarketsSkipped.Should().ContainSingle(s => s.Market == Market.Nyse && s.Reason == PriceBackfillSkipReason.AlreadyCoveredSinceLastClose);
        result.Summary.Should().NotBeNull();
        result.Summary!.AssetsProcessed.Should().Contain("Z74").And.NotContain("AAPL");
    }

    [Fact]
    public async Task RunIfDueAsync_At2239Sgt_SgxDue_NyseNotDue()
    {
        // 22:39 SGT: SGX closed at 17:00 (due, assuming no covering run yet); NYSE is mid-session
        // (21:30 SGT open, 04:00 SGT close) — not due, regardless of any run history.
        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 4m, "SGD")],
                callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]));

        // 2026-07-29 is an ordinary Wednesday (matches MarketCalendarTests' SGX fixture day).
        // 22:39 SGT = 14:39 UTC.
        var probeTime = new FixedTimeProvider(new DateTimeOffset(2026, 7, 29, 14, 39, 0, TimeSpan.Zero));

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider, calendar: new MarketCalendar(), timeProvider: probeTime);

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.MarketsRun.Should().Contain(Market.Sgx);
        result.MarketsSkipped.Should().ContainSingle(s => s.Market == Market.Nyse && s.Reason == PriceBackfillSkipReason.SessionOpen);
    }

    [Fact]
    public async Task RunIfDueAsync_SecondPoll15MinAfterACompletedSgxRun_IsNotDue_NoReSpend()
    {
        // 17:15 SGT: SGX due (closed at 17:00). A second poll at 17:30, after the 17:15 run
        // completed, must find SGX no longer due — no re-spend.
        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 4m, "SGD")],
                callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]));

        // 2026-07-29 17:15 SGT = 09:15 UTC.
        var pollTime = new MutableTimeProvider(new DateTimeOffset(2026, 7, 29, 9, 15, 0, TimeSpan.Zero));
        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider, calendar: new MarketCalendar(), timeProvider: pollTime);

        var firstResult = await sut.RunIfDueAsync(CancellationToken.None);
        firstResult.MarketsRun.Should().Contain(Market.Sgx);

        // 17:30 SGT = 09:30 UTC, 15 minutes later.
        pollTime.Now = new DateTimeOffset(2026, 7, 29, 9, 30, 0, TimeSpan.Zero);

        var secondResult = await sut.RunIfDueAsync(CancellationToken.None);

        secondResult.MarketsRun.Should().NotContain(Market.Sgx);
        secondResult.MarketsSkipped.Should().ContainSingle(s => s.Market == Market.Sgx && s.Reason == PriceBackfillSkipReason.AlreadyCoveredSinceLastClose);
        await stockProvider.Received(1).GetHistoryAsync(
            _z74, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()); // only the first poll's call
    }

    [Fact]
    public async Task RunIfDueAsync_BothMarketsOpen_SkipsBoth_WithSessionOpenReason()
    {
        var stockProvider = Substitute.For<IQuoteProvider>();
        var router = RouterAlwaysReturning(stockProvider);
        var fxProvider = Substitute.For<IFxRateProvider>();

        var openCalendar = Substitute.For<IMarketCalendar>();
        openCalendar.IsOpen(Arg.Any<Market>(), Arg.Any<DateTimeOffset>()).Returns(true);

        var sut = CreateSut(router, fxProvider, calendar: openCalendar);

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.MarketsRun.Should().BeEmpty();
        result.Summary.Should().BeNull();
        result.MarketsSkipped.Should().Contain([
            new PriceBackfillMarketSkip(Market.Nyse, PriceBackfillSkipReason.SessionOpen),
            new PriceBackfillMarketSkip(Market.Sgx, PriceBackfillSkipReason.SessionOpen),
        ]);
        await stockProvider.DidNotReceive().GetHistoryAsync(
            Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        (await _db.RefreshRuns.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RunIfDueAsync_BothMarketsClosed_NoPriorRuns_RunsBoth_OneRefreshRunRowEach()
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

        result.MarketsRun.Should().Contain([Market.Nyse, Market.Sgx]);
        result.MarketsSkipped.Should().BeEmpty();
        result.Summary.Should().NotBeNull();
        result.Summary!.AssetsProcessed.Should().Contain(["AAPL", "Z74"]);

        var runs = await _db.RefreshRuns.ToListAsync();
        runs.Should().HaveCount(2);
        runs.Should().ContainSingle(r => r.Market == Market.Nyse && r.Trigger == RefreshTrigger.BackfillScheduled);
        runs.Should().ContainSingle(r => r.Market == Market.Sgx && r.Trigger == RefreshTrigger.BackfillScheduled);
    }

    [Fact]
    public async Task RunIfDueAsync_ManualRunEarlierToday_DoesNotCountAsTheScheduledRun()
    {
        // Only RefreshTrigger.BackfillScheduled counts toward a market's due-ness latch - a manual
        // click earlier today must not suppress the scheduled run, or a user who tests the manual
        // endpoint would accidentally starve the automatic one.
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
            Market = Market.Nyse,
            StartedAt = new DateTimeOffset(2026, 7, 26, 5, 0, 0, TimeSpan.Zero),
            CompletedAt = new DateTimeOffset(2026, 7, 26, 5, 0, 1, TimeSpan.Zero),
            Success = true,
            SymbolsRefreshed = 1,
        });
        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.BackfillManual,
            AssetClass = AssetClass.Stock,
            Market = Market.Sgx,
            StartedAt = new DateTimeOffset(2026, 7, 26, 5, 0, 0, TimeSpan.Zero),
            CompletedAt = new DateTimeOffset(2026, 7, 26, 5, 0, 1, TimeSpan.Zero),
            Success = true,
            SymbolsRefreshed = 1,
        });
        await _db.SaveChangesAsync();

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider); // AlwaysClosedCalendar

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.MarketsRun.Should().Contain([Market.Nyse, Market.Sgx]);
    }

    // --- D51: a FAILED scheduled run is due again for a bounded, paced, narrowly-scoped retry.
    // Regression tests confirmed to FAIL against the pre-D51 code (via a detached git worktree
    // probe, same technique D47 used) before the fix was written — see tracker.md's D51 entry.

    [Fact]
    public async Task RunIfDueAsync_D51_RetryPendingBeforeTheDelayElapses_IsNotDue_DistinctReason()
    {
        var closeInstant = new DateTimeOffset(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);
        var closeLocalDate = new DateOnly(2026, 8, 5);
        var calendar = CalendarWithFixedClose(Market.Nyse, closeInstant, closeLocalDate);

        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.BackfillScheduled,
            AssetClass = AssetClass.Stock,
            Market = Market.Nyse,
            StartedAt = closeInstant,
            CompletedAt = closeInstant.AddMinutes(5),
            Success = false, // the DNS-outage shape from the live 2026-09-11 incident
            SymbolsRefreshed = 0,
        });
        _db.RefreshRuns.Add(new RefreshRun // SGX already covered — kept out of this run's scope
        {
            Trigger = RefreshTrigger.BackfillScheduled, AssetClass = AssetClass.Stock, Market = Market.Sgx,
            StartedAt = closeInstant, CompletedAt = closeInstant, Success = true, SymbolsRefreshed = 1,
        });
        await _db.SaveChangesAsync();

        // Only 5 minutes after the failed run completed — well short of the default 15-minute
        // FailedRunRetryDelay.
        var probeTime = new FixedTimeProvider(closeInstant.AddMinutes(10));
        var stockProvider = Substitute.For<IQuoteProvider>();
        var fxProvider = Substitute.For<IFxRateProvider>();

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider, calendar: calendar, timeProvider: probeTime);

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.MarketsRun.Should().NotContain(Market.Nyse);
        result.MarketsSkipped.Should().ContainSingle(s => s.Market == Market.Nyse && s.Reason == PriceBackfillSkipReason.RetryPending);
        await stockProvider.DidNotReceive().GetHistoryAsync(
            Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunIfDueAsync_D51_RetryDueAfterTheDelayElapses_NarrowsToOnlyAssetsMissingTheLatestClose()
    {
        var msft = new Asset
        {
            Id = 2, Symbol = "MSFT", Name = "Microsoft", AssetClass = AssetClass.Stock, Currency = "USD",
            QuoteProviderKind = QuoteProviderKind.TwelveData, ProviderSymbol = "MSFT",
        };
        _db.Assets.Add(msft);
        _db.Transactions.Add(new Transaction
        {
            AssetId = msft.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 7, 1),
            Quantity = 1m, PricePerUnit = 300m, Fees = 0m, Currency = "USD",
        });

        var closeInstant = new DateTimeOffset(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);
        var closeLocalDate = new DateOnly(2026, 8, 5);

        // AAPL already holds the market's latest close — the retry must NOT re-fetch it.
        _db.PriceHistories.Add(new PriceHistory { AssetId = _aapl.Id, Date = closeLocalDate, Close = 200m, Currency = "USD" });

        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.BackfillScheduled, AssetClass = AssetClass.Stock, Market = Market.Nyse,
            StartedAt = closeInstant, CompletedAt = closeInstant.AddMinutes(5), Success = false, SymbolsRefreshed = 1,
        });
        _db.RefreshRuns.Add(new RefreshRun // SGX already covered — kept out of this run's scope
        {
            Trigger = RefreshTrigger.BackfillScheduled, AssetClass = AssetClass.Stock, Market = Market.Sgx,
            StartedAt = closeInstant, CompletedAt = closeInstant, Success = true, SymbolsRefreshed = 1,
        });
        await _db.SaveChangesAsync();

        var calendar = CalendarWithFixedClose(Market.Nyse, closeInstant, closeLocalDate);
        // 25 minutes after the close, 20 minutes after the failed run completed — past the default
        // 15-minute FailedRunRetryDelay.
        var probeTime = new FixedTimeProvider(closeInstant.AddMinutes(25));

        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(msft, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(closeLocalDate, 301m, "USD")], callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider, calendar: calendar, timeProvider: probeTime);

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.MarketsRun.Should().Contain(Market.Nyse);
        result.Summary!.AssetsProcessed.Should().Contain("MSFT").And.NotContain("AAPL");
        await stockProvider.DidNotReceive().GetHistoryAsync(
            _aapl, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        await stockProvider.Received(1).GetHistoryAsync(
            msft, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunIfDueAsync_D51_RetriesExhausted_IsNotDue_DistinctFromCoveredOrPending()
    {
        var closeInstant = new DateTimeOffset(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);
        var closeLocalDate = new DateOnly(2026, 8, 5);
        var calendar = CalendarWithFixedClose(Market.Nyse, closeInstant, closeLocalDate);

        // Two failed BackfillScheduled runs since this close (the full pass plus one retry) against
        // a MaxFailedRunRetriesPerClose of 1 — one retry is all this market gets.
        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.BackfillScheduled, AssetClass = AssetClass.Stock, Market = Market.Nyse,
            StartedAt = closeInstant, CompletedAt = closeInstant.AddMinutes(5), Success = false, SymbolsRefreshed = 0,
        });
        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.BackfillScheduled, AssetClass = AssetClass.Stock, Market = Market.Nyse,
            StartedAt = closeInstant.AddMinutes(30), CompletedAt = closeInstant.AddMinutes(30), Success = false, SymbolsRefreshed = 0,
        });
        _db.RefreshRuns.Add(new RefreshRun // SGX already covered — kept out of this run's scope
        {
            Trigger = RefreshTrigger.BackfillScheduled, AssetClass = AssetClass.Stock, Market = Market.Sgx,
            StartedAt = closeInstant, CompletedAt = closeInstant, Success = true, SymbolsRefreshed = 1,
        });
        await _db.SaveChangesAsync();

        var probeTime = new FixedTimeProvider(closeInstant.AddHours(1));
        var stockProvider = Substitute.For<IQuoteProvider>();
        var fxProvider = Substitute.For<IFxRateProvider>();

        var sut = CreateSut(
            RouterAlwaysReturning(stockProvider), fxProvider, calendar: calendar, timeProvider: probeTime, maxFailedRunRetriesPerClose: 1);

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.MarketsRun.Should().NotContain(Market.Nyse);
        result.MarketsSkipped.Should().ContainSingle(s => s.Market == Market.Nyse && s.Reason == PriceBackfillSkipReason.RetriesExhausted);
        await stockProvider.DidNotReceive().GetHistoryAsync(
            Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunIfDueAsync_D51_MostRecentRunSinceCloseSucceeded_IsNotDue_CoveredNotRetryReason()
    {
        var closeInstant = new DateTimeOffset(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);
        var closeLocalDate = new DateOnly(2026, 8, 5);
        var calendar = CalendarWithFixedClose(Market.Nyse, closeInstant, closeLocalDate);

        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.BackfillScheduled, AssetClass = AssetClass.Stock, Market = Market.Nyse,
            StartedAt = closeInstant, CompletedAt = closeInstant.AddMinutes(5), Success = true, SymbolsRefreshed = 1,
        });
        _db.RefreshRuns.Add(new RefreshRun // SGX already covered — kept out of this run's scope
        {
            Trigger = RefreshTrigger.BackfillScheduled, AssetClass = AssetClass.Stock, Market = Market.Sgx,
            StartedAt = closeInstant, CompletedAt = closeInstant, Success = true, SymbolsRefreshed = 1,
        });
        await _db.SaveChangesAsync();

        var probeTime = new FixedTimeProvider(closeInstant.AddHours(1));
        var stockProvider = Substitute.For<IQuoteProvider>();
        var fxProvider = Substitute.For<IFxRateProvider>();

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider, calendar: calendar, timeProvider: probeTime);

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.MarketsRun.Should().NotContain(Market.Nyse);
        result.MarketsSkipped.Should().ContainSingle(s => s.Market == Market.Nyse && s.Reason == PriceBackfillSkipReason.AlreadyCoveredSinceLastClose);
    }

    [Fact]
    public async Task RunAsync_D51_FxRetryRule_SkipsFxWhenAlreadyCoveringTheRequiredDate_ButStillFetchesAStalePrice()
    {
        // Proves the FX skip is judged on ITS OWN criterion (the FX table's own newest date), not
        // derived from whether the price loop happened to narrow the same asset out — Z74's own
        // price is deliberately left stale here so both loops' independence is visible in one test.
        var closeInstant = new DateTimeOffset(2026, 8, 5, 17, 0, 0, TimeSpan.Zero);
        var closeLocalDate = new DateOnly(2026, 8, 5);
        var calendar = CalendarWithFixedClose(Market.Sgx, closeInstant, closeLocalDate);

        _db.FxRates.Add(new FxRate { Date = closeLocalDate, Base = "USD", Quote = "SGD", Rate = 1.30m });

        _db.RefreshRuns.Add(new RefreshRun // NYSE already covered — kept out of this run's scope
        {
            Trigger = RefreshTrigger.BackfillScheduled, AssetClass = AssetClass.Stock, Market = Market.Nyse,
            StartedAt = closeInstant, CompletedAt = closeInstant, Success = true, SymbolsRefreshed = 1,
        });
        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.BackfillScheduled, AssetClass = AssetClass.Stock, Market = Market.Sgx,
            StartedAt = closeInstant, CompletedAt = closeInstant.AddMinutes(5), Success = false, SymbolsRefreshed = 0,
        });
        await _db.SaveChangesAsync();

        var probeTime = new FixedTimeProvider(closeInstant.AddMinutes(25));

        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(_z74, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(closeLocalDate, 4.05m, "SGD")], callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider, calendar: calendar, timeProvider: probeTime);

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.MarketsRun.Should().Contain(Market.Sgx);
        await fxProvider.DidNotReceive().GetHistoryAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        await stockProvider.Received(1).GetHistoryAsync(
            _z74, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_D51_FxRetryRule_FetchesFxWhenStale_EvenIfTheAssetItselfIsAlreadyUpToDate()
    {
        var closeInstant = new DateTimeOffset(2026, 8, 5, 17, 0, 0, TimeSpan.Zero);
        var closeLocalDate = new DateOnly(2026, 8, 5);
        var calendar = CalendarWithFixedClose(Market.Sgx, closeInstant, closeLocalDate);

        // Z74's own price is already up to date — the retry's price loop must skip it entirely...
        _db.PriceHistories.Add(new PriceHistory { AssetId = _z74.Id, Date = closeLocalDate, Close = 4m, Currency = "SGD" });
        // ...but USD/SGD's newest stored rate is a day stale, so the FX loop must still fetch it.
        _db.FxRates.Add(new FxRate { Date = closeLocalDate.AddDays(-1), Base = "USD", Quote = "SGD", Rate = 1.29m });

        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.BackfillScheduled, AssetClass = AssetClass.Stock, Market = Market.Nyse,
            StartedAt = closeInstant, CompletedAt = closeInstant, Success = true, SymbolsRefreshed = 1,
        });
        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.BackfillScheduled, AssetClass = AssetClass.Stock, Market = Market.Sgx,
            StartedAt = closeInstant, CompletedAt = closeInstant.AddMinutes(5), Success = false, SymbolsRefreshed = 0,
        });
        await _db.SaveChangesAsync();

        var probeTime = new FixedTimeProvider(closeInstant.AddMinutes(25));

        var stockProvider = Substitute.For<IQuoteProvider>();
        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync("USD", "SGD", Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(closeLocalDate, 1.31m)]));

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider, calendar: calendar, timeProvider: probeTime);

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.MarketsRun.Should().Contain(Market.Sgx);
        await fxProvider.Received(1).GetHistoryAsync(
            "USD", "SGD", Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        await stockProvider.DidNotReceive().GetHistoryAsync(
            _z74, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_D51_PerMarketSuccess_NyseAssetFailureDoesNotMarkSgxRowFailed_AndViceVersa()
    {
        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(_aapl, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new HistoryFetchResult([], new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 20), Truncated: false, Success: false, Error: "Twelve Data returned HTTP 400."));
        stockProvider.GetHistoryAsync(_z74, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 4m, "SGD")], callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]));

        var router = Substitute.For<IQuoteProviderRouter>();
        router.GetProvider(_aapl).Returns(stockProvider);
        router.GetProvider(_z74).Returns(stockProvider);

        var sut = CreateSut(router, fxProvider);

        await sut.RunAsync(RefreshTrigger.BackfillManual, BothMarkets, CancellationToken.None);

        var runs = await _db.RefreshRuns.ToListAsync();
        runs.Should().ContainSingle(r => r.Market == Market.Nyse && !r.Success, "AAPL (NYSE) failed");
        runs.Should().ContainSingle(r => r.Market == Market.Sgx && r.Success, "Z74 (SGX) and the FX it needs both succeeded, unrelated to NYSE's own failure");
    }

    [Fact]
    public async Task RunAsync_D51_PerMarketSuccess_FxFailureMarksOnlyTheMarketThatNeedsIt_AsFailed()
    {
        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 200m, "USD")], callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Failed("Twelve Data returned HTTP 429."));

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider);

        await sut.RunAsync(RefreshTrigger.BackfillManual, BothMarkets, CancellationToken.None);

        var runs = await _db.RefreshRuns.ToListAsync();
        // Only Z74/SGX (SGD-denominated) needs USD/SGD in this fixture — NYSE's only asset (AAPL)
        // is USD-denominated and needs no FX at all, so its row must stay Success even though the
        // shared FX call failed.
        runs.Should().ContainSingle(r => r.Market == Market.Sgx && !r.Success, "SGX's own asset (Z74) needs the USD/SGD rate that failed");
        runs.Should().ContainSingle(r => r.Market == Market.Nyse && r.Success, "AAPL needs no FX conversion at all");
    }

    [Fact]
    public async Task RunAsync_D51_ManualTrigger_IsAlwaysAFullPass_FetchesEvenAnAssetAlreadyHoldingTheLatestClose()
    {
        // Only RunIfDueAsync's own scheduled retries narrow scope (D51). The manual RunAsync (used
        // by POST /api/prices/backfill) must behave exactly as before D51 — a full pass regardless
        // of what is already on file, because it is also what fills history for a newly recorded
        // back-dated transaction on an asset whose latest close happens to already be there.
        _db.PriceHistories.Add(new PriceHistory { AssetId = _aapl.Id, Date = new DateOnly(2026, 7, 21), Close = 201m, Currency = "USD" });

        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 21), 202m, "USD")], callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]));

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider);

        await sut.RunAsync(RefreshTrigger.BackfillManual, BothMarkets, CancellationToken.None);

        await stockProvider.Received(1).GetHistoryAsync(
            _aapl, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }
}
