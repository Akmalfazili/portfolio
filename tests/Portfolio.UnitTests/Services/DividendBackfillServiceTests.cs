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
        IDividendProvider provider,
        int maxAssetsPerRun = 500,
        TimeProvider? timeProvider = null,
        TimeSpan? failedAssetRetryInterval = null) =>
        new(
            _db,
            provider,
            timeProvider ?? _timeProvider,
            Options.Create(new DividendBackfillOptions
            {
                MaxAssetsPerRun = maxAssetsPerRun,
                FailedAssetRetryInterval = failedAssetRetryInterval ?? TimeSpan.FromMinutes(30),
            }),
            NullLogger<DividendBackfillService>.Instance);

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

    // --- D51: a partial failure inside today's full run no longer latches the whole reporting
    // day — a failed asset gets a narrowly-scoped retry once FailedAssetRetryInterval has passed.
    // Regression tests confirmed to FAIL against the pre-D51 code (via a detached git worktree
    // probe, same technique D47 used) before the fix was written — see tracker.md's D51 entry.

    private Asset AddMsft()
    {
        var msft = new Asset
        {
            Id = 2, Symbol = "MSFT", Name = "Microsoft", AssetClass = AssetClass.Stock, Currency = "USD",
            QuoteProviderKind = QuoteProviderKind.TwelveData, ProviderSymbol = "MSFT",
        };
        _db.Assets.Add(msft);
        _db.Transactions.Add(new Transaction
        {
            AssetId = msft.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 5m, PricePerUnit = 300m, Fees = 0m, Currency = "USD",
        });
        return msft;
    }

    [Fact]
    public async Task RunIfDueAsync_D51_PartialFailureToday_RetriesOnlyTheFailedAssetOnceTheIntervalHasPassed()
    {
        var msft = AddMsft();

        // Today's full run already happened and was productive (AAPL succeeded) — but MSFT's own
        // attempt inside it failed, well outside the default 30-minute FailedAssetRetryInterval.
        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.DividendBackfillScheduled,
            AssetClass = AssetClass.Stock,
            StartedAt = _timeProvider.GetUtcNow().AddHours(-1),
            CompletedAt = _timeProvider.GetUtcNow().AddHours(-1),
            Success = false,
            SymbolsRefreshed = 1, // AAPL alone still counts as "productive"
        });
        _db.AssetDividendStates.Add(new AssetDividendState
        {
            AssetId = _aapl.Id, LastAttemptedAt = _timeProvider.GetUtcNow().AddHours(-1), LastRunSuccess = true,
        });
        _db.AssetDividendStates.Add(new AssetDividendState
        {
            AssetId = msft.Id,
            LastAttemptedAt = _timeProvider.GetUtcNow().AddMinutes(-45), // 45 min ago > 30-min interval
            LastRunSuccess = false,
            LastError = "Yahoo returned HTTP 500.",
        });
        await _db.SaveChangesAsync();

        var provider = Substitute.For<IDividendProvider>();
        provider.GetDividendHistoryAsync(msft, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(DividendHistoryFetchResult.Ok([new DividendPoint(new DateOnly(2026, 2, 1), 0.75m, "USD")]));

        var sut = CreateSut(provider);

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.Outcome.Should().Be(DividendBackfillOutcome.RetryCompleted);
        result.Summary!.AssetsProcessed.Should().Contain("MSFT").And.NotContain("AAPL");
        await provider.DidNotReceive().GetDividendHistoryAsync(
            _aapl, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        await provider.Received(1).GetDividendHistoryAsync(
            msft, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());

        var msftState = await _db.AssetDividendStates.SingleAsync(s => s.AssetId == msft.Id);
        msftState.LastRunSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task RunIfDueAsync_D51_PartialFailureToday_NotYetDueForRetry_BeforeTheIntervalElapses()
    {
        var msft = AddMsft();

        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.DividendBackfillScheduled,
            AssetClass = AssetClass.Stock,
            StartedAt = _timeProvider.GetUtcNow().AddMinutes(-10),
            CompletedAt = _timeProvider.GetUtcNow().AddMinutes(-10),
            Success = false,
            SymbolsRefreshed = 1,
        });
        _db.AssetDividendStates.Add(new AssetDividendState
        {
            AssetId = msft.Id,
            LastAttemptedAt = _timeProvider.GetUtcNow().AddMinutes(-10), // only 10 min ago < 30-min interval
            LastRunSuccess = false,
            LastError = "Yahoo returned HTTP 500.",
        });
        await _db.SaveChangesAsync();

        var provider = Substitute.For<IDividendProvider>();
        var sut = CreateSut(provider);

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.Outcome.Should().Be(DividendBackfillOutcome.AlreadyRanToday);
        result.Summary.Should().BeNull();
        await provider.DidNotReceive().GetDividendHistoryAsync(
            Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunIfDueAsync_D51_PartialFailureToday_ButAssetHasSinceSucceeded_DoesNotRetryIt()
    {
        // Proves the retry scan reads the CURRENT AssetDividendState, not a snapshot of what the
        // failed RefreshRun row once reported — a success recorded after that row (e.g. via a
        // manual retry) must clear the asset from the retry scan.
        var msft = AddMsft();

        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.DividendBackfillScheduled,
            AssetClass = AssetClass.Stock,
            StartedAt = _timeProvider.GetUtcNow().AddHours(-1),
            CompletedAt = _timeProvider.GetUtcNow().AddHours(-1),
            Success = true,
            SymbolsRefreshed = 2,
        });
        _db.AssetDividendStates.Add(new AssetDividendState
        {
            AssetId = msft.Id, LastAttemptedAt = _timeProvider.GetUtcNow().AddHours(-1), LastRunSuccess = true,
        });
        await _db.SaveChangesAsync();

        var provider = Substitute.For<IDividendProvider>();
        var sut = CreateSut(provider);

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.Outcome.Should().Be(DividendBackfillOutcome.AlreadyRanToday);
        await provider.DidNotReceive().GetDividendHistoryAsync(
            Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    // --- D51 follow-up (found on coordinator review, before deploy): an ALL-failed full run has
    // SymbolsRefreshed == 0, the same shape D41's genuinely-nothing-to-do case has — before this
    // fix it fell through D41's unpaced branch and re-ran a full pass on every poll tick. Regression
    // confirmed FAILING against the post-first-D51-pass/pre-follow-up code (a detached git worktree
    // probe, same technique used throughout D51) before this fix was written — see tracker.md.

    [Fact]
    public async Task RunIfDueAsync_D51FollowUp_AllFailedFullRun_NotDueForRetryAt15Minutes_ReturnsRetryPending()
    {
        // Well short of the default 30-minute FailedAssetRetryInterval — must NOT re-run.
        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.DividendBackfillScheduled,
            AssetClass = AssetClass.Stock,
            StartedAt = _timeProvider.GetUtcNow().AddMinutes(-15),
            CompletedAt = _timeProvider.GetUtcNow().AddMinutes(-15),
            Success = false, // every asset failed
            SymbolsRefreshed = 0,
        });
        await _db.SaveChangesAsync();

        var provider = Substitute.For<IDividendProvider>();
        var sut = CreateSut(provider);

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.Outcome.Should().Be(DividendBackfillOutcome.RetryPending);
        result.Summary.Should().BeNull();
        await provider.DidNotReceive().GetDividendHistoryAsync(
            Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunIfDueAsync_D51FollowUp_AllFailedFullRun_DueForAFullRerunAt30Minutes()
    {
        // Past the default 30-minute FailedAssetRetryInterval — a full rerun is due (there is
        // nothing narrower to retry than everything, since every asset failed last time).
        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.DividendBackfillScheduled,
            AssetClass = AssetClass.Stock,
            StartedAt = _timeProvider.GetUtcNow().AddMinutes(-35),
            CompletedAt = _timeProvider.GetUtcNow().AddMinutes(-35),
            Success = false,
            SymbolsRefreshed = 0,
        });
        await _db.SaveChangesAsync();

        var provider = Substitute.For<IDividendProvider>();
        provider.GetDividendHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(DividendHistoryFetchResult.Ok([new DividendPoint(new DateOnly(2026, 2, 1), 0.25m, "USD")]));

        var sut = CreateSut(provider);

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.Outcome.Should().Be(DividendBackfillOutcome.Completed);
        result.Summary!.AssetsProcessed.Should().Contain("AAPL");
        await provider.Received(1).GetDividendHistoryAsync(
            _aapl, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunIfDueAsync_D51FollowUp_ZeroAssetSuccessfulRun_RunsAgainImmediately_D41RegressionGuard()
    {
        // D41's shape: a run with nothing to do (Success == true, SymbolsRefreshed == 0) must NOT
        // be paced by FailedAssetRetryInterval, even seconds later — only a FAILED zero-asset run
        // (the D51 follow-up case above) is paced.
        _db.Transactions.RemoveRange(_db.Transactions);
        await _db.SaveChangesAsync();

        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.DividendBackfillScheduled,
            AssetClass = AssetClass.Stock,
            StartedAt = _timeProvider.GetUtcNow().AddMinutes(-1),
            CompletedAt = _timeProvider.GetUtcNow().AddMinutes(-1),
            Success = true, // nothing to do, not a failure
            SymbolsRefreshed = 0,
        });
        await _db.SaveChangesAsync();

        // AAPL's first transaction has just been recorded - the exact D41 trigger.
        _db.Transactions.Add(new Transaction
        {
            AssetId = _aapl.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2027, 3, 1),
            Quantity = 10m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        await _db.SaveChangesAsync();

        var provider = Substitute.For<IDividendProvider>();
        provider.GetDividendHistoryAsync(Arg.Any<Asset>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(DividendHistoryFetchResult.Ok([]));

        var sut = CreateSut(provider);

        var result = await sut.RunIfDueAsync(CancellationToken.None);

        result.Outcome.Should().Be(DividendBackfillOutcome.Completed);
        result.Summary!.AssetsProcessed.Should().Contain("AAPL");
        await provider.Received(1).GetDividendHistoryAsync(
            _aapl, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// D51 escalation follow-up (found on coordinator review, before deploy): a narrowed per-asset
    /// retry writes its OWN <see cref="RefreshRun"/> row, and if every asset in that NARROWED set
    /// fails again (e.g. one persistently delisted symbol), that row reads
    /// <c>SymbolsRefreshed == 0 &amp;&amp; !Success</c> too — structurally identical to a fresh
    /// all-failed FULL run once it becomes the newest row. Keying the gate on "the single most
    /// recent scheduled run" (the shape of the first D51 follow-up's fix) misread it as exactly
    /// that and escalated into a full run over every asset, every
    /// <see cref="DividendBackfillOptions.FailedAssetRetryInterval"/>, for one bad symbol — the same
    /// Yahoo-hammering the first follow-up had just closed, reopened through a different door.
    /// Regression confirmed FAILING against the post-follow-up-#1/pre-follow-up-#2 code (a detached
    /// git worktree probe) before this fix was written — see tracker.md.
    ///
    /// Driven end to end through the real SUT (not hand-seeded rows) across three ticks: an initial
    /// full run (AAPL succeeds, MSFT fails), then two more ticks 35 minutes apart, each past
    /// <see cref="DividendBackfillOptions.FailedAssetRetryInterval"/>. AAPL must never be re-fetched
    /// after its one success, and every later tick must stay a narrowed MSFT-only retry
    /// (<see cref="DividendBackfillOutcome.RetryCompleted"/>) — never escalate back to
    /// <see cref="DividendBackfillOutcome.Completed"/> (a full run).
    /// </summary>
    [Fact]
    public async Task RunIfDueAsync_D51Escalation_APersistentlyFailingAssetsNarrowedRetry_NeverEscalatesToAFullRun()
    {
        var msft = AddMsft();
        await _db.SaveChangesAsync();

        var provider = Substitute.For<IDividendProvider>();
        provider.GetDividendHistoryAsync(_aapl, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(DividendHistoryFetchResult.Ok([]));
        provider.GetDividendHistoryAsync(msft, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(DividendHistoryFetchResult.Failed("Yahoo returned HTTP 404 (delisted)."));

        var pollTime = new MutableTimeProvider(new DateTimeOffset(2027, 3, 1, 8, 0, 0, TimeSpan.Zero));
        var sut = CreateSut(provider, timeProvider: pollTime);

        // Tick 1: nothing has run today - a full run. AAPL succeeds, MSFT fails.
        var first = await sut.RunIfDueAsync(CancellationToken.None);
        first.Outcome.Should().Be(DividendBackfillOutcome.Completed);
        first.Summary!.AssetsProcessed.Should().Contain("AAPL");
        first.Summary!.AssetsFailed.Should().ContainSingle(f => f.Symbol == "MSFT");

        // Tick 2, 35 minutes later (past the default 30-minute FailedAssetRetryInterval): a
        // productive run already happened today, so this must be a NARROWED retry of MSFT alone -
        // which also fails, writing a SymbolsRefreshed == 0 && !Success row of its own.
        pollTime.Now = pollTime.Now.AddMinutes(35);
        var second = await sut.RunIfDueAsync(CancellationToken.None);
        second.Outcome.Should().Be(DividendBackfillOutcome.RetryCompleted);
        second.Summary!.AssetsFailed.Should().ContainSingle(f => f.Symbol == "MSFT");

        // Tick 3, another 35 minutes later: THE ESCALATION CHECK. The most recent row (tick 2's
        // narrowed retry) itself reads SymbolsRefreshed == 0 && !Success - the bug misread this as
        // a fresh all-failed FULL run and re-fetched every asset. It must instead still recognise
        // today's original run as productive and stay on the narrowed MSFT-only retry path.
        pollTime.Now = pollTime.Now.AddMinutes(35);
        var third = await sut.RunIfDueAsync(CancellationToken.None);
        third.Outcome.Should().Be(DividendBackfillOutcome.RetryCompleted);
        third.Summary!.AssetsFailed.Should().ContainSingle(f => f.Symbol == "MSFT");

        // AAPL was fetched exactly once, ever - ticks 2 and 3 must never have touched it.
        await provider.Received(1).GetDividendHistoryAsync(
            _aapl, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        await provider.Received(3).GetDividendHistoryAsync(
            msft, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }
}
