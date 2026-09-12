using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
/// Proves the refresh status outlives the process that produced it. Each test opens a *second*
/// <see cref="PortfolioDbContext"/> over the same database to stand in for a restart: new context,
/// new store, nothing carried over in memory.
///
/// The stakes are not just a blank UI indicator. <c>NextDueAt</c> is what gates the 5/60/2-minute
/// cadence, so when it was held in memory every restart made all three providers due immediately
/// and re-spent Twelve Data credits that had been spent moments earlier — a debugging session with
/// a dozen restarts could burn a meaningful slice of the 800/day budget.
/// </summary>
public sealed class PriceRefreshStatusStoreTests : IDisposable
{
    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly PortfolioDbContext _db;
    private readonly MutableTimeProvider _time = new(new DateTimeOffset(2026, 7, 29, 15, 0, 0, TimeSpan.Zero));
    private readonly IMarketCalendar _calendar = Substitute.For<IMarketCalendar>();
    private readonly IPriceUpdateBroadcaster _broadcaster = Substitute.For<IPriceUpdateBroadcaster>();
    private readonly PriceRefreshOptions _options = new();

    private readonly Asset _eth = new()
    {
        Id = 4,
        Symbol = "ETH",
        Name = "Ethereum",
        AssetClass = AssetClass.Crypto,
        Currency = "USD",
        QuoteProviderKind = QuoteProviderKind.CoinGecko,
        ProviderCoinId = "ethereum",
    };

