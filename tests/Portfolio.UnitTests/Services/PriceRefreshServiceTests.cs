using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
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
/// Exercises <see cref="PriceRefreshService"/> end to end against an in-memory database and faked
/// providers/calendar/broadcaster — no network access, no real clock. Every provider and the
/// calendar are NSubstitute fakes, per the "unit tests must not hit the network or spend API
/// credits" constraint.
/// </summary>
public sealed class PriceRefreshServiceTests : IDisposable
{
    private readonly PortfolioDbContext _db;
    private readonly MutableTimeProvider _timeProvider;
    private readonly IMarketCalendar _calendar;
    private readonly IPriceUpdateBroadcaster _broadcaster;
    private readonly PriceRefreshStatusStore _statusStore;
    private readonly PriceRefreshOptions _options;

    private readonly Asset _aapl;
    private readonly Asset _z74;
    private readonly Asset _eth;

    public PriceRefreshServiceTests()
    {
        var dbOptions = new DbContextOptionsBuilder<PortfolioDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new PortfolioDbContext(dbOptions);

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
        _eth = new Asset
        {
            Id = 4,
            Symbol = "ETH",
            Name = "Ethereum",
            AssetClass = AssetClass.Crypto,
            Currency = "USD",
            QuoteProviderKind = QuoteProviderKind.CoinGecko,
            ProviderCoinId = "ethereum",
        };
        _db.Assets.AddRange(_aapl, _z74, _eth);
        _db.SaveChanges();

        _timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 7, 29, 15, 0, 0, TimeSpan.Zero)); // Wed 15:00 UTC
        _calendar = Substitute.For<IMarketCalendar>();
        _broadcaster = Substitute.For<IPriceUpdateBroadcaster>();
        _statusStore = new PriceRefreshStatusStore(_db);
        _options = new PriceRefreshOptions();
    }

    public void Dispose() => _db.Dispose();

    private PriceRefreshService CreateSut(IQuoteProviderRouter router, ITwelveDataCreditThrottle? creditThrottle = null)
    {
        var throttle = creditThrottle ?? AlwaysFullBudgetThrottle();
        return new(
            _db,
            router,
            _calendar,
            _broadcaster,
            _statusStore,
            new PriceRefreshStatusEnricher(_db, throttle, Options.Create(_options)),
            throttle,
            Substitute.For<IServiceScopeFactory>(), // unused unless NeedsDetachedTwelveDataSweepAsync is true - see the dedicated detach test below, which builds a real container instead
            new ManualRefreshInFlightGate(),
            _timeProvider,
            Options.Create(_options),
            NullLogger<PriceRefreshService>.Instance);
    }

    /// <summary>A credit-throttle fake reporting a full, untouched daily budget — the default for
    /// tests that are not themselves about credit pacing or the derived cadence.</summary>
    private static ITwelveDataCreditThrottle AlwaysFullBudgetThrottle()
    {
        var throttle = Substitute.For<ITwelveDataCreditThrottle>();
        throttle.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(new TwelveDataCreditStatus(0, 800, 800));
        throttle.TryAcquireAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        return throttle;
    }

    private static IQuoteProvider FakeProvider(QuoteProviderKind kind) =>
        Substitute.For<IQuoteProvider>().Also(p => p.Kind.Returns(kind));

    private static IQuoteProviderRouter RouterFor(params (Asset Asset, IQuoteProvider Provider)[] map)
    {
        var router = Substitute.For<IQuoteProviderRouter>();
        foreach (var (asset, provider) in map)
        {
            router.GetProvider(asset).Returns(provider);
        }

        return router;
    }

    [Fact]
    public async Task RefreshDueAsync_RefreshesCrypto_RegardlessOfMarketCalendar()
    {
        // Calendar is never even consulted for crypto — no IsOpen setup at all — and both
        // markets default to "not configured" (NSubstitute returns false), which would skip a
        // stock group but must not skip crypto.
        var coinGecko = FakeProvider(QuoteProviderKind.CoinGecko);
        coinGecko.GetQuotesAsync(Arg.Is<IReadOnlyCollection<Asset>>(a => a != null && a.Contains(_eth)), Arg.Any<CancellationToken>())
            .Returns([new QuoteFetchResult(_eth.Id, true, 2500.12m, "USD", _timeProvider.Now, null)]);

        var router = RouterFor((_eth, coinGecko));
        var sut = CreateSut(router);

        // Remove the stock assets so this test is purely about crypto.
        _db.Assets.RemoveRange(_aapl, _z74);
        await _db.SaveChangesAsync();

        var result = await sut.RefreshDueAsync(CancellationToken.None);

        result.Outcome.Should().Be(PriceRefreshOutcome.Completed);
        result.Sources.Should().ContainSingle(s => s.Source == QuoteProviderKind.CoinGecko && s.Success);
        result.TotalSymbolsRefreshed.Should().Be(1);

        var quote = await _db.PriceQuotes.SingleAsync(q => q.AssetId == _eth.Id);
        quote.Price.Should().Be(2500.12m);

        // The calendar is still consulted once per market when building the post-cycle status
        // snapshot (needed for "is NYSE/SGX open" on the status endpoint), but never as a gate on
        // whether to fetch crypto — that is what result.Sources containing only CoinGecko proves.
    }

    [Fact]
    public async Task RefreshDueAsync_SkipsStockGroup_WhenItsMarketIsClosed()
    {
        _calendar.IsOpen(Market.Nyse, Arg.Any<DateTimeOffset>()).Returns(false);

        var twelveData = FakeProvider(QuoteProviderKind.TwelveData);
        var router = RouterFor((_aapl, twelveData));

        _db.Assets.RemoveRange(_z74, _eth);
        await _db.SaveChangesAsync();

        var sut = CreateSut(router);
        var result = await sut.RefreshDueAsync(CancellationToken.None);

        result.Outcome.Should().Be(PriceRefreshOutcome.NothingDue);
        await twelveData.DidNotReceive().GetQuotesAsync(Arg.Any<IReadOnlyCollection<Asset>>(), Arg.Any<CancellationToken>());
        (await _db.RefreshRuns.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RefreshDueAsync_RefreshesStockGroup_WhenItsMarketIsOpen()
    {
        _calendar.IsOpen(Market.Nyse, Arg.Any<DateTimeOffset>()).Returns(true);

        var twelveData = FakeProvider(QuoteProviderKind.TwelveData);
        twelveData.GetQuotesAsync(Arg.Any<IReadOnlyCollection<Asset>>(), Arg.Any<CancellationToken>())
            .Returns([new QuoteFetchResult(_aapl.Id, true, 333.02m, "USD", _timeProvider.Now, null)]);

        var router = RouterFor((_aapl, twelveData));

        _db.Assets.RemoveRange(_z74, _eth);
        await _db.SaveChangesAsync();

        var sut = CreateSut(router);
        var result = await sut.RefreshDueAsync(CancellationToken.None);

        result.Outcome.Should().Be(PriceRefreshOutcome.Completed);
        result.Sources.Should().ContainSingle(s => s.Source == QuoteProviderKind.TwelveData && s.Success && s.SymbolsRefreshed == 1);

        var run = await _db.RefreshRuns.SingleAsync();
        run.Trigger.Should().Be(RefreshTrigger.Scheduled);
        run.AssetClass.Should().Be(AssetClass.Stock);
        run.SymbolsRefreshed.Should().Be(1);
        run.Success.Should().BeTrue();

        await _broadcaster.Received(1).BroadcastQuoteUpdatedAsync(
            Arg.Is<QuoteUpdateNotification>(n => n != null && n.AssetId == _aapl.Id && n.Price == 333.02m),
            Arg.Any<CancellationToken>());
        await _broadcaster.Received(1).BroadcastRefreshStatusAsync(Arg.Any<PriceRefreshStatus>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshDueAsync_BroadcastsAnEnrichedStatus_WithTheDerivedCadenceAndCreditFieldsPopulated()
    {
        // Regression coverage for the transport asymmetry PriceRefreshStatusEnricher fixed: this
        // status used to reach the broadcaster bare (all three of these fields null), because the
        // enrichment lived only inline in the GET /api/prices/status handler. See
        // PricesHubTests for the matching coverage on the hub-connect transport.
        _calendar.IsOpen(Market.Nyse, Arg.Any<DateTimeOffset>()).Returns(true);

        var twelveData = FakeProvider(QuoteProviderKind.TwelveData);
        twelveData.GetQuotesAsync(Arg.Any<IReadOnlyCollection<Asset>>(), Arg.Any<CancellationToken>())
            .Returns([new QuoteFetchResult(_aapl.Id, true, 333.02m, "USD", _timeProvider.Now, null)]);

        var router = RouterFor((_aapl, twelveData));
        _db.Assets.RemoveRange(_z74, _eth);
        await _db.SaveChangesAsync();

        var throttle = Substitute.For<ITwelveDataCreditThrottle>();
        throttle.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(new TwelveDataCreditStatus(50, 800, 750));
        throttle.TryAcquireAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);

        var sut = CreateSut(router, throttle);
        var result = await sut.RefreshDueAsync(CancellationToken.None);

        result.Outcome.Should().Be(PriceRefreshOutcome.Completed);

        await _broadcaster.Received(1).BroadcastRefreshStatusAsync(
            Arg.Is<PriceRefreshStatus>(s =>
                s != null &&
                s.CreditsUsedToday == 50 &&
                s.CreditBudget == 800 &&
                s.EffectiveTwelveDataIntervalSeconds != null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshDueAsync_ProviderThrows_DegradesToLastStoredQuote_AndKeepsOtherGroupsRunning()
    {
        _calendar.IsOpen(Market.Nyse, Arg.Any<DateTimeOffset>()).Returns(true);

        // AAPL already has a stored quote from a previous, successful cycle.
        _db.PriceQuotes.Add(new PriceQuote { AssetId = _aapl.Id, Price = 300m, Currency = "USD", AsOf = _timeProvider.Now.AddHours(-1) });
        await _db.SaveChangesAsync();

        var twelveData = FakeProvider(QuoteProviderKind.TwelveData);
        twelveData.GetQuotesAsync(Arg.Any<IReadOnlyCollection<Asset>>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<QuoteFetchResult>>>(_ => throw new HttpRequestException("Twelve Data is down"));

        var coinGecko = FakeProvider(QuoteProviderKind.CoinGecko);
        coinGecko.GetQuotesAsync(Arg.Any<IReadOnlyCollection<Asset>>(), Arg.Any<CancellationToken>())
            .Returns([new QuoteFetchResult(_eth.Id, true, 2500m, "USD", _timeProvider.Now, null)]);

        var router = RouterFor((_aapl, twelveData), (_eth, coinGecko));

        _db.Assets.Remove(_z74);
        await _db.SaveChangesAsync();

        var sut = CreateSut(router);
        var result = await sut.RefreshDueAsync(CancellationToken.None);

        result.Outcome.Should().Be(PriceRefreshOutcome.Completed);
        result.Sources.Should().Contain(s => s.Source == QuoteProviderKind.TwelveData && !s.Success && s.Error != null);
        result.Sources.Should().Contain(s => s.Source == QuoteProviderKind.CoinGecko && s.Success);

        // AAPL's last stored quote must be untouched — degrade, don't blank it out.
        var aaplQuote = await _db.PriceQuotes.SingleAsync(q => q.AssetId == _aapl.Id);
        aaplQuote.Price.Should().Be(300m);

        // ETH still got its fresh quote despite AAPL's provider throwing.
        var ethQuote = await _db.PriceQuotes.SingleAsync(q => q.AssetId == _eth.Id);
        ethQuote.Price.Should().Be(2500m);

        var run = await _db.RefreshRuns.SingleAsync();
        run.Success.Should().BeFalse(); // one of the two sources failed
        run.AssetClass.Should().BeNull(); // mixed: one stock source, one crypto source
    }

    [Fact]
    public async Task RefreshDueAsync_NothingDue_DoesNotRecordARefreshRun()
    {
        _calendar.IsOpen(Market.Nyse, Arg.Any<DateTimeOffset>()).Returns(true);

        var twelveData = FakeProvider(QuoteProviderKind.TwelveData);
        twelveData.GetQuotesAsync(Arg.Any<IReadOnlyCollection<Asset>>(), Arg.Any<CancellationToken>())
            .Returns([new QuoteFetchResult(_aapl.Id, true, 300m, "USD", _timeProvider.Now, null)]);

        var coinGecko = FakeProvider(QuoteProviderKind.CoinGecko);
        coinGecko.GetQuotesAsync(Arg.Any<IReadOnlyCollection<Asset>>(), Arg.Any<CancellationToken>())
            .Returns([new QuoteFetchResult(_eth.Id, true, 2500m, "USD", _timeProvider.Now, null)]);

        var router = RouterFor((_aapl, twelveData), (_eth, coinGecko));
        _db.Assets.Remove(_z74);
        await _db.SaveChangesAsync();

        var sut = CreateSut(router);

        var first = await sut.RefreshDueAsync(CancellationToken.None);
        first.Outcome.Should().Be(PriceRefreshOutcome.Completed);

        // Same instant, immediately again: neither source's interval has elapsed yet.
        var second = await sut.RefreshDueAsync(CancellationToken.None);
        second.Outcome.Should().Be(PriceRefreshOutcome.NothingDue);

        (await _db.RefreshRuns.CountAsync()).Should().Be(1); // only the first cycle wrote one
        await twelveData.Received(1).GetQuotesAsync(Arg.Any<IReadOnlyCollection<Asset>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshDueAsync_BecomesDueAgain_OnlyAfterItsIntervalElapses()
    {
        var coinGecko = FakeProvider(QuoteProviderKind.CoinGecko);
        coinGecko.GetQuotesAsync(Arg.Any<IReadOnlyCollection<Asset>>(), Arg.Any<CancellationToken>())
            .Returns([new QuoteFetchResult(_eth.Id, true, 2500m, "USD", _timeProvider.Now, null)]);

        var router = RouterFor((_eth, coinGecko));
        _db.Assets.RemoveRange(_aapl, _z74);
        await _db.SaveChangesAsync();

        var sut = CreateSut(router);

        (await sut.RefreshDueAsync(CancellationToken.None)).Outcome.Should().Be(PriceRefreshOutcome.Completed);

        // Just under the crypto interval later: still not due.
        _timeProvider.Now += _options.CryptoInterval - TimeSpan.FromSeconds(1);
        (await sut.RefreshDueAsync(CancellationToken.None)).Outcome.Should().Be(PriceRefreshOutcome.NothingDue);

        // Past the interval: due again.
        _timeProvider.Now += TimeSpan.FromSeconds(2);
        (await sut.RefreshDueAsync(CancellationToken.None)).Outcome.Should().Be(PriceRefreshOutcome.Completed);

        await coinGecko.Received(2).GetQuotesAsync(Arg.Any<IReadOnlyCollection<Asset>>(), Arg.Any<CancellationToken>());

        // Still exactly one PriceQuote row for ETH — the second cycle updated it, not duplicated it.
        (await _db.PriceQuotes.CountAsync(q => q.AssetId == _eth.Id)).Should().Be(1);
    }

    [Fact]
    public async Task RefreshNowAsync_ReturnsCooldownActive_WithinThirtySecondsOfThePreviousManualRun()
    {
        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.Manual,
            StartedAt = _timeProvider.Now - TimeSpan.FromSeconds(10),
            CompletedAt = _timeProvider.Now - TimeSpan.FromSeconds(9),
            Success = true,
            SymbolsRefreshed = 1,
        });
        await _db.SaveChangesAsync();

        var coinGecko = FakeProvider(QuoteProviderKind.CoinGecko);
        var router = RouterFor((_eth, coinGecko));
        _db.Assets.RemoveRange(_aapl, _z74);
        await _db.SaveChangesAsync();

        var sut = CreateSut(router);
        var result = await sut.RefreshNowAsync(CancellationToken.None);

        result.Outcome.Should().Be(PriceRefreshOutcome.CooldownActive);
        result.CooldownSecondsRemaining.Should().Be(20);
        await coinGecko.DidNotReceive().GetQuotesAsync(Arg.Any<IReadOnlyCollection<Asset>>(), Arg.Any<CancellationToken>());
        (await _db.RefreshRuns.CountAsync()).Should().Be(1); // no new run recorded
    }

    [Fact]
    public async Task RefreshDueAsync_IsNeverBlockedByTheManualCooldown()
    {
        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.Manual,
            StartedAt = _timeProvider.Now - TimeSpan.FromSeconds(1),
            CompletedAt = _timeProvider.Now,
            Success = true,
            SymbolsRefreshed = 1,
        });
        await _db.SaveChangesAsync();

        var coinGecko = FakeProvider(QuoteProviderKind.CoinGecko);
        coinGecko.GetQuotesAsync(Arg.Any<IReadOnlyCollection<Asset>>(), Arg.Any<CancellationToken>())
            .Returns([new QuoteFetchResult(_eth.Id, true, 2500m, "USD", _timeProvider.Now, null)]);

        var router = RouterFor((_eth, coinGecko));
        _db.Assets.RemoveRange(_aapl, _z74);
        await _db.SaveChangesAsync();

        var sut = CreateSut(router);
        var result = await sut.RefreshDueAsync(CancellationToken.None);

        result.Outcome.Should().Be(PriceRefreshOutcome.Completed);
    }

    [Fact]
    public async Task RefreshNowAsync_StillSkipsAClosedEquityMarket_ButAlwaysRefreshesCrypto()
    {
        _calendar.IsOpen(Market.Nyse, Arg.Any<DateTimeOffset>()).Returns(false);

        var twelveData = FakeProvider(QuoteProviderKind.TwelveData);
        var coinGecko = FakeProvider(QuoteProviderKind.CoinGecko);
        coinGecko.GetQuotesAsync(Arg.Any<IReadOnlyCollection<Asset>>(), Arg.Any<CancellationToken>())
            .Returns([new QuoteFetchResult(_eth.Id, true, 2500m, "USD", _timeProvider.Now, null)]);

        var router = RouterFor((_aapl, twelveData), (_eth, coinGecko));
        _db.Assets.Remove(_z74);
        await _db.SaveChangesAsync();

        var sut = CreateSut(router);
        var result = await sut.RefreshNowAsync(CancellationToken.None);

        result.Outcome.Should().Be(PriceRefreshOutcome.Completed);
        result.Sources.Should().ContainSingle(s => s.Source == QuoteProviderKind.CoinGecko);
        result.Sources.Should().NotContain(s => s.Source == QuoteProviderKind.TwelveData);
        await twelveData.DidNotReceive().GetQuotesAsync(Arg.Any<IReadOnlyCollection<Asset>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshNowAsync_EngagesTheCooldown_EvenWhenEverySourceWasGated()
    {
        // The one arrangement where the cooldown used to fail open: no crypto to refresh
        // unconditionally, and every equity market closed. Every group is gated, so the cycle
        // does no work — but the attempt must still be recorded, or the cooldown has nothing to
        // measure from and the endpoint can be hammered.
        _calendar.IsOpen(Arg.Any<Market>(), Arg.Any<DateTimeOffset>()).Returns(false);

        var twelveData = FakeProvider(QuoteProviderKind.TwelveData);
        var yahoo = FakeProvider(QuoteProviderKind.Yahoo);
        var router = RouterFor((_aapl, twelveData), (_z74, yahoo));
        _db.Assets.Remove(_eth);
        await _db.SaveChangesAsync();

        var sut = CreateSut(router);

        var first = await sut.RefreshNowAsync(CancellationToken.None);
        first.Outcome.Should().Be(PriceRefreshOutcome.NothingDue);

        // The attempt is on record even though nothing was fetched.
        (await _db.RefreshRuns.CountAsync(r => r.Trigger == RefreshTrigger.Manual)).Should().Be(1);

        var second = await sut.RefreshNowAsync(CancellationToken.None);
        second.Outcome.Should().Be(PriceRefreshOutcome.CooldownActive);
        second.CooldownSecondsRemaining.Should().BePositive();

        // Still no provider touched, and the blocked attempt added no second row.
        await twelveData.DidNotReceive().GetQuotesAsync(Arg.Any<IReadOnlyCollection<Asset>>(), Arg.Any<CancellationToken>());
        await yahoo.DidNotReceive().GetQuotesAsync(Arg.Any<IReadOnlyCollection<Asset>>(), Arg.Any<CancellationToken>());
        (await _db.RefreshRuns.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RefreshDueAsync_EverySymbolInABatchFails_ReportsTheSourceAsUnsuccessful_AndDoesNotAdvanceLastSuccessAt()
    {
        // Reproduces the live D-defect: CoinGecko 401ing for every coin in the batch never throws
        // (it returns per-asset QuoteFetchResult failures, same as Twelve Data's nested-error
        // shape), so before the fix `Success` stayed hardcoded true and the status snapshot said
        // "lastRunSuccess: true" / advanced "lastSuccessAt" on a cycle that fetched ZERO symbols.
        var coinGecko = FakeProvider(QuoteProviderKind.CoinGecko);
        coinGecko.GetQuotesAsync(Arg.Any<IReadOnlyCollection<Asset>>(), Arg.Any<CancellationToken>())
            .Returns([new QuoteFetchResult(_eth.Id, false, null, null, null, "CoinGecko returned HTTP 401.")]);

        var router = RouterFor((_eth, coinGecko));
        _db.Assets.RemoveRange(_aapl, _z74);
        await _db.SaveChangesAsync();

        var sut = CreateSut(router);
        var result = await sut.RefreshDueAsync(CancellationToken.None);

        result.Outcome.Should().Be(PriceRefreshOutcome.Completed);
        result.Sources.Should().ContainSingle(s =>
            s.Source == QuoteProviderKind.CoinGecko &&
            !s.Success &&
            s.SymbolsRefreshed == 0 &&
            s.Error == "CoinGecko returned HTTP 401.");

        var run = await _db.RefreshRuns.SingleAsync();
        run.Success.Should().BeFalse();

        var status = await _statusStore.GetSnapshotAsync(nyseOpen: false, sgxOpen: false, CancellationToken.None);
        var coinGeckoStatus = status.Sources.Single(s => s.Source == QuoteProviderKind.CoinGecko);
        coinGeckoStatus.LastRunSuccess.Should().BeFalse();
        coinGeckoStatus.LastSuccessAt.Should().BeNull(); // must NOT advance on a cycle that fetched nothing
        coinGeckoStatus.LastAttemptedAt.Should().Be(_timeProvider.Now); // the attempt itself IS recorded
        status.LastRefreshedAt.Should().BeNull();
    }

    [Fact]
    public async Task RefreshDueAsync_SomeSymbolsSucceedAndSomeFailInABatch_StillReportsTheSourceAsSuccessful()
    {
        // The deliberate partial-success rule: Twelve Data/Yahoo nest a per-symbol error inside an
        // otherwise-successful batch, and one bad symbol must not flip the whole provider to
        // "failed" when the rest of the batch genuinely refreshed. This is NOT the same bug as
        // the all-fail case above — both are pinned so neither regresses into the other.
        _calendar.IsOpen(Market.Sgx, Arg.Any<DateTimeOffset>()).Returns(true);

        var yahoo = FakeProvider(QuoteProviderKind.Yahoo);
        var d05 = new Asset
        {
            Id = 5,
            Symbol = "D05",
            Name = "DBS",
            AssetClass = AssetClass.Stock,
            Currency = "SGD",
            QuoteProviderKind = QuoteProviderKind.Yahoo,
            ProviderSymbol = "D05.SI",
        };
        _db.Assets.Add(d05);
        await _db.SaveChangesAsync();

        // A two-symbol Yahoo batch where one asset succeeds and a sibling in the same call fails.
        yahoo.GetQuotesAsync(Arg.Any<IReadOnlyCollection<Asset>>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                new QuoteFetchResult(_z74.Id, true, 4.39m, "SGD", _timeProvider.Now, null),
                new QuoteFetchResult(d05.Id, false, null, null, null, "D05.SI not found"),
            ]);

        var router = RouterFor((_z74, yahoo), (d05, yahoo));
        _db.Assets.RemoveRange(_aapl, _eth);
        await _db.SaveChangesAsync();

        var sut = CreateSut(router);
        var result = await sut.RefreshDueAsync(CancellationToken.None);

        result.Sources.Should().ContainSingle(s =>
            s.Source == QuoteProviderKind.Yahoo &&
            s.Success &&
            s.SymbolsRefreshed == 1 &&
            s.Error == "D05.SI not found");

        var status = await _statusStore.GetSnapshotAsync(nyseOpen: false, sgxOpen: true, CancellationToken.None);
        var yahooStatus = status.Sources.Single(s => s.Source == QuoteProviderKind.Yahoo);
        yahooStatus.LastRunSuccess.Should().BeTrue();
        yahooStatus.LastSuccessAt.Should().Be(_timeProvider.Now);
    }

    [Fact]
    public async Task RefreshDueAsync_GatedByClosedMarkets_StillRecordsNoRefreshRun()
    {
        // The counterpart to the test above: the same all-gated situation on the *scheduled* path
        // must stay silent. The background loop polls every 30 seconds, so recording a row here
        // would write thousands of no-op rows a day.
        _calendar.IsOpen(Arg.Any<Market>(), Arg.Any<DateTimeOffset>()).Returns(false);

        var twelveData = FakeProvider(QuoteProviderKind.TwelveData);
        var yahoo = FakeProvider(QuoteProviderKind.Yahoo);
        var router = RouterFor((_aapl, twelveData), (_z74, yahoo));
        _db.Assets.Remove(_eth);
        await _db.SaveChangesAsync();

        var sut = CreateSut(router);

        var result = await sut.RefreshDueAsync(CancellationToken.None);

        result.Outcome.Should().Be(PriceRefreshOutcome.NothingDue);
        (await _db.RefreshRuns.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RefreshDueAsync_TwelveDataGroup_UsesTheDerivedCadence_NotTheFixedFiveMinuteFloor()
    {
        // D38/D37: 21 Twelve Data symbols (the live measured count) — NextIntervalAsync must
        // consult TwelveDataCadenceCalculator for this source instead of unconditionally returning
        // the fixed 5-minute PriceRefreshOptions.StockOpenInterval, or the cadence silently
        // under-paces the moment the portfolio grows past today's symbol count.
        _calendar.IsOpen(Market.Nyse, Arg.Any<DateTimeOffset>()).Returns(true);

        var extraAssets = Enumerable.Range(0, 20)
            .Select(i => new Asset
            {
                Id = 1000 + i,
                Symbol = $"SYM{i}",
                Name = $"Symbol {i}",
                AssetClass = AssetClass.Stock,
                Currency = "USD",
                QuoteProviderKind = QuoteProviderKind.TwelveData,
                ProviderSymbol = $"SYM{i}",
            })
            .ToList();
        _db.Assets.AddRange(extraAssets);
        _db.Assets.RemoveRange(_z74, _eth);
        await _db.SaveChangesAsync();

        var twelveData = FakeProvider(QuoteProviderKind.TwelveData);
        twelveData.GetQuotesAsync(Arg.Any<IReadOnlyCollection<Asset>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult<IReadOnlyList<QuoteFetchResult>>(
                callInfo.Arg<IReadOnlyCollection<Asset>>()!
                    .Select(a => new QuoteFetchResult(a.Id, true, 100m, "USD", _timeProvider.Now, null))
                    .ToList()));

        var router = Substitute.For<IQuoteProviderRouter>();
        router.GetProvider(Arg.Any<Asset>()).Returns(twelveData);

        var throttle = AlwaysFullBudgetThrottle(); // 800/800 remaining — a fresh day
        var sut = CreateSut(router, throttle);

        var result = await sut.RefreshDueAsync(CancellationToken.None);
        result.Outcome.Should().Be(PriceRefreshOutcome.Completed);

        var expectedInterval = TwelveDataCadenceCalculator.DeriveStockOpenInterval(
            21, 800, TimeSpan.FromMinutes(TwelveDataCreditPolicy.NyseSessionMinutes), _options.StockOpenInterval);
        expectedInterval.Should().BeGreaterThan(_options.StockOpenInterval, "this scenario (N=21) must really derive something wider than the floor");

        var status = await _statusStore.GetSnapshotAsync(nyseOpen: true, sgxOpen: false, CancellationToken.None);
        var twelveDataStatus = status.Sources.Single(s => s.Source == QuoteProviderKind.TwelveData);
        twelveDataStatus.NextDueAt.Should().Be(_timeProvider.Now + expectedInterval);
    }

    /// <summary>
    /// D38: proves <c>RefreshNowAsync</c> decides between the synchronous path (unaffected, every
    /// other test in this class) and a detached background sweep purely from the live Twelve Data
    /// symbol count — using a REAL DI container (not NSubstitute) because the detach path resolves
    /// a second <see cref="PriceRefreshService"/> instance from its own scope via
    /// <see cref="IServiceScopeFactory"/>, which a fake scope factory cannot stand in for.
    /// </summary>
    [Fact]
    public async Task RefreshNowAsync_MoreThanEightTwelveDataSymbols_ReturnsQueuedImmediately_AndCompletesTheSweepInTheBackground()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var seedDb = new PortfolioDbContext(
            new DbContextOptionsBuilder<PortfolioDbContext>().UseInMemoryDatabase(dbName).Options);

        var assets = Enumerable.Range(0, 9) // > PerMinuteCreditLimit (8)
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
        seedDb.Assets.AddRange(assets);
        await seedDb.SaveChangesAsync();

        var calendar = Substitute.For<IMarketCalendar>();
        calendar.IsOpen(Market.Nyse, Arg.Any<DateTimeOffset>()).Returns(true);

        var twelveData = FakeProvider(QuoteProviderKind.TwelveData);
        twelveData.GetQuotesAsync(Arg.Any<IReadOnlyCollection<Asset>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult<IReadOnlyList<QuoteFetchResult>>(
                callInfo.Arg<IReadOnlyCollection<Asset>>()!
                    .Select(a => new QuoteFetchResult(a.Id, true, 100m, "USD", _timeProvider.Now, null))
                    .ToList()));
        var router = Substitute.For<IQuoteProviderRouter>();
        router.GetProvider(Arg.Any<Asset>()).Returns(twelveData);

        var services = new ServiceCollection();
        services.AddDbContext<PortfolioDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<IPortfolioDbContext>(sp => sp.GetRequiredService<PortfolioDbContext>());
        services.AddScoped(sp => new PriceRefreshStatusStore(sp.GetRequiredService<IPortfolioDbContext>()));
        services.AddScoped<PriceRefreshStatusEnricher>();
        services.AddSingleton(calendar);
        services.AddSingleton(router);
        services.AddSingleton(Substitute.For<IPriceUpdateBroadcaster>());
        services.AddSingleton(AlwaysFullBudgetThrottle());
        services.AddSingleton<TimeProvider>(_timeProvider);
        services.AddSingleton(Options.Create(_options));
        services.AddSingleton(new ManualRefreshInFlightGate());
        services.AddSingleton<ILogger<PriceRefreshService>>(NullLogger<PriceRefreshService>.Instance);
        services.AddScoped<PriceRefreshService>();
        services.AddScoped<IPriceRefreshService>(sp => sp.GetRequiredService<PriceRefreshService>());
        var provider = services.BuildServiceProvider();

        var sut = provider.GetRequiredService<IPriceRefreshService>();

        var result = await sut.RefreshNowAsync(CancellationToken.None);

        // The distinguishing assertion: the code path taken is the detach, not the synchronous
        // one — against the pre-fix shape (always synchronous), this would have been Completed.
        result.Outcome.Should().Be(PriceRefreshOutcome.Queued);
        result.Sources.Should().BeEmpty();

        // "Queued" must not mean "silently doing nothing" - poll briefly for the detached sweep
        // to actually land its RefreshRun and PriceQuote rows.
        var completed = false;
        for (var attempt = 0; attempt < 100 && !completed; attempt++)
        {
            await Task.Delay(20);
            await using var pollDb = new PortfolioDbContext(
                new DbContextOptionsBuilder<PortfolioDbContext>().UseInMemoryDatabase(dbName).Options);
            completed = await pollDb.RefreshRuns.AnyAsync(r => r.Trigger == RefreshTrigger.Manual);
        }

        completed.Should().BeTrue("the detached sweep must actually complete and record its RefreshRun, not silently do nothing");

        await using var finalDb = new PortfolioDbContext(
            new DbContextOptionsBuilder<PortfolioDbContext>().UseInMemoryDatabase(dbName).Options);
        (await finalDb.PriceQuotes.CountAsync()).Should().Be(9, "every symbol's quote must have been written by the background sweep");
    }
}

file static class SubstituteExtensions
{
    /// <summary>Small fluent helper so a substitute's <c>Kind</c> can be configured inline where
    /// it is created, instead of a separate statement at every call site.</summary>
    public static T Also<T>(this T value, Action<T> configure)
    {
        configure(value);
        return value;
    }
}
