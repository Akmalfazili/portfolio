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
    /// silently-gated one.
    ///
    /// D53: <c>RunAsync</c> now ALSO consults the calendar unconditionally, to compute each
    /// market's settled cap (<c>capByMarket</c> — see <c>PriceBackfillService</c>'s class remarks).
    /// <c>LastSessionCloseAt</c>/<c>LocalDateOn</c> are stubbed here to resolve to the settled
    /// instant's own UTC date, regardless of market or instant passed — "just closed, right now" —
    /// so a test that only cares about ordinary asset/FX plumbing (the overwhelming majority of the
    /// tests in this file) sees a cap that comfortably covers every fixture date (all in the past
    /// relative to <c>_timeProvider</c>'s fixed "now") without needing to know anything about D53.
    /// Tests that ARE about the cap itself use <see cref="CalendarWithFixedClose"/> or a real
    /// <see cref="MarketCalendar"/> instead, exactly as the D51 retry tests already do.</summary>
    private static IMarketCalendar AlwaysClosedCalendar()
    {
        var calendar = Substitute.For<IMarketCalendar>();
        calendar.IsOpen(Arg.Any<Market>(), Arg.Any<DateTimeOffset>()).Returns(false);
        calendar.LastSessionCloseAt(Arg.Any<Market>(), Arg.Any<DateTimeOffset>())
            .Returns(callInfo => callInfo.ArgAt<DateTimeOffset>(1));
        calendar.LocalDateOn(Arg.Any<Market>(), Arg.Any<DateTimeOffset>())
            .Returns(callInfo => DateOnly.FromDateTime(callInfo.ArgAt<DateTimeOffset>(1).UtcDateTime));
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
    public async Task D47_Regression_SgxDueAfter1700Sgt_EvenThoughAScheduledRunCompletedAt0405SameSgtDay()
    {
        // THE regression this closed. Confirmed (see tracker.md and this session's own probe) to
        // FAIL against the pre-fix code with PriceBackfillOutcome.AlreadyRanToday: the old gate was
        // ONE NYSE-keyed "is the market open" check plus ONE once-per-Singapore-day latch shared by
        // every asset. A run completing at 04:05 SGT (right after NYSE's ~04:00 SGT close) used to
        // suppress a second run for the rest of the SGT day — even though SGX's OWN session for
        // that day had not even opened yet at 04:05, and its 17:00 SGT close (published at 17:00,
        // never fetched) then had to wait until the following day's 04:05 run, ~11 hours late.
        //
        // D53 note: the probe instant below was originally 17:05 SGT (five minutes after SGX's
        // close) and is now 17:35 SGT — D53's CloseSettleDelay (default 30 minutes) means due-ness
        // is judged on the SETTLED instant, not raw "now", so SGX does not read due again until 30
        // minutes after its own close, not the instant the calendar starts reporting it closed. See
        // PriceBackfillOptions.CloseSettleDelay and the D53 tests below for that gap on its own.
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

        // "Now" = 17:35 SGT on 2026-08-05 (2026-08-05T09:35:00Z) — 35 minutes past SGX's 17:00
        // close, past the default 30-minute CloseSettleDelay (D53), so TODAY's (2026-08-05) close
        // now reads as settled — never covered by the seeded SGX row above (completed the previous
        // SGT day, before SGX's 2026-08-05 session even opened).
        var probeTime = new FixedTimeProvider(new DateTimeOffset(2026, 8, 5, 9, 35, 0, TimeSpan.Zero));

        // Real calendar so SGX genuinely reads "closed" at 17:35 SGT (after its own 17:00 close)
        // and NYSE genuinely reads "closed" too (09:35 UTC = 05:35 ET, before NYSE's 09:30 open) —
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

    // --- D53: a market reading "closed" is not the same as its close being SETTLED. Covers the
    // per-market cap (capByMarket) for every trigger, including manual (which bypasses due-ness and
    // the market calendar entirely but must never bypass the settle cap), and the due-ness gate's
    // own use of the same settled instant. See tracker.md's D53 entry for the live evidence.

    [Fact]
    public async Task RunAsync_D53_ManualRunMidSgxSession_DoesNotStoreTodaysPartialBar_EvenIfTheProviderReturnsOne()
    {
        // The core defect, reproduced directly: Yahoo's chart endpoint has no documented guarantee
        // that `period2` (the requested end) is honoured, so the provider mock here deliberately
        // returns TODAY's bar anyway, alongside a genuine prior-day close — proving the cap's
        // defense-in-depth filter (not just the requested range) is what keeps it out, not merely a
        // cooperative provider.
        var today = new DateOnly(2026, 7, 29); // ordinary Wednesday
        var yesterday = new DateOnly(2026, 7, 28); // ordinary Tuesday, no SGX holiday

        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(_z74, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [
                    new PriceHistoryPoint(yesterday, 4.49m, "SGD"),
                    new PriceHistoryPoint(today, 4.55m, "SGD"), // today's still-updating price
                ],
                callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(yesterday, 1.29m)]));

        // 10:00 SGT on 2026-07-29 (02:00 UTC) — mid-session, well before the 17:00 close.
        var midSession = new FixedTimeProvider(new DateTimeOffset(2026, 7, 29, 2, 0, 0, TimeSpan.Zero));
        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider, calendar: new MarketCalendar(), timeProvider: midSession);

        var summary = await sut.RunAsync(RefreshTrigger.BackfillManual, [Market.Sgx], CancellationToken.None);

        summary.AssetsProcessed.Should().Contain("Z74");
        (await _db.PriceHistories.Where(p => p.AssetId == _z74.Id && p.Date == today).CountAsync()).Should().Be(0,
            "today's SGX session has not finished, let alone settled");
        (await _db.PriceHistories.Where(p => p.AssetId == _z74.Id && p.Date == yesterday).CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_D53_ManualRunDuringSgxLunchBreak_DoesNotStoreTodaysPartialBar()
    {
        var today = new DateOnly(2026, 7, 29);
        var yesterday = new DateOnly(2026, 7, 28);

        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(_z74, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [
                    new PriceHistoryPoint(yesterday, 4.49m, "SGD"),
                    new PriceHistoryPoint(today, 4.55m, "SGD"),
                ],
                callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(yesterday, 1.29m)]));

        // 12:48 SGT on 2026-07-29 (04:48 UTC) — SGX's own midday lunch break. IsOpen already reads
        // false here (verified below), which is exactly why LastSessionCloseAt — not IsOpen — must
        // be the mechanism that keeps the session's own unfinished day out: IsOpen going false at
        // noon says nothing about whether the day's close has happened yet.
        var lunchBreak = new FixedTimeProvider(new DateTimeOffset(2026, 7, 29, 4, 48, 0, TimeSpan.Zero));
        var calendar = new MarketCalendar();
        calendar.IsOpen(Market.Sgx, lunchBreak.GetUtcNow()).Should().BeFalse(
            "SGX's lunch break reads as closed even though the session itself is not over");

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider, calendar: calendar, timeProvider: lunchBreak);

        await sut.RunAsync(RefreshTrigger.BackfillManual, [Market.Sgx], CancellationToken.None);

        (await _db.PriceHistories.Where(p => p.AssetId == _z74.Id && p.Date == today).CountAsync()).Should().Be(0);
        (await _db.PriceHistories.Where(p => p.AssetId == _z74.Id && p.Date == yesterday).CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_D53_ManualRunMidNyseSession_DoesNotStoreTodaysPartialBar()
    {
        // The NYSE equivalent of the SGX cases above — the per-market cap applies identically
        // regardless of which provider (Twelve Data here, Yahoo above) sits behind it.
        var today = new DateOnly(2026, 7, 29);
        var yesterday = new DateOnly(2026, 7, 28);

        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(_aapl, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [
                    new PriceHistoryPoint(yesterday, 200m, "USD"),
                    new PriceHistoryPoint(today, 205m, "USD"),
                ],
                callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>(); // AAPL is USD — never called

        // Noon ET on 2026-07-29 (16:00 UTC — EDT is UTC-4 in July) — mid-session, well before the
        // 16:00 ET close.
        var midSession = new FixedTimeProvider(new DateTimeOffset(2026, 7, 29, 16, 0, 0, TimeSpan.Zero));
        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider, calendar: new MarketCalendar(), timeProvider: midSession);

        await sut.RunAsync(RefreshTrigger.BackfillManual, [Market.Nyse], CancellationToken.None);

        (await _db.PriceHistories.Where(p => p.AssetId == _aapl.Id && p.Date == today).CountAsync()).Should().Be(0);
        (await _db.PriceHistories.Where(p => p.AssetId == _aapl.Id && p.Date == yesterday).CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_D53_FromBeyondMarketCap_IsDeferred_NotFailed_AndNeverCallsTheProvider()
    {
        // Generalizes the pre-D53 `from == today` guard: the earliest date an asset needs can be
        // beyond its own market's settled cap WITHOUT being literally ReportingClock's "today" —
        // exactly what happens once the cap itself can fall behind raw "today" (a lunch break, a
        // closing-auction window, or simply CloseSettleDelay). FUT's earliest trade date is
        // deliberately capDate + 1 — nowhere near this fixture's own dates — to prove the guard now
        // reads the actual per-market cap, not a hardcoded comparison to "today".
        var existingTransactions = _db.Transactions.ToList();
        _db.Transactions.RemoveRange(existingTransactions);
        _db.Assets.RemoveRange(_aapl, _z74);

        var capDate = new DateOnly(2026, 8, 4);
        var future = new Asset
        {
            Id = 10, Symbol = "FUT", Name = "Future Co", AssetClass = AssetClass.Stock, Currency = "USD",
            QuoteProviderKind = QuoteProviderKind.TwelveData, ProviderSymbol = "FUT",
        };
        _db.Assets.Add(future);
        _db.Transactions.Add(new Transaction
        {
            AssetId = future.Id, Type = TransactionType.Buy, TradeDate = capDate.AddDays(1),
            Quantity = 1m, PricePerUnit = 10m, Fees = 0m, Currency = "USD",
        });
        await _db.SaveChangesAsync();

        var calendar = CalendarWithFixedClose(Market.Nyse, new DateTimeOffset(2026, 8, 4, 20, 0, 0, TimeSpan.Zero), capDate);
        var stockProvider = Substitute.For<IQuoteProvider>();
        var fxProvider = Substitute.For<IFxRateProvider>(); // USD-only fixture — never called

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider, calendar: calendar);

        var summary = await sut.RunAsync(RefreshTrigger.BackfillManual, [Market.Nyse], CancellationToken.None);

        summary.AssetsSkippedTodayNotClosed.Should().Contain("FUT");
        summary.AssetsFailed.Should().BeEmpty();
        summary.AssetsSkippedForBudget.Should().BeEmpty();
        summary.AssetsProcessed.Should().BeEmpty();
        await stockProvider.DidNotReceive().GetHistoryAsync(
            Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunIfDueAsync_D53_WithinCloseSettleDelay_SgxIsNotDue_EvenThoughRawNowWouldReadItUncovered()
    {
        // The scheduling half of D53: at 17:10 SGT (10 minutes after SGX's nominal 17:00 close,
        // inside the default 30-minute CloseSettleDelay), a due-ness check keyed on raw "now" would
        // read SGX as closed and uncovered since today's close (only yesterday's is on file) and
        // call it due — RunAsync would then defer every asset via the cap (still yesterday's close,
        // not yet settled) and still write a SUCCESSFUL RefreshRun row for "today's" close, which
        // would then read as AlreadyCoveredSinceLastClose on every later check — D51's trap one
        // layer in. Due-ness must stay on the SAME settled instant the cap uses, so this market
        // simply isn't due yet.
        var stockProvider = Substitute.For<IQuoteProvider>();
        var fxProvider = Substitute.For<IFxRateProvider>();

        // A normal daily run completed just after SGX's PREVIOUS (2026-07-28) close.
        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.BackfillScheduled, AssetClass = AssetClass.Stock, Market = Market.Sgx,
            StartedAt = new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.Zero),
            CompletedAt = new DateTimeOffset(2026, 7, 28, 9, 5, 0, TimeSpan.Zero), // 17:05 SGT on 7/28
            Success = true,
            SymbolsRefreshed = 1,
        });

        // 17:10 SGT on 2026-07-29 (09:10 UTC).
        var probeInstant = new DateTimeOffset(2026, 7, 29, 9, 10, 0, TimeSpan.Zero);
        _db.RefreshRuns.Add(new RefreshRun // NYSE already covered — kept out of this run's scope
        {
            Trigger = RefreshTrigger.BackfillScheduled, AssetClass = AssetClass.Stock, Market = Market.Nyse,
            StartedAt = probeInstant, CompletedAt = probeInstant, Success = true, SymbolsRefreshed = 1,
        });
        await _db.SaveChangesAsync();

        var probeTime = new FixedTimeProvider(probeInstant);
        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider, calendar: new MarketCalendar(), timeProvider: probeTime);

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.MarketsRun.Should().NotContain(Market.Sgx);
        result.MarketsSkipped.Should().ContainSingle(s => s.Market == Market.Sgx && s.Reason == PriceBackfillSkipReason.AlreadyCoveredSinceLastClose);
        await stockProvider.DidNotReceive().GetHistoryAsync(
            Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunIfDueAsync_D53_AfterCloseSettleDelayElapses_SgxIsDue_AndFetchesTodaysNowSettledClose()
    {
        // The other half: once CloseSettleDelay has actually elapsed since SGX's close, the
        // scheduled path must both consider SGX due AND actually fetch (and store) today's close —
        // the settle delay is a temporary hold, not a permanent one.
        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(_z74, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 29), 4.50m, "SGD")], callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 28), 1.30m)]));

        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.BackfillScheduled, AssetClass = AssetClass.Stock, Market = Market.Sgx,
            StartedAt = new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.Zero),
            CompletedAt = new DateTimeOffset(2026, 7, 28, 9, 5, 0, TimeSpan.Zero),
            Success = true,
            SymbolsRefreshed = 1,
        });

        // 17:35 SGT on 2026-07-29 (09:35 UTC) — 35 minutes after today's close, past the 30-minute
        // settle delay.
        var probeInstant = new DateTimeOffset(2026, 7, 29, 9, 35, 0, TimeSpan.Zero);
        _db.RefreshRuns.Add(new RefreshRun // NYSE already covered — kept out of this run's scope
        {
            Trigger = RefreshTrigger.BackfillScheduled, AssetClass = AssetClass.Stock, Market = Market.Nyse,
            StartedAt = probeInstant, CompletedAt = probeInstant, Success = true, SymbolsRefreshed = 1,
        });
        await _db.SaveChangesAsync();

        var probeTime = new FixedTimeProvider(probeInstant);
        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider, calendar: new MarketCalendar(), timeProvider: probeTime);

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.MarketsRun.Should().Contain(Market.Sgx);
        result.Summary!.AssetsProcessed.Should().Contain("Z74");
        (await _db.PriceHistories.Where(p => p.AssetId == _z74.Id && p.Date == new DateOnly(2026, 7, 29)).CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RunIfDueAsync_D53_AfterNyseCloseAndSettleDelay_ScheduledRunRequestsThatDayInclusive_AndStoresItsClose()
    {
        // The end-to-end contract check for the D53 `end_date` follow-up: PriceBackfillService
        // always treats its own `cap` as INCLUSIVE (IQuoteProvider.GetHistoryAsync's contract),
        // regardless of what any one provider's wire format needs to achieve that —
        // TwelveDataQuoteProviderTests covers the wire format itself (`end_date = to + 1`). This
        // proves the SERVICE's own `to` argument is the closed day itself, not one day short of it,
        // and that the close returned for that exact day is the one that gets stored.
        var closeDate = new DateOnly(2026, 7, 29); // ordinary Wednesday
        DateOnly? requestedTo = null;

        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(_aapl, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                requestedTo = callInfo.ArgAt<DateOnly>(2);
                return Task.FromResult(HistoryFetchResult.Ok(
                    [new PriceHistoryPoint(closeDate, 210m, "USD")], callInfo.ArgAt<DateOnly>(1)));
            });

        var fxProvider = Substitute.For<IFxRateProvider>(); // AAPL is USD — never called

        // NYSE closes 16:00 ET (20:00 UTC in July, EDT). 35 minutes later, past the 30-minute
        // settle delay: 20:35 UTC on 2026-07-29.
        var probeInstant = new DateTimeOffset(2026, 7, 29, 20, 35, 0, TimeSpan.Zero);
        _db.RefreshRuns.Add(new RefreshRun // SGX already covered — kept out of this run's scope
        {
            Trigger = RefreshTrigger.BackfillScheduled, AssetClass = AssetClass.Stock, Market = Market.Sgx,
            StartedAt = probeInstant, CompletedAt = probeInstant, Success = true, SymbolsRefreshed = 1,
        });
        await _db.SaveChangesAsync();

        var probeTime = new FixedTimeProvider(probeInstant);
        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider, calendar: new MarketCalendar(), timeProvider: probeTime);

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.MarketsRun.Should().Contain(Market.Nyse);
        requestedTo.Should().Be(closeDate, "the service's own `to` is the closed day ITSELF — inclusive, never one short");
        (await _db.PriceHistories.Where(p => p.AssetId == _aapl.Id && p.Date == closeDate).CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_D53_FxNeverRequestsOrStoresTodaysRate_EvenIfTheProviderReturnsOne()
    {
        // FX has no market session to key a settle cap off (see `fxCap` in PriceBackfillService's
        // class remarks), so it is capped to one UTC calendar day behind `now` regardless of either
        // equity market's own session state. The FX provider mock here deliberately returns a rate
        // dated "today" anyway, proving the defensive per-point filter — not just the requested
        // range — is what keeps CLAUDE.md's "a live spot rate is never written into FxRates" rule
        // intact even against an uncooperative provider.
        var utcNow = new DateTimeOffset(2026, 7, 29, 3, 0, 0, TimeSpan.Zero);
        var utcToday = DateOnly.FromDateTime(utcNow.UtcDateTime);
        var utcYesterday = utcToday.AddDays(-1);

        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(utcYesterday, 4m, "SGD")], callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok(
            [
                new FxRatePoint(utcYesterday, 1.29m),
                new FxRatePoint(utcToday, 1.31m), // the live-spot-into-FxRates violation CLAUDE.md warns about
            ]));

        var fixedNow = new FixedTimeProvider(utcNow);
        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider, calendar: AlwaysClosedCalendar(), timeProvider: fixedNow);

        await sut.RunAsync(RefreshTrigger.BackfillManual, BothMarkets, CancellationToken.None);

        await fxProvider.Received(1).GetHistoryAsync(
            "USD", "SGD", Arg.Any<DateOnly>(), utcYesterday, Arg.Any<CancellationToken>());
        (await _db.FxRates.Where(f => f.Date == utcToday).CountAsync()).Should().Be(0);
        (await _db.FxRates.Where(f => f.Date == utcYesterday).CountAsync()).Should().Be(1);
    }

    // --- Refresh catch-up feature: AssetPriceHistoryState writing (RunAsyncCore, every trigger)
    // and RunCatchUpAsync's asset/currency narrowing.

    [Fact]
    public async Task RunAsync_SuccessfulFetch_WritesAssetPriceHistoryState_WithTheRequestedCoverage()
    {
        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 200m, "USD")], callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]));

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider);

        await sut.RunAsync(RefreshTrigger.BackfillManual, BothMarkets, CancellationToken.None);

        var state = await _db.AssetPriceHistoryStates.SingleAsync(s => s.AssetId == _aapl.Id);
        state.LastRunSuccess.Should().BeTrue();
        state.LastSuccessAt.Should().Be(_timeProvider.GetUtcNow());
        state.CoveredFrom.Should().Be(new DateOnly(2026, 7, 20)); // AAPL's own earliest trade date
        // AlwaysClosedCalendar's LocalDateOn/LastSessionCloseAt resolve the cap to the settled
        // instant's own UTC date — _timeProvider is fixed at 2026-07-26T00:00Z, minus the default
        // 30-minute CloseSettleDelay, still 2026-07-25.
        state.CoveredTo.Should().Be(new DateOnly(2026, 7, 25));
    }

    [Fact]
    public async Task RunAsync_FailedFetch_WritesAssetPriceHistoryState_ButLeavesCoveredFromCoveredToUntouched()
    {
        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 200m, "USD")], callInfo.ArgAt<DateOnly>(1))));
        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]));

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider);
        await sut.RunAsync(RefreshTrigger.BackfillManual, BothMarkets, CancellationToken.None);

        var covered = await _db.AssetPriceHistoryStates.SingleAsync(s => s.AssetId == _aapl.Id);
        var (originalFrom, originalTo) = (covered.CoveredFrom, covered.CoveredTo);

        var failingProvider = Substitute.For<IQuoteProvider>();
        failingProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new HistoryFetchResult([], new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 20), Truncated: false, Success: false, Error: "Twelve Data returned HTTP 400."));
        var sut2 = CreateSut(RouterAlwaysReturning(failingProvider), fxProvider);
        await sut2.RunAsync(RefreshTrigger.BackfillManual, BothMarkets, CancellationToken.None);

        var state = await _db.AssetPriceHistoryStates.SingleAsync(s => s.AssetId == _aapl.Id);
        state.LastRunSuccess.Should().BeFalse();
        state.LastError.Should().Be("Twelve Data returned HTTP 400.");
        state.CoveredFrom.Should().Be(originalFrom, "a failed attempt must never shrink known coverage");
        state.CoveredTo.Should().Be(originalTo);
    }

    [Fact]
    public async Task RunAsync_TruncatedFetch_RecordsCoveredFromAsTheRequestedFrom_NotTheEffectiveFrom()
    {
        // AAPL's own earliest trade date (the REQUESTED `from`) is 2026-07-20 per the fixture — the
        // provider truncating its response to a later effective start must not change what
        // CoveredFrom records: re-asking for 2026-07-20 will never return more, by provider policy.
        var effectiveFrom = new DateOnly(2026, 7, 22);
        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(_aapl, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new HistoryFetchResult(
                [new PriceHistoryPoint(effectiveFrom, 200m, "USD")],
                new DateOnly(2026, 7, 20),
                effectiveFrom,
                Truncated: true,
                Success: true,
                Error: null));
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

        var state = await _db.AssetPriceHistoryStates.SingleAsync(s => s.AssetId == _aapl.Id);
        state.LastRunSuccess.Should().BeTrue();
        state.CoveredFrom.Should().Be(new DateOnly(2026, 7, 20), "the REQUESTED from, not the provider's truncated effective start");
    }

    [Fact]
    public async Task RunCatchUpAsync_RestrictsToTheGivenAssetIdsAndCurrencies_AndTagsTheCatchUpTrigger()
    {
        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 4m, "SGD")], callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]));

        var router = Substitute.For<IQuoteProviderRouter>();
        router.GetProvider(Arg.Any<Asset>()).Returns(stockProvider);

        var sut = CreateSut(router, fxProvider);

        var summary = await sut.RunCatchUpAsync(
            [Market.Sgx], new HashSet<int> { _z74.Id }, new HashSet<string> { "SGD" }, CancellationToken.None);

        summary.AssetsProcessed.Should().Contain("Z74").And.NotContain("AAPL");
        router.DidNotReceive().GetProvider(_aapl);
        summary.FxRatePointsInserted.Should().Be(1);

        var run = (await _db.RefreshRuns.ToListAsync()).Should().ContainSingle().Subject;
        run.Trigger.Should().Be(RefreshTrigger.BackfillCatchUp);
        run.Market.Should().Be(Market.Sgx);
    }

    [Fact]
    public async Task RunIfDueAsync_DueNessIsUnaffectedByAPriorCatchUpRun()
    {
        // Pins the spec's "neither new trigger affects scheduled due-ness" requirement: a
        // BackfillCatchUp row (however productive) must never satisfy RunIfDueAsync's
        // BackfillScheduled-only query — both markets must still read as due.
        var now = _timeProvider.GetUtcNow();
        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.BackfillCatchUp, AssetClass = AssetClass.Stock, Market = Market.Nyse,
            StartedAt = now, CompletedAt = now, Success = true, SymbolsRefreshed = 1,
        });
        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.BackfillCatchUp, AssetClass = AssetClass.Stock, Market = Market.Sgx,
            StartedAt = now, CompletedAt = now, Success = true, SymbolsRefreshed = 1,
        });
        await _db.SaveChangesAsync();

        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 200m, "USD")], callInfo.ArgAt<DateOnly>(1))));
        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]));

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider);

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.MarketsRun.Should().Contain([Market.Nyse, Market.Sgx]);
    }

    [Fact]
    public async Task RunAsync_SuccessfulFxFetch_WritesFxPairBackfillState_WithTheRequestedCoverage()
    {
        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 200m, "USD")], callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]));

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider);

        await sut.RunAsync(RefreshTrigger.BackfillManual, BothMarkets, CancellationToken.None);

        var state = await _db.FxPairBackfillStates.SingleAsync(s => s.Base == "USD" && s.Quote == "SGD");
        state.LastRunSuccess.Should().BeTrue();
        state.CoveredFrom.Should().Be(new DateOnly(2026, 7, 20)); // Z74's own earliest trade date
        // PriceBackfillCapCalculator.ComputeFxCap(now): _timeProvider is fixed at 2026-07-26T00:00Z
        // UTC, one day behind is 2026-07-25 — never a market's own settled cap.
        state.CoveredTo.Should().Be(new DateOnly(2026, 7, 25));
    }

    [Fact]
    public async Task RunAsync_FailedFxFetch_WritesFxPairBackfillState_ButLeavesCoveredFromCoveredToUntouched()
    {
        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 200m, "USD")], callInfo.ArgAt<DateOnly>(1))));
        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]));
        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider);
        await sut.RunAsync(RefreshTrigger.BackfillManual, BothMarkets, CancellationToken.None);

        var covered = await _db.FxPairBackfillStates.SingleAsync(s => s.Base == "USD" && s.Quote == "SGD");
        var (originalFrom, originalTo) = (covered.CoveredFrom, covered.CoveredTo);

        var failingFxProvider = Substitute.For<IFxRateProvider>();
        failingFxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Failed("Twelve Data returned HTTP 429."));
        var sut2 = CreateSut(RouterAlwaysReturning(stockProvider), failingFxProvider);
        await sut2.RunAsync(RefreshTrigger.BackfillManual, BothMarkets, CancellationToken.None);

        var state = await _db.FxPairBackfillStates.SingleAsync(s => s.Base == "USD" && s.Quote == "SGD");
        state.LastRunSuccess.Should().BeFalse();
        state.LastError.Should().Be("Twelve Data returned HTTP 429.");
        state.CoveredFrom.Should().Be(originalFrom, "a failed attempt must never shrink known coverage");
        state.CoveredTo.Should().Be(originalTo);
    }

    // --- 2026-09-15: BackfillCatchUp and a D51 narrowed retry request only the tail coverage
    // state proves is missing, not the full range from the asset's/currency's earliest trade
    // date. See PriceBackfillService's class remarks for the live evidence (a catch-up requesting
    // six years of USD/SGD history for one missing day) and the coverage-merge trap this closed.

    [Fact]
    public async Task RunCatchUpAsync_AssetWithCoveringState_RequestsOnlyFromCoveredToPlusOne()
    {
        _db.AssetPriceHistoryStates.Add(new AssetPriceHistoryState
        {
            AssetId = _aapl.Id,
            LastAttemptedAt = _timeProvider.GetUtcNow(),
            LastSuccessAt = _timeProvider.GetUtcNow(),
            LastRunSuccess = true,
            CoveredFrom = new DateOnly(2026, 7, 20), // AAPL's own earliest trade date
            CoveredTo = new DateOnly(2026, 7, 22),
        });
        // Narrowing requires the newest STORED row to corroborate the state (see the class
        // remarks) — a prior successful fetch actually returned this bar, so both agree here.
        _db.PriceHistories.Add(new PriceHistory { AssetId = _aapl.Id, Date = new DateOnly(2026, 7, 22), Close = 200m, Currency = "USD" });
        await _db.SaveChangesAsync();

        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 23), 201m, "USD")], callInfo.ArgAt<DateOnly>(1))));

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), Substitute.For<IFxRateProvider>());

        await sut.RunCatchUpAsync([Market.Nyse], new HashSet<int> { _aapl.Id }, new HashSet<string>(), CancellationToken.None);

        // AlwaysClosedCalendar's cap resolves to _timeProvider's fixed "now" (2026-07-26T00:00Z)
        // minus the default 30-minute CloseSettleDelay, still 2026-07-25 — well past the narrowed
        // `from` below, so this is an ordinary narrowed fetch, not the already-covered case.
        await stockProvider.Received(1).GetHistoryAsync(
            _aapl, new DateOnly(2026, 7, 23), new DateOnly(2026, 7, 25), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCatchUpAsync_BackDatedTradeBeforeCoveredFrom_FallsBackToTheFullRange()
    {
        // Simulates a back-dated transaction recorded AFTER the state above was written: AAPL's
        // own earliest trade is now BEFORE CoveredFrom, so the state does not prove this newly
        // relevant early history was ever asked for — narrowing must not apply.
        _db.Transactions.Add(new Transaction
        {
            AssetId = _aapl.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 7, 15),
            Quantity = 1m, PricePerUnit = 190m, Fees = 0m, Currency = "USD",
        });
        _db.AssetPriceHistoryStates.Add(new AssetPriceHistoryState
        {
            AssetId = _aapl.Id, LastAttemptedAt = _timeProvider.GetUtcNow(), LastSuccessAt = _timeProvider.GetUtcNow(),
            LastRunSuccess = true, CoveredFrom = new DateOnly(2026, 7, 20), CoveredTo = new DateOnly(2026, 7, 22),
        });
        await _db.SaveChangesAsync();

        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 15), 190m, "USD")], callInfo.ArgAt<DateOnly>(1))));

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), Substitute.For<IFxRateProvider>());

        await sut.RunCatchUpAsync([Market.Nyse], new HashSet<int> { _aapl.Id }, new HashSet<string>(), CancellationToken.None);

        await stockProvider.Received(1).GetHistoryAsync(
            _aapl, new DateOnly(2026, 7, 15), new DateOnly(2026, 7, 25), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCatchUpAsync_NoState_RequestsTheFullRange()
    {
        // No AssetPriceHistoryState at all (a genuinely never-attempted asset) — narrowing has
        // nothing to narrow against, so this must behave exactly as before: the full range from
        // the asset's own earliest trade date.
        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 200m, "USD")], callInfo.ArgAt<DateOnly>(1))));

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), Substitute.For<IFxRateProvider>());

        await sut.RunCatchUpAsync([Market.Nyse], new HashSet<int> { _aapl.Id }, new HashSet<string>(), CancellationToken.None);

        await stockProvider.Received(1).GetHistoryAsync(
            _aapl, new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 25), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCatchUpAsync_FxWithCoveringState_RequestsOnlyFromCoveredToPlusOne()
    {
        _db.FxPairBackfillStates.Add(new FxPairBackfillState
        {
            Base = "USD", Quote = "SGD",
            LastAttemptedAt = _timeProvider.GetUtcNow(), LastSuccessAt = _timeProvider.GetUtcNow(), LastRunSuccess = true,
            CoveredFrom = new DateOnly(2026, 7, 20), // Z74's own earliest trade date
            CoveredTo = new DateOnly(2026, 7, 22),
        });
        // Narrowing requires the newest STORED FxRate to corroborate the state (see the class
        // remarks) — a prior successful fetch actually returned this rate, so both agree here.
        _db.FxRates.Add(new FxRate { Date = new DateOnly(2026, 7, 22), Base = "USD", Quote = "SGD", Rate = 1.295m });
        await _db.SaveChangesAsync();

        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 23), 4.01m, "SGD")], callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 23), 1.30m)]));

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider);

        // Z74 itself is NOT in the fetch set here — this is the planner's "FX missing on its own
        // upper-bound criterion, no asset of that currency actually being fetched" case (see
        // IRefreshCatchUpService's remarks) — narrowing must still apply, driven by
        // RefreshTrigger.BackfillCatchUp alone.
        await sut.RunCatchUpAsync([Market.Sgx], new HashSet<int>(), new HashSet<string> { "SGD" }, CancellationToken.None);

        // ComputeFxCap(now): _timeProvider fixed at 2026-07-26T00:00Z UTC, one day behind is 2026-07-25.
        await fxProvider.Received(1).GetHistoryAsync(
            "USD", "SGD", new DateOnly(2026, 7, 23), new DateOnly(2026, 7, 25), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCatchUpAsync_FxForcedByBackDatedAsset_FallsBackToTheFullRange()
    {
        // Z74's own earliest trade is back-dated BEFORE the FX state's CoveredFrom — the state
        // does not prove that newly-relevant early history was ever asked for, so even though this
        // is the "hard requirement" case (Z74 itself is being fetched), FX narrowing must not apply.
        _db.Transactions.Add(new Transaction
        {
            AssetId = _z74.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 7, 15),
            Quantity = 10m, PricePerUnit = 3.9m, Fees = 0m, Currency = "SGD",
        });
        _db.FxPairBackfillStates.Add(new FxPairBackfillState
        {
            Base = "USD", Quote = "SGD",
            LastAttemptedAt = _timeProvider.GetUtcNow(), LastSuccessAt = _timeProvider.GetUtcNow(), LastRunSuccess = true,
            CoveredFrom = new DateOnly(2026, 7, 20), CoveredTo = new DateOnly(2026, 7, 22),
        });
        await _db.SaveChangesAsync();

        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 15), 3.9m, "SGD")], callInfo.ArgAt<DateOnly>(1))));

        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 15), 1.29m)]));

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider);

        await sut.RunCatchUpAsync([Market.Sgx], new HashSet<int> { _z74.Id }, new HashSet<string> { "SGD" }, CancellationToken.None);

        await fxProvider.Received(1).GetHistoryAsync(
            "USD", "SGD", new DateOnly(2026, 7, 15), new DateOnly(2026, 7, 25), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunIfDueAsync_D51_NarrowedRetry_RequestsOnlyFromCoveredToPlusOne_NotTheFullRange()
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
        _db.AssetPriceHistoryStates.Add(new AssetPriceHistoryState
        {
            AssetId = msft.Id, LastAttemptedAt = new DateTimeOffset(2026, 8, 3, 9, 0, 0, TimeSpan.Zero),
            LastSuccessAt = new DateTimeOffset(2026, 8, 3, 9, 0, 0, TimeSpan.Zero), LastRunSuccess = true,
            CoveredFrom = new DateOnly(2026, 7, 1), CoveredTo = new DateOnly(2026, 8, 3),
        });
        // Narrowing requires the newest STORED row to corroborate the state (see the class
        // remarks) — a prior successful fetch actually returned this bar, so both agree here.
        _db.PriceHistories.Add(new PriceHistory { AssetId = msft.Id, Date = new DateOnly(2026, 8, 3), Close = 299m, Currency = "USD" });

        var closeInstant = new DateTimeOffset(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);
        var closeLocalDate = new DateOnly(2026, 8, 5);

        // AAPL already holds the market's latest close — the retry's own stored-rows pre-filter
        // must exclude it (unrelated to this test's own narrowing assertion, but needed so it's
        // never handed to the unstubbed substitute below).
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
        var probeTime = new FixedTimeProvider(closeInstant.AddMinutes(25));

        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(msft, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(closeLocalDate, 301m, "USD")], callInfo.ArgAt<DateOnly>(1))));

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), Substitute.For<IFxRateProvider>(), calendar: calendar, timeProvider: probeTime);

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.MarketsRun.Should().Contain(Market.Nyse);
        // The tail only — CoveredTo (2026-08-03) + 1, NOT the full 2026-07-01 earliest trade date.
        await stockProvider.Received(1).GetHistoryAsync(
            msft, new DateOnly(2026, 8, 4), closeLocalDate, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunIfDueAsync_D51_NarrowedRetry_LateProviderBar_StillRetriesTheCapDate_NotAlreadyCovered()
    {
        // Coordinator review finding: state.CoveredTo alone is not proof the cap-day bar is
        // actually on file — a run can succeed (and record CoveredTo = cap) even though the
        // provider's response didn't include that specific date's bar yet. D51's own retry
        // SELECTION already guards against this by using stored rows, not state
        // (`lastBackfilledByAsset` below) — the narrowing must use the same dual proof, or a
        // still-missing close silently stops being retried until the market's NEXT close.
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
        var closeLocalDate = new DateOnly(2026, 8, 5); // the cap

        // State claims the fetch that ran at this close already covered THROUGH the cap...
        _db.AssetPriceHistoryStates.Add(new AssetPriceHistoryState
        {
            AssetId = msft.Id, LastAttemptedAt = closeInstant.AddMinutes(5), LastSuccessAt = closeInstant.AddMinutes(5),
            LastRunSuccess = true, CoveredFrom = new DateOnly(2026, 7, 1), CoveredTo = closeLocalDate,
        });
        // ...but the provider's response for that run did not actually include the cap-day bar —
        // the newest MSFT row on file is one day short of what state claims.
        _db.PriceHistories.Add(new PriceHistory { AssetId = msft.Id, Date = closeLocalDate.AddDays(-1), Close = 299m, Currency = "USD" });

        // AAPL already holds the market's latest close — kept out of scope so it's never handed
        // to the unstubbed substitute below.
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
        var probeTime = new FixedTimeProvider(closeInstant.AddMinutes(25));

        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(msft, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(closeLocalDate, 301m, "USD")], callInfo.ArgAt<DateOnly>(1))));

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), Substitute.For<IFxRateProvider>(), calendar: calendar, timeProvider: probeTime);

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.MarketsRun.Should().Contain(Market.Nyse);
        // The cap date itself must still be requested — min(state.CoveredTo, newest stored) + 1
        // = (cap - 1) + 1 = cap, NOT state.CoveredTo + 1 (which would be one day past the cap and
        // read as "already covered", spending zero calls and stranding the close).
        await stockProvider.Received(1).GetHistoryAsync(
            msft, closeLocalDate, closeLocalDate, Arg.Any<CancellationToken>());
        result.Summary!.AssetsAlreadyCovered.Should().NotContain("MSFT");
    }

    [Fact]
    public async Task RunIfDueAsync_D51_FxNarrowedRetry_LateProviderBar_StillRetriesTheCapDate_NotAlreadyCovered()
    {
        // The FX equivalent of the test above.
        var closeInstant = new DateTimeOffset(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);
        var closeLocalDate = new DateOnly(2026, 8, 5); // Sgx's own cap — irrelevant to the FX cap below
        var calendar = CalendarWithFixedClose(Market.Sgx, closeInstant, closeLocalDate);

        // Z74's own price is already up to date — isolates this test to the FX loop alone.
        _db.PriceHistories.Add(new PriceHistory { AssetId = _z74.Id, Date = closeLocalDate, Close = 4m, Currency = "SGD" });

        // State claims USD/SGD was already asked for through 2026-08-05 (the FX cap this probe
        // time below resolves to)...
        _db.FxPairBackfillStates.Add(new FxPairBackfillState
        {
            Base = "USD", Quote = "SGD",
            LastAttemptedAt = closeInstant.AddMinutes(5), LastSuccessAt = closeInstant.AddMinutes(5), LastRunSuccess = true,
            CoveredFrom = new DateOnly(2026, 7, 20), CoveredTo = new DateOnly(2026, 8, 5),
        });
        // ...but the newest USD/SGD rate actually on file is one day short of that.
        _db.FxRates.Add(new FxRate { Date = new DateOnly(2026, 8, 4), Base = "USD", Quote = "SGD", Rate = 1.29m });

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

        // Chosen so ComputeFxCap(now) = 2026-08-05 exactly — one UTC calendar day behind `now`,
        // and comfortably past the default 15-minute FailedRunRetryDelay since closeInstant.
        var probeTime = new FixedTimeProvider(new DateTimeOffset(2026, 8, 6, 0, 10, 0, TimeSpan.Zero));

        var stockProvider = Substitute.For<IQuoteProvider>();
        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 8, 5), 1.31m)]));

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider, calendar: calendar, timeProvider: probeTime);

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.MarketsRun.Should().Contain(Market.Sgx);
        await fxProvider.Received(1).GetHistoryAsync(
            "USD", "SGD", new DateOnly(2026, 8, 5), new DateOnly(2026, 8, 5), Arg.Any<CancellationToken>());
        result.Summary!.AssetsAlreadyCovered.Should().NotContain("FX:USD/SGD");
    }

    [Fact]
    public async Task RunAsync_Manual_IgnoresCoveringState_AlwaysRequestsTheFullRange()
    {
        // The manual endpoint (RunAsync's own public entry point) must stay a full pass — narrowing
        // is reachable only via BackfillCatchUp and a D51 retryOnlyMarkets market, neither of which
        // this call uses.
        _db.AssetPriceHistoryStates.Add(new AssetPriceHistoryState
        {
            AssetId = _aapl.Id, LastAttemptedAt = _timeProvider.GetUtcNow(), LastSuccessAt = _timeProvider.GetUtcNow(),
            LastRunSuccess = true, CoveredFrom = new DateOnly(2026, 7, 20), CoveredTo = new DateOnly(2026, 7, 21),
        });
        await _db.SaveChangesAsync();

        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 200m, "USD")], callInfo.ArgAt<DateOnly>(1))));
        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]));

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider);

        await sut.RunAsync(RefreshTrigger.BackfillManual, BothMarkets, CancellationToken.None);

        await stockProvider.Received(1).GetHistoryAsync(
            _aapl, new DateOnly(2026, 7, 20), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunIfDueAsync_FirstScheduledPassSinceClose_IgnoresCoveringState_AlwaysRequestsTheFullRange()
    {
        // The first scheduled pass per close (no BackfillScheduled run yet since this close, so
        // retryOnlyMarkets is empty — see RunIfDueAsync) must also stay a full pass — this is what
        // fills history for a newly recorded back-dated transaction on an asset whose latest close
        // happens to already be on file, which a tail-only request would never revisit.
        _db.AssetPriceHistoryStates.Add(new AssetPriceHistoryState
        {
            AssetId = _aapl.Id, LastAttemptedAt = _timeProvider.GetUtcNow(), LastSuccessAt = _timeProvider.GetUtcNow(),
            LastRunSuccess = true, CoveredFrom = new DateOnly(2026, 7, 20), CoveredTo = new DateOnly(2026, 7, 21),
        });
        await _db.SaveChangesAsync();

        var stockProvider = Substitute.For<IQuoteProvider>();
        stockProvider.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(new DateOnly(2026, 7, 20), 200m, "USD")], callInfo.ArgAt<DateOnly>(1))));
        var fxProvider = Substitute.For<IFxRateProvider>();
        fxProvider.GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(FxHistoryFetchResult.Ok([new FxRatePoint(new DateOnly(2026, 7, 20), 1.29m)]));

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), fxProvider); // AlwaysClosedCalendar, no prior runs

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.MarketsRun.Should().Contain(Market.Nyse);
        await stockProvider.Received(1).GetHistoryAsync(
            _aapl, new DateOnly(2026, 7, 20), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCatchUpAsync_NarrowedFromLandsBeyondCap_IsReportedAsAlreadyCovered_NotDeferred()
    {
        // The planner and this method compute the settled cap/read coverage state at slightly
        // different instants (see the class remarks) — simulated here directly: state.CoveredTo
        // AND the newest actually-stored PriceHistory row both already reach the cap
        // AlwaysClosedCalendar would compute, so the narrowed `from` lands one day beyond it. This
        // must read as "already covered", never as "not yet settled"
        // (AssetsSkippedTodayNotClosed), and must spend zero provider calls. Both sources must
        // agree — see RunIfDueAsync_D51_NarrowedRetry_LateProviderBar_... below for the case where
        // only state reaches the cap but the stored row does not, which must NOT land here.
        _db.AssetPriceHistoryStates.Add(new AssetPriceHistoryState
        {
            AssetId = _aapl.Id, LastAttemptedAt = _timeProvider.GetUtcNow(), LastSuccessAt = _timeProvider.GetUtcNow(),
            LastRunSuccess = true, CoveredFrom = new DateOnly(2026, 7, 20),
            CoveredTo = new DateOnly(2026, 7, 25), // == AlwaysClosedCalendar's cap for this fixed "now"
        });
        _db.PriceHistories.Add(new PriceHistory { AssetId = _aapl.Id, Date = new DateOnly(2026, 7, 25), Close = 205m, Currency = "USD" });
        await _db.SaveChangesAsync();

        var stockProvider = Substitute.For<IQuoteProvider>();

        var sut = CreateSut(RouterAlwaysReturning(stockProvider), Substitute.For<IFxRateProvider>());

        var summary = await sut.RunCatchUpAsync([Market.Nyse], new HashSet<int> { _aapl.Id }, new HashSet<string>(), CancellationToken.None);

        summary.AssetsAlreadyCovered.Should().Contain("AAPL");
        summary.AssetsSkippedTodayNotClosed.Should().NotContain("AAPL");
        summary.AssetsFailed.Should().BeEmpty();
        summary.AssetsProcessed.Should().NotContain("AAPL");
        await stockProvider.DidNotReceive().GetHistoryAsync(
            _aapl, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCatchUpAsync_FxNarrowedFromLandsBeyondFxCap_IsReportedAsAlreadyCovered_NotDeferred()
    {
        _db.FxPairBackfillStates.Add(new FxPairBackfillState
        {
            Base = "USD", Quote = "SGD",
            LastAttemptedAt = _timeProvider.GetUtcNow(), LastSuccessAt = _timeProvider.GetUtcNow(), LastRunSuccess = true,
            CoveredFrom = new DateOnly(2026, 7, 20),
            CoveredTo = new DateOnly(2026, 7, 25), // == ComputeFxCap for this fixed "now" (one UTC day behind)
        });
        // The newest stored FxRate must ALSO reach the cap — state alone is not enough (see the
        // class remarks and the late-provider-bar regression tests below).
        _db.FxRates.Add(new FxRate { Date = new DateOnly(2026, 7, 25), Base = "USD", Quote = "SGD", Rate = 1.31m });
        await _db.SaveChangesAsync();

        var fxProvider = Substitute.For<IFxRateProvider>();

        var sut = CreateSut(RouterAlwaysReturning(Substitute.For<IQuoteProvider>()), fxProvider);

        var summary = await sut.RunCatchUpAsync([Market.Sgx], new HashSet<int>(), new HashSet<string> { "SGD" }, CancellationToken.None);

        summary.AssetsAlreadyCovered.Should().Contain("FX:USD/SGD");
        summary.AssetsSkippedTodayNotClosed.Should().NotContain("FX:USD/SGD");
        summary.AssetsFailed.Should().BeEmpty();
        await fxProvider.DidNotReceive().GetHistoryAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCatchUpAsync_NarrowedSuccess_UnionsCoverage_AndThePlannerDoesNotRePlanTheAsset()
    {
        // THE regression the coverage-merge trap guards against (see the class remarks and
        // RecordPriceHistoryState's 2026-09-15 doc comment). Confirmed to FAIL against the
        // pre-fix (straight-overwrite) code before the fix was written: see this session's report.
        var closeInstant1 = new DateTimeOffset(2026, 8, 3, 21, 0, 0, TimeSpan.Zero);
        var closeLocalDate1 = new DateOnly(2026, 8, 3);
        var calendar1 = CalendarWithFixedClose(Market.Nyse, closeInstant1, closeLocalDate1);
        var stockProvider1 = Substitute.For<IQuoteProvider>();
        stockProvider1.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(closeLocalDate1, 200m, "USD")], callInfo.ArgAt<DateOnly>(1))));

        var sut1 = CreateSut(RouterAlwaysReturning(stockProvider1), Substitute.For<IFxRateProvider>(), calendar: calendar1);
        // Full-range manual pass establishes CoveredFrom = AAPL's earliest trade date (2026-07-20),
        // CoveredTo = closeLocalDate1 (2026-08-03).
        await sut1.RunAsync(RefreshTrigger.BackfillManual, [Market.Nyse], CancellationToken.None);

        var afterFirstRun = await _db.AssetPriceHistoryStates.SingleAsync(s => s.AssetId == _aapl.Id);
        afterFirstRun.CoveredFrom.Should().Be(new DateOnly(2026, 7, 20));
        afterFirstRun.CoveredTo.Should().Be(closeLocalDate1);

        // A later catch-up narrows to the tail and succeeds.
        var closeInstant2 = new DateTimeOffset(2026, 8, 5, 21, 0, 0, TimeSpan.Zero);
        var closeLocalDate2 = new DateOnly(2026, 8, 5);
        var calendar2 = CalendarWithFixedClose(Market.Nyse, closeInstant2, closeLocalDate2);
        var stockProvider2 = Substitute.For<IQuoteProvider>();
        stockProvider2.GetHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(HistoryFetchResult.Ok(
                [new PriceHistoryPoint(closeLocalDate2, 205m, "USD")], callInfo.ArgAt<DateOnly>(1))));

        var sut2 = CreateSut(RouterAlwaysReturning(stockProvider2), Substitute.For<IFxRateProvider>(), calendar: calendar2);

        await sut2.RunCatchUpAsync([Market.Nyse], new HashSet<int> { _aapl.Id }, new HashSet<string>(), CancellationToken.None);

        await stockProvider2.Received(1).GetHistoryAsync(
            _aapl, closeLocalDate1.AddDays(1), closeLocalDate2, Arg.Any<CancellationToken>());

        var afterCatchUp = await _db.AssetPriceHistoryStates.SingleAsync(s => s.AssetId == _aapl.Id);
        afterCatchUp.CoveredFrom.Should().Be(
            new DateOnly(2026, 7, 20), "the union must keep the ORIGINAL lower bound, not the narrowed request's own `from`");
        afterCatchUp.CoveredTo.Should().Be(closeLocalDate2, "the union must advance to the new upper bound");

        // The trap: a follow-up planner call must NOT read the (correctly-unioned) state as
        // missing and re-plan a full-range fetch for this asset.
        var plannerCalendar = Substitute.For<IMarketCalendar>();
        plannerCalendar.LastSessionCloseAt(Arg.Any<Market>(), Arg.Any<DateTimeOffset>())
            .Returns(callInfo => callInfo.ArgAt<DateTimeOffset>(1));
        plannerCalendar.LocalDateOn(Market.Nyse, Arg.Any<DateTimeOffset>()).Returns(closeLocalDate2);
        plannerCalendar.LocalDateOn(Market.Sgx, Arg.Any<DateTimeOffset>()).Returns(closeLocalDate2);

        var planner = new RefreshCatchUpService(
            _db,
            plannerCalendar,
            _timeProvider,
            Options.Create(new PriceBackfillOptions()),
            Options.Create(new DividendBackfillOptions()));

        var plan = await planner.PlanAsync(CancellationToken.None);

        plan.PriceHistory.ToFetch.Should().NotContain(t => t.Symbol == "AAPL");
        plan.PriceHistory.RetryPendingSymbols.Should().NotContain("AAPL");
        plan.PriceHistory.NotYetAvailableSymbols.Should().NotContain("AAPL");
    }
}
