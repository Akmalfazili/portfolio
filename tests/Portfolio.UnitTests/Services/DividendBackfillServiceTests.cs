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
/// <see cref="DividendBackfillService"/> — idempotency against the unique (AssetId, ExDate) index,
/// the "not attempted vs attempted-and-failed" split (D26 applied to dividends), the per-asset
/// <see cref="AssetDividendState"/> upsert, and the crypto exclusion.
/// </summary>
public sealed class DividendBackfillServiceTests : IDisposable
{
    private readonly PortfolioDbContext _db;
    private readonly FixedTimeProvider _timeProvider;
    private readonly Asset _aapl;

    public DividendBackfillServiceTests()
    {
        var options = new DbContextOptionsBuilder<PortfolioDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new PortfolioDbContext(options);

        _aapl = new Asset
        {
            Id = 1, Symbol = "AAPL", Name = "Apple Inc.", AssetClass = AssetClass.Stock, Currency = "USD",
            QuoteProviderKind = QuoteProviderKind.TwelveData, ProviderSymbol = "AAPL",
        };
        _db.Assets.Add(_aapl);
        _db.Transactions.Add(new Transaction
        {
            AssetId = _aapl.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 10m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        _db.SaveChanges();

        _timeProvider = new FixedTimeProvider(new DateTimeOffset(2027, 3, 1, 0, 0, 0, TimeSpan.Zero));
    }

    public void Dispose() => _db.Dispose();

    private DividendBackfillService CreateSut(
        IDividendProvider provider, int maxAssetsPerRun = 500, TimeProvider? timeProvider = null) =>
        new(_db, provider, timeProvider ?? _timeProvider, Options.Create(new DividendBackfillOptions { MaxAssetsPerRun = maxAssetsPerRun }), NullLogger<DividendBackfillService>.Instance);

    [Fact]
    public async Task RunAsync_InsertsDividendEvents_AndUpsertsASuccessfulAssetDividendState()
    {
        var provider = Substitute.For<IDividendProvider>();
        provider.GetDividendHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(DividendHistoryFetchResult.Ok(
            [
                new DividendPoint(new DateOnly(2026, 2, 1), 0.25m, "USD"),
                new DividendPoint(new DateOnly(2026, 5, 1), 0.26m, "USD"),
            ]));

        var sut = CreateSut(provider);

        var summary = await sut.RunAsync(RefreshTrigger.DividendBackfillManual, CancellationToken.None);

        summary.AssetsProcessed.Should().Contain("AAPL");
        summary.DividendEventsInserted.Should().Be(2);
        (await _db.DividendEvents.CountAsync()).Should().Be(2);

        var state = await _db.AssetDividendStates.SingleAsync(s => s.AssetId == _aapl.Id);
        state.LastRunSuccess.Should().BeTrue();
        state.LastSuccessAt.Should().Be(_timeProvider.GetUtcNow());
    }

    [Fact]
    public async Task RunAsync_IsIdempotent_RerunningDoesNotDuplicate()
    {
        var provider = Substitute.For<IDividendProvider>();
        provider.GetDividendHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(DividendHistoryFetchResult.Ok([new DividendPoint(new DateOnly(2026, 2, 1), 0.25m, "USD")]));

        var sut = CreateSut(provider);

        await sut.RunAsync(RefreshTrigger.DividendBackfillManual, CancellationToken.None);
        var second = await sut.RunAsync(RefreshTrigger.DividendBackfillManual, CancellationToken.None);

        second.DividendEventsInserted.Should().Be(0);
        (await _db.DividendEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_ProviderReportsFailure_IsReportedAsAssetsFailed_AndStateRecordsIt()
    {
        var provider = Substitute.For<IDividendProvider>();
        provider.GetDividendHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(DividendHistoryFetchResult.Failed("Yahoo returned HTTP 500."));

        var sut = CreateSut(provider);

        var summary = await sut.RunAsync(RefreshTrigger.DividendBackfillManual, CancellationToken.None);

        summary.AssetsFailed.Should().ContainSingle(f => f.Symbol == "AAPL" && f.Error == "Yahoo returned HTTP 500.");
        summary.AssetsProcessed.Should().BeEmpty();

        var state = await _db.AssetDividendStates.SingleAsync(s => s.AssetId == _aapl.Id);
        state.LastRunSuccess.Should().BeFalse();
        state.LastError.Should().Be("Yahoo returned HTTP 500.");
    }

    [Fact]
    public async Task RunAsync_ProviderThrows_IsReportedAsAssetsFailed_WithTheExceptionMessage()
    {
        var provider = Substitute.For<IDividendProvider>();
        provider.GetDividendHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns<Task<DividendHistoryFetchResult>>(_ => throw new InvalidOperationException("transport blew up"));

        var sut = CreateSut(provider);

        var summary = await sut.RunAsync(RefreshTrigger.DividendBackfillManual, CancellationToken.None);

        summary.AssetsFailed.Should().ContainSingle(f => f.Symbol == "AAPL" && f.Error == "transport blew up");
    }

    [Fact]
    public async Task RunAsync_BudgetOfZero_SkipsWithoutCallingTheProvider_AndIsNotAFailure()
    {
        var provider = Substitute.For<IDividendProvider>();

        var sut = CreateSut(provider, maxAssetsPerRun: 0);

        var summary = await sut.RunAsync(RefreshTrigger.DividendBackfillManual, CancellationToken.None);

        summary.AssetsSkippedForBudget.Should().Contain("AAPL");
        summary.AssetsFailed.Should().BeEmpty();
        await provider.DidNotReceive().GetDividendHistoryAsync(
            Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ExcludesCryptoAssets_EvenWhenTheyHaveTransactions()
    {
        var eth = new Asset
        {
            Id = 4, Symbol = "ETH", Name = "Ethereum", AssetClass = AssetClass.Crypto, Currency = "USD",
            QuoteProviderKind = QuoteProviderKind.CoinGecko, ProviderCoinId = "ethereum",
        };
        _db.Assets.Add(eth);
        _db.Transactions.Add(new Transaction
        {
            AssetId = eth.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 1m, PricePerUnit = 2000m, Fees = 0m, Currency = "USD",
        });
        await _db.SaveChangesAsync();

        var provider = Substitute.For<IDividendProvider>();
        provider.GetDividendHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(DividendHistoryFetchResult.Ok([]));

        var sut = CreateSut(provider);

        var summary = await sut.RunAsync(RefreshTrigger.DividendBackfillManual, CancellationToken.None);

        summary.AssetsProcessed.Should().NotContain("ETH");
        await provider.DidNotReceive().GetDividendHistoryAsync(eth, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunIfDueAsync_AlreadyRanScheduledToday_WithAssetsProcessed_SkipsWithoutCallingTheProvider()
    {
        // A run only "counts" toward the once-per-day gate once it actually processed something
        // (SymbolsRefreshed > 0) - see the Defect 1 fix below for why a zero-asset run must NOT
        // count the same way.
        var provider = Substitute.For<IDividendProvider>();
        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.DividendBackfillScheduled,
            AssetClass = AssetClass.Stock,
            StartedAt = _timeProvider.GetUtcNow(),
            CompletedAt = _timeProvider.GetUtcNow(),
            Success = true,
            SymbolsRefreshed = 1,
        });
        await _db.SaveChangesAsync();

        var sut = CreateSut(provider);

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.Outcome.Should().Be(DividendBackfillOutcome.AlreadyRanToday);
        await provider.DidNotReceive().GetDividendHistoryAsync(
            Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunIfDueAsync_PreviousProductiveRunJustAfterNyseClose_StillGatesAcrossTheFollowingUtcMidnight()
    {
        // Same reasoning as the equivalent PriceBackfillService test: NYSE closes ~20:00-21:00
        // UTC, already past the SGT day boundary (16:00 UTC), so a run written right after close
        // (here, 2026-07-25T21:00:00Z) lands in SGT day 2026-07-26. A later overnight poll at
        // 2026-07-26T01:00:00Z has crossed UTC midnight but is still SGT day 2026-07-26 (09:00
        // SGT) - the gate must still hold and must not re-run just because the UTC date ticked
        // over.
        var provider = Substitute.For<IDividendProvider>();
        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.DividendBackfillScheduled,
            AssetClass = AssetClass.Stock,
            StartedAt = new DateTimeOffset(2026, 7, 25, 21, 0, 0, TimeSpan.Zero),
            CompletedAt = new DateTimeOffset(2026, 7, 25, 21, 0, 1, TimeSpan.Zero),
            Success = true,
            SymbolsRefreshed = 1,
        });
        await _db.SaveChangesAsync();

        var overnightPollTime = new FixedTimeProvider(new DateTimeOffset(2026, 7, 26, 1, 0, 0, TimeSpan.Zero));
        var sut = CreateSut(provider, timeProvider: overnightPollTime);

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.Outcome.Should().Be(DividendBackfillOutcome.AlreadyRanToday);
        await provider.DidNotReceive().GetDividendHistoryAsync(
            Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Defect 1, found live against the dev database: a scheduled run whose stock asset list was
    /// empty (no stock has any transaction yet) still wrote a RefreshRun unconditionally, with
    /// SymbolsRefreshed == 0 - and the OLD gate ("any scheduled run today") then locked out the
    /// real backfill for up to 24 hours the moment the day's first stock transaction was recorded,
    /// with DividendEvents/AssetDividendStates left completely empty. The fix gates on the most
    /// recent PRODUCTIVE scheduled run (SymbolsRefreshed > 0) instead, so a day is only consumed by
    /// a run that actually accomplished something.
    /// </summary>
    [Fact]
    public async Task RunIfDueAsync_PriorRunTodayProcessedZeroAssets_DoesNotConsumeTheDay_AndRunsAgain()
    {
        // No stock has any transaction at all - simulates a fresh portfolio, the exact scenario
        // that triggered Defect 1 live.
        _db.Transactions.RemoveRange(_db.Transactions);
        await _db.SaveChangesAsync();

        var provider = Substitute.For<IDividendProvider>();

        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.DividendBackfillScheduled,
            AssetClass = AssetClass.Stock,
            StartedAt = _timeProvider.GetUtcNow(),
            CompletedAt = _timeProvider.GetUtcNow(),
            Success = true,
            SymbolsRefreshed = 0, // accomplished nothing - the exact row Defect 1 wrote unconditionally
        });
        await _db.SaveChangesAsync();

        var sut = CreateSut(provider);

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        // Must NOT be gated out - a zero-asset run is free (no Yahoo call is made), so re-checking
        // costs nothing and must not wait until tomorrow.
        result.Outcome.Should().Be(DividendBackfillOutcome.Completed);
        result.Summary!.AssetsProcessed.Should().BeEmpty();
        await provider.DidNotReceive().GetDividendHistoryAsync(
            Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The other half of Defect 1's fix, made explicit: once a stock transaction exists and the
    /// asset is genuinely processed, the day IS consumed - a zero-asset run being free to retry
    /// must not regress into every run being retried forever.
    /// </summary>
    [Fact]
    public async Task RunIfDueAsync_PriorRunTodayProcessedAnAsset_DoesConsumeTheDay()
    {
        var provider = Substitute.For<IDividendProvider>();
        provider.GetDividendHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(DividendHistoryFetchResult.Ok([]));

        var sut = CreateSut(provider);

        var first = await sut.RunIfDueAsync(CancellationToken.None);
        first.Outcome.Should().Be(DividendBackfillOutcome.Completed);
        first.Summary!.AssetsProcessed.Should().Contain("AAPL");

        var second = await sut.RunIfDueAsync(CancellationToken.None);

        second.Outcome.Should().Be(DividendBackfillOutcome.AlreadyRanToday);
        await provider.Received(1).GetDividendHistoryAsync(
            Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Defect 2: <see cref="RefreshTrigger.DividendBackfillManual"/> existed but nothing ever wrote
    /// it, and there was no way to trigger a dividend backfill on demand. Pins that
    /// <c>POST /api/dividends/backfill</c>'s handler (<c>DividendsEndpoints</c>) calling
    /// <c>RunAsync(RefreshTrigger.DividendBackfillManual, ...)</c> actually produces a
    /// distinguishable audit row - this is also how Defect 1's zero-asset lockout is recovered by
    /// hand rather than by waiting for the next calendar day.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithManualTrigger_RecordsARefreshRunTaggedAsManual()
    {
        var provider = Substitute.For<IDividendProvider>();
        provider.GetDividendHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(DividendHistoryFetchResult.Ok([]));

        var sut = CreateSut(provider);

        await sut.RunAsync(RefreshTrigger.DividendBackfillManual, CancellationToken.None);

        var run = (await _db.RefreshRuns.ToListAsync()).Should().ContainSingle().Subject;
        run.Trigger.Should().Be(RefreshTrigger.DividendBackfillManual);
    }

    [Fact]
    public async Task RunIfDueAsync_NoPriorRunToday_RunsAndRecordsAScheduledRefreshRun()
    {
        var provider = Substitute.For<IDividendProvider>();
        provider.GetDividendHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(DividendHistoryFetchResult.Ok([]));

        var sut = CreateSut(provider);

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.Outcome.Should().Be(DividendBackfillOutcome.Completed);
        var run = (await _db.RefreshRuns.ToListAsync()).Should().ContainSingle().Subject;
        run.Trigger.Should().Be(RefreshTrigger.DividendBackfillScheduled);
    }
}