    public PriceRefreshStatusStoreTests()
    {
        _db = NewContext();
        _db.Assets.Add(_eth);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private PortfolioDbContext NewContext() => new(
        new DbContextOptionsBuilder<PortfolioDbContext>().UseInMemoryDatabase(_databaseName).Options);

    [Fact]
    public async Task Snapshot_SurvivesARestart()
    {
        var store = new PriceRefreshStatusStore(_db);
        var at = _time.Now;

        await store.RecordOutcomeAsync(
            new SourceRefreshOutcome(QuoteProviderKind.CoinGecko, Attempted: true, Success: true, SymbolsRefreshed: 3, Error: null),
            at,
            at + _options.CryptoInterval,
            CancellationToken.None);

        // Restart: fresh context, fresh store, nothing shared but the database.
        await using var restarted = NewContext();
        var afterRestart = new PriceRefreshStatusStore(restarted);

        var snapshot = await afterRestart.GetSnapshotAsync(false, false, CancellationToken.None);

        snapshot.LastRefreshedAt.Should().Be(at);
        snapshot.Sources.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                Source = QuoteProviderKind.CoinGecko,
                LastAttemptedAt = (DateTimeOffset?)at,
                LastSuccessAt = (DateTimeOffset?)at,
                LastRunSuccess = true,
                SymbolsRefreshed = 3,
                NextDueAt = (DateTimeOffset?)(at + _options.CryptoInterval),
            });
    }

    [Fact]
    public async Task ARestartDoesNotMakeAnAlreadyRefreshedProviderDueAgain()
    {
        var coinGecko = Substitute.For<IQuoteProvider>();
        coinGecko.Kind.Returns(QuoteProviderKind.CoinGecko);
        coinGecko.GetQuotesAsync(Arg.Any<IReadOnlyCollection<Asset>>(), Arg.Any<CancellationToken>())
            .Returns([new QuoteFetchResult(_eth.Id, true, 2500m, "USD", _time.Now, null)]);

        var router = Substitute.For<IQuoteProviderRouter>();
        router.GetProvider(Arg.Any<Asset>()).Returns(coinGecko);

        (await CreateService(_db, router).RefreshDueAsync(CancellationToken.None))
            .Outcome.Should().Be(PriceRefreshOutcome.Completed);
        await coinGecko.Received(1).GetQuotesAsync(Arg.Any<IReadOnlyCollection<Asset>>(), Arg.Any<CancellationToken>());

        // Restart within the crypto interval. The provider was refreshed seconds ago, so the very
        // first cycle after startup must not call it again.
        _time.Now += TimeSpan.FromSeconds(5);
        await using var restarted = NewContext();

        var result = await CreateService(restarted, router).RefreshDueAsync(CancellationToken.None);

        result.Outcome.Should().Be(PriceRefreshOutcome.NothingDue);
        await coinGecko.Received(1).GetQuotesAsync(Arg.Any<IReadOnlyCollection<Asset>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARestartAfterTheIntervalHasElapsed_StillRefreshes()
    {
        // The guard above must not turn into a permanent block: once the interval really has
        // passed, a restarted process refreshes normally.
        var coinGecko = Substitute.For<IQuoteProvider>();
        coinGecko.Kind.Returns(QuoteProviderKind.CoinGecko);
        coinGecko.GetQuotesAsync(Arg.Any<IReadOnlyCollection<Asset>>(), Arg.Any<CancellationToken>())
            .Returns([new QuoteFetchResult(_eth.Id, true, 2500m, "USD", _time.Now, null)]);

        var router = Substitute.For<IQuoteProviderRouter>();
        router.GetProvider(Arg.Any<Asset>()).Returns(coinGecko);

        await CreateService(_db, router).RefreshDueAsync(CancellationToken.None);

        _time.Now += _options.CryptoInterval + TimeSpan.FromSeconds(1);
        await using var restarted = NewContext();

        var result = await CreateService(restarted, router).RefreshDueAsync(CancellationToken.None);

        result.Outcome.Should().Be(PriceRefreshOutcome.Completed);
        await coinGecko.Received(2).GetQuotesAsync(Arg.Any<IReadOnlyCollection<Asset>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordingTheSameSourceRepeatedly_KeepsExactlyOneRow()
    {
        // The table is current state, not history — it must not grow with every cycle.
        var store = new PriceRefreshStatusStore(_db);

        for (var i = 0; i < 5; i++)
        {
            await store.RecordOutcomeAsync(
                new SourceRefreshOutcome(QuoteProviderKind.CoinGecko, Attempted: true, Success: true, SymbolsRefreshed: 3, Error: null),
                _time.Now,
                _time.Now + _options.CryptoInterval,
                CancellationToken.None);
            _time.Now += _options.CryptoInterval;
        }

        (await _db.SourceRefreshStates.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ASkippedSourceMovesNextDueAt_WithoutErasingTheLastRealResult()
    {
        var store = new PriceRefreshStatusStore(_db);
        var succeededAt = _time.Now;

        await store.RecordOutcomeAsync(
            new SourceRefreshOutcome(QuoteProviderKind.TwelveData, Attempted: true, Success: true, SymbolsRefreshed: 2, Error: null),
            succeededAt,
            succeededAt + _options.StockOpenInterval,
            CancellationToken.None);

        // Market closes: the source is skipped, not called. That must only push the next check out.
        _time.Now += TimeSpan.FromHours(1);
        await store.RecordOutcomeAsync(
            new SourceRefreshOutcome(QuoteProviderKind.TwelveData, Attempted: false, Success: true, SymbolsRefreshed: 0, Error: null),
            _time.Now,
            _time.Now + _options.StockClosedInterval,
            CancellationToken.None);

        await using var restarted = NewContext();
        var snapshot = await new PriceRefreshStatusStore(restarted).GetSnapshotAsync(false, false, CancellationToken.None);

        var source = snapshot.Sources.Should().ContainSingle().Subject;
        source.LastAttemptedAt.Should().Be(succeededAt, "a skip is not an attempt");
        source.LastSuccessAt.Should().Be(succeededAt);
        source.SymbolsRefreshed.Should().Be(2, "the skip must not zero out what the last real call achieved");
        source.NextDueAt.Should().Be(_time.Now + _options.StockClosedInterval);
    }

    private PriceRefreshService CreateService(PortfolioDbContext db, IQuoteProviderRouter router)
    {
        var creditThrottle = Substitute.For<ITwelveDataCreditThrottle>();
        creditThrottle.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(new TwelveDataCreditStatus(0, 800, 800));

        return new PriceRefreshService(
            db,
            router,
            _calendar,
            _broadcaster,
            new PriceRefreshStatusStore(db),
            new PriceRefreshStatusEnricher(db, creditThrottle, Options.Create(_options)),
            creditThrottle,
            Substitute.For<IServiceScopeFactory>(), // unused — no test here exercises RefreshNowAsync's detach path
            new ManualRefreshInFlightGate(),
            _time,
            Options.Create(_options),
            NullLogger<PriceRefreshService>.Instance);
    }
}
