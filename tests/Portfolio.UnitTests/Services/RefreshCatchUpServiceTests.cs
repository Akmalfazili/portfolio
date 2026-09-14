using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Services;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;
using Portfolio.Infrastructure.Persistence;
using Portfolio.UnitTests.TestSupport;

namespace Portfolio.UnitTests.Services;

/// <summary>
/// <see cref="RefreshCatchUpService"/> — the refresh-catch-up feature's planner. DB-only: every
/// test here seeds rows directly and asserts on <see cref="RefreshCatchUpCandidates"/>, never a
/// provider mock, since <see cref="IRefreshCatchUpService.PlanAsync"/> never calls one.
///
/// The central trap this guards against: a naive "MIN(PriceHistory.Date) &gt; first trade date ⇒
/// missing" rule would re-fetch a permanently-gapped asset (AAPL's own shape — first trade a
/// Saturday, first close the following Monday) on every single call, forever. Every "covered"
/// assertion below distinguishes stored-data coverage from state-recorded coverage on purpose.
/// </summary>
public sealed class RefreshCatchUpServiceTests : IDisposable
{
    // Chosen so PriceBackfillCapCalculator's settled-instant subtraction (default 30-minute
    // CloseSettleDelay) never crosses a date boundary — irrelevant here anyway, since the calendar
    // substitute below returns a fixed cap regardless of the instant it's asked about.
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Cap = new(2026, 8, 9);

    private readonly PortfolioDbContext _db;
    private readonly FixedTimeProvider _timeProvider = new(Now);

    public RefreshCatchUpServiceTests()
    {
        var options = new DbContextOptionsBuilder<PortfolioDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new PortfolioDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    private RefreshCatchUpService CreateSut(
        IMarketCalendar? calendar = null,
        TimeSpan? failedRunRetryDelay = null,
        TimeSpan? failedAssetRetryInterval = null) =>
        new(
            _db,
            calendar ?? CalendarWithFixedCap(Cap),
            _timeProvider,
            Options.Create(new PriceBackfillOptions { FailedRunRetryDelay = failedRunRetryDelay ?? TimeSpan.FromMinutes(15) }),
            Options.Create(new DividendBackfillOptions { FailedAssetRetryInterval = failedAssetRetryInterval ?? TimeSpan.FromMinutes(30) }));

    /// <summary>Every market's settled cap resolves to the same fixed date, regardless of the
    /// instant it's asked about — the planner's own market/session-walking correctness is
    /// PriceBackfillCapCalculator's and MarketCalendar's job, already covered elsewhere
    /// (PriceBackfillServiceTests' D53 suite); this file is about what the planner does with a cap
    /// once it has one.</summary>
    private static IMarketCalendar CalendarWithFixedCap(DateOnly cap)
    {
        var calendar = Substitute.For<IMarketCalendar>();
        calendar.LastSessionCloseAt(Arg.Any<Market>(), Arg.Any<DateTimeOffset>())
            .Returns(callInfo => callInfo.ArgAt<DateTimeOffset>(1));
        calendar.LocalDateOn(Arg.Any<Market>(), Arg.Any<DateTimeOffset>()).Returns(cap);
        return calendar;
    }

    private static Asset MakeAapl(int id = 1) => new()
    {
        Id = id, Symbol = "AAPL", Name = "Apple Inc.", AssetClass = AssetClass.Stock, Currency = "USD",
        QuoteProviderKind = QuoteProviderKind.TwelveData, ProviderSymbol = "AAPL",
    };

    private static Asset MakeZ74(int id = 2) => new()
    {
        Id = id, Symbol = "Z74", Name = "Singtel", AssetClass = AssetClass.Stock, Currency = "SGD",
        QuoteProviderKind = QuoteProviderKind.Yahoo, ProviderSymbol = "Z74.SI",
    };

    private void AddTransaction(Asset asset, DateOnly tradeDate) =>
        _db.Transactions.Add(new Transaction
        {
            AssetId = asset.Id, Type = TransactionType.Buy, TradeDate = tradeDate,
            Quantity = 1m, PricePerUnit = 10m, Fees = 0m, Currency = asset.Currency,
        });

    // --- Price history: the permanent-gap trap and ordinary missing/covered/not-yet-available.

    [Fact]
    public async Task PriceHistory_WeekendFirstTrade_NoStoredDataNoState_IsMissing()
    {
        var aapl = MakeAapl();
        _db.Assets.Add(aapl);
        AddTransaction(aapl, new DateOnly(2026, 7, 4)); // well before Cap, no PriceHistory on file at all
        await _db.SaveChangesAsync();

        var plan = await CreateSut().PlanAsync(CancellationToken.None);

        plan.PriceHistory.ToFetch.Should().ContainSingle(t => t.Symbol == "AAPL");
        plan.PriceHistory.NotYetAvailableSymbols.Should().BeEmpty();
        plan.PriceHistory.RetryPendingSymbols.Should().BeEmpty();
    }

    [Fact]
    public async Task PriceHistory_WeekendFirstTrade_NotMissingAfterOneSuccessfulFetchRecordsState()
    {
        // The permanent-gap trap itself: stored PriceHistory's own MIN date is AFTER firstTrade (the
        // close for the weekend/holiday trade date will never exist), but a prior successful attempt
        // recorded state covering [firstTrade, cap] — that alone must be enough to read as covered.
        var aapl = MakeAapl();
        _db.Assets.Add(aapl);
        var firstTrade = new DateOnly(2026, 7, 4);
        AddTransaction(aapl, firstTrade);
        _db.PriceHistories.Add(new PriceHistory { AssetId = aapl.Id, Date = new DateOnly(2026, 7, 6), Close = 190m, Currency = "USD" });
        _db.AssetPriceHistoryStates.Add(new AssetPriceHistoryState
        {
            AssetId = aapl.Id, LastAttemptedAt = Now, LastSuccessAt = Now, LastRunSuccess = true,
            CoveredFrom = firstTrade, CoveredTo = Cap,
        });
        await _db.SaveChangesAsync();

        var plan = await CreateSut().PlanAsync(CancellationToken.None);

        plan.PriceHistory.ToFetch.Should().NotContain(t => t.Symbol == "AAPL");
    }

    [Fact]
    public async Task PriceHistory_PreListingGap_ArvlfShape_CoveredByStateEvenThoughStoredDataStartsMuchLater()
    {
        // ARVLF's own shape: bought 2021-01-01, listed (first close) 2021-03-25 — an even wider gap
        // than the weekend case, same principle: state coverage, not stored data, is what settles it.
        var arvlf = new Asset
        {
            Id = 5, Symbol = "ARVLF", Name = "Arrival", AssetClass = AssetClass.Stock, Currency = "USD",
            QuoteProviderKind = QuoteProviderKind.TwelveData, ProviderSymbol = "ARVLF",
        };
        _db.Assets.Add(arvlf);
        var firstTrade = new DateOnly(2021, 1, 1);
        AddTransaction(arvlf, firstTrade);
        _db.PriceHistories.Add(new PriceHistory { AssetId = arvlf.Id, Date = new DateOnly(2021, 3, 25), Close = 12m, Currency = "USD" });
        _db.AssetPriceHistoryStates.Add(new AssetPriceHistoryState
        {
            AssetId = arvlf.Id, LastAttemptedAt = Now, LastSuccessAt = Now, LastRunSuccess = true,
            CoveredFrom = firstTrade, CoveredTo = Cap,
        });
        await _db.SaveChangesAsync();

        var plan = await CreateSut().PlanAsync(CancellationToken.None);

        plan.PriceHistory.ToFetch.Should().NotContain(t => t.Symbol == "ARVLF");
    }

    [Fact]
    public async Task PriceHistory_FullyCoveredByStoredData_IsNotMissing_AndNoStateRowIsNeeded()
    {
        var aapl = MakeAapl();
        _db.Assets.Add(aapl);
        var firstTrade = new DateOnly(2026, 7, 4);
        AddTransaction(aapl, firstTrade);
        // Stored data genuinely spans [firstTrade, Cap] — no AssetPriceHistoryState row exists at
        // all, proving the stored-data leg alone is enough (day-one cheapness, before any state
        // rows exist).
        _db.PriceHistories.Add(new PriceHistory { AssetId = aapl.Id, Date = firstTrade, Close = 190m, Currency = "USD" });
        _db.PriceHistories.Add(new PriceHistory { AssetId = aapl.Id, Date = Cap, Close = 195m, Currency = "USD" });
        await _db.SaveChangesAsync();

        var plan = await CreateSut().PlanAsync(CancellationToken.None);

        plan.PriceHistory.ToFetch.Should().BeEmpty();
        plan.PriceHistory.RetryPendingSymbols.Should().BeEmpty();
        plan.PriceHistory.NotYetAvailableSymbols.Should().BeEmpty();
    }

    [Fact]
    public async Task PriceHistory_NewStockWithNoHistoryAtAll_IsMissing()
    {
        var aapl = MakeAapl();
        _db.Assets.Add(aapl);
        AddTransaction(aapl, new DateOnly(2026, 8, 1));
        await _db.SaveChangesAsync();

        var plan = await CreateSut().PlanAsync(CancellationToken.None);

        plan.PriceHistory.ToFetch.Should().ContainSingle(t => t.Symbol == "AAPL");
    }

    [Fact]
    public async Task PriceHistory_BackDatedTransactionEarlierThanCoveredFrom_IsMissing()
    {
        var aapl = MakeAapl();
        _db.Assets.Add(aapl);
        var newEarlierFirstTrade = new DateOnly(2026, 6, 1);
        AddTransaction(aapl, newEarlierFirstTrade); // a back-dated transaction recorded after the state below
        _db.PriceHistories.Add(new PriceHistory { AssetId = aapl.Id, Date = new DateOnly(2026, 7, 4), Close = 190m, Currency = "USD" });
        _db.AssetPriceHistoryStates.Add(new AssetPriceHistoryState
        {
            AssetId = aapl.Id, LastAttemptedAt = Now, LastSuccessAt = Now, LastRunSuccess = true,
            CoveredFrom = new DateOnly(2026, 7, 4), CoveredTo = Cap, // what was asked BEFORE the back-dated trade existed
        });
        await _db.SaveChangesAsync();

        var plan = await CreateSut().PlanAsync(CancellationToken.None);

        plan.PriceHistory.ToFetch.Should().ContainSingle(t => t.Symbol == "AAPL");
    }

    [Fact]
    public async Task PriceHistory_FirstTradeAfterTheCap_IsNotYetAvailable_NotMissing()
    {
        var aapl = MakeAapl();
        _db.Assets.Add(aapl);
        AddTransaction(aapl, Cap.AddDays(3)); // bought after the latest settled close
        await _db.SaveChangesAsync();

        var plan = await CreateSut().PlanAsync(CancellationToken.None);

        plan.PriceHistory.NotYetAvailableSymbols.Should().Contain("AAPL");
        plan.PriceHistory.ToFetch.Should().BeEmpty();
        plan.PriceHistory.RetryPendingSymbols.Should().BeEmpty();
    }

    [Fact]
    public async Task PriceHistory_RecentlyFailed_IsRetryPending_NotAttemptedThisClick()
    {
        var aapl = MakeAapl();
        _db.Assets.Add(aapl);
        var firstTrade = new DateOnly(2026, 7, 4);
        AddTransaction(aapl, firstTrade);
        _db.AssetPriceHistoryStates.Add(new AssetPriceHistoryState
        {
            AssetId = aapl.Id, LastAttemptedAt = Now.AddMinutes(-5), LastRunSuccess = false, LastError = "HTTP 429",
        });
        await _db.SaveChangesAsync();

        // Default FailedRunRetryDelay is 15 minutes — 5 minutes ago is still within it.
        var plan = await CreateSut().PlanAsync(CancellationToken.None);

        plan.PriceHistory.RetryPendingSymbols.Should().Contain("AAPL");
        plan.PriceHistory.ToFetch.Should().BeEmpty();
    }

    [Fact]
    public async Task PriceHistory_OlderFailure_IsMissingAgain_NotRetryPending()
    {
        var aapl = MakeAapl();
        _db.Assets.Add(aapl);
        var firstTrade = new DateOnly(2026, 7, 4);
        AddTransaction(aapl, firstTrade);
        _db.AssetPriceHistoryStates.Add(new AssetPriceHistoryState
        {
            AssetId = aapl.Id, LastAttemptedAt = Now.AddMinutes(-20), LastRunSuccess = false, LastError = "HTTP 429",
        });
        await _db.SaveChangesAsync();

        // Past the default 15-minute FailedRunRetryDelay — eligible to retry this click.
        var plan = await CreateSut().PlanAsync(CancellationToken.None);

        plan.PriceHistory.ToFetch.Should().ContainSingle(t => t.Symbol == "AAPL");
        plan.PriceHistory.RetryPendingSymbols.Should().BeEmpty();
    }

    [Fact]
    public async Task Crypto_NeverAppearsInEitherLeg_EvenWithTransactions()
    {
        var eth = new Asset
        {
            Id = 9, Symbol = "ETH", Name = "Ethereum", AssetClass = AssetClass.Crypto, Currency = "USD",
            QuoteProviderKind = QuoteProviderKind.CoinGecko, ProviderCoinId = "ethereum",
        };
        _db.Assets.Add(eth);
        AddTransaction(eth, new DateOnly(2026, 7, 4));
        await _db.SaveChangesAsync();

        var plan = await CreateSut().PlanAsync(CancellationToken.None);

        plan.PriceHistory.ToFetch.Should().NotContain(t => t.Symbol == "ETH");
        plan.PriceHistory.NotYetAvailableSymbols.Should().NotContain("ETH");
        plan.Dividends.ToFetch.Should().NotContain(t => t.Symbol == "ETH");
    }

    // --- FX: upper-bound-only triggers, deliberately no lower-bound-alone check.

    [Fact]
    public async Task Fx_UpperBoundMissing_TriggersThePair_WithNoAssetItselfBeingFetched()
    {
        var z74 = MakeZ74();
        _db.Assets.Add(z74);
        var firstTrade = new DateOnly(2026, 7, 4);
        AddTransaction(z74, firstTrade);
        // Z74's OWN price history is fully covered — it must not appear in ToFetch — but the stored
        // FX rate is stale relative to Cap.
        _db.PriceHistories.Add(new PriceHistory { AssetId = z74.Id, Date = firstTrade, Close = 4m, Currency = "SGD" });
        _db.PriceHistories.Add(new PriceHistory { AssetId = z74.Id, Date = Cap, Close = 4.1m, Currency = "SGD" });
        _db.FxRates.Add(new FxRate { Date = Cap.AddDays(-5), Base = "USD", Quote = "SGD", Rate = 1.29m });
        await _db.SaveChangesAsync();

        var plan = await CreateSut().PlanAsync(CancellationToken.None);

        plan.PriceHistory.ToFetch.Should().BeEmpty();
        plan.PriceHistory.CurrenciesToFetch.Should().Contain("SGD");
        plan.PriceHistory.MarketsInPlay.Should().Contain(Market.Sgx);
    }

    [Fact]
    public async Task Fx_NoLowerBoundCheckOnItsOwn_AWeekendFirstTradeAloneNeverTriggersThePair()
    {
        // The trap this guards: FX carry-forward already covers a weekend-dated first trade, so
        // checking the lower bound alone would reopen the permanent-gap-every-click trap this
        // feature exists to close. Z74's own price history IS missing (so it lands in ToFetch,
        // which independently triggers the pair via rule (a)) — this test isolates rule (b) alone
        // by giving Z74 state-recorded price-history coverage while FX's stored data starts late.
        var z74 = MakeZ74();
        _db.Assets.Add(z74);
        var firstTrade = new DateOnly(2026, 7, 4);
        AddTransaction(z74, firstTrade);
        _db.AssetPriceHistoryStates.Add(new AssetPriceHistoryState
        {
            AssetId = z74.Id, LastAttemptedAt = Now, LastSuccessAt = Now, LastRunSuccess = true,
            CoveredFrom = firstTrade, CoveredTo = Cap,
        });
        // FX itself is fully covered through Cap — only the (now irrelevant) lower bound predates
        // firstTrade being fetched at all.
        _db.FxRates.Add(new FxRate { Date = Cap, Base = "USD", Quote = "SGD", Rate = 1.29m });
        await _db.SaveChangesAsync();

        var plan = await CreateSut().PlanAsync(CancellationToken.None);

        plan.PriceHistory.ToFetch.Should().BeEmpty();
        plan.PriceHistory.CurrenciesToFetch.Should().BeEmpty();
    }

    // --- FX coverage against `required = min(market cap, fxCap)`, and FxPairBackfillState — the
    // coordinator review fix: comparing stored/state coverage against a market's cap ALONE (without
    // also capping at fxCap) reads FX as missing for hours every SGX evening, since RunAsyncCore
    // never requests/accepts a rate beyond fxCap; and stored data ALONE is not enough either, since
    // Twelve Data does not reliably publish a bar for every calendar day (measured live: gaps on
    // Sundays). `Now` here is 2026-08-10T00:00Z, so ComputeFxCap(Now) = 2026-08-09 — one UTC day
    // behind "today" — regardless of what the market's OWN settled cap resolves to.

    private static readonly DateOnly FxCap = new(2026, 8, 9); // PriceBackfillCapCalculator.ComputeFxCap(Now)

    /// <summary>Seeds Z74 with its OWN price history fully covered (stored data spanning
    /// [firstTrade, marketCap]) so the asset-leg's own ToFetch never includes it — isolating the FX
    /// rule under test from rule (a)'s "an asset of this currency is being fetched" override.</summary>
    private Asset SeedZ74WithPriceHistoryCoveredButNoFxState(DateOnly firstTrade, DateOnly marketCap)
    {
        var z74 = MakeZ74();
        _db.Assets.Add(z74);
        AddTransaction(z74, firstTrade);
        _db.PriceHistories.Add(new PriceHistory { AssetId = z74.Id, Date = firstTrade, Close = 4m, Currency = "SGD" });
        _db.PriceHistories.Add(new PriceHistory { AssetId = z74.Id, Date = marketCap, Close = 4.1m, Currency = "SGD" });
        return z74;
    }

    [Fact]
    public async Task Fx_SgxEveningWindow_MarketCapIsToday_ButRequiredStaysAtFxCap_StoredThroughFxCap_IsNotMissing()
    {
        // SGX's own settled cap is "today" (D) — later than fxCap (D-1) — simulating the window
        // after SGX's close settles but before fxCap itself advances at UTC midnight.
        var marketCapToday = new DateOnly(2026, 8, 10);
        SeedZ74WithPriceHistoryCoveredButNoFxState(new DateOnly(2026, 7, 4), marketCapToday);
        _db.FxRates.Add(new FxRate { Date = FxCap, Base = "USD", Quote = "SGD", Rate = 1.29m });
        await _db.SaveChangesAsync();

        var plan = await CreateSut(calendar: CalendarWithFixedCap(marketCapToday)).PlanAsync(CancellationToken.None);

        plan.PriceHistory.CurrenciesToFetch.Should().BeEmpty("required = min(today, fxCap) = fxCap, and stored FX already reaches fxCap");
        plan.PriceHistory.RetryPendingSymbols.Should().BeEmpty();
    }

    [Fact]
    public async Task Fx_TwelveDataMissingASundayBar_StateCoveredToRecordsTheGapAsAttempted_IsNotMissing()
    {
        var marketCapMonday = new DateOnly(2026, 8, 10);
        var z74 = SeedZ74WithPriceHistoryCoveredButNoFxState(new DateOnly(2026, 7, 4), marketCapMonday);
        // Twelve Data simply has no Sunday (FxCap) bar — stored FX stops at Friday.
        _db.FxRates.Add(new FxRate { Date = new DateOnly(2026, 8, 7), Base = "USD", Quote = "SGD", Rate = 1.29m });
        _db.FxPairBackfillStates.Add(new FxPairBackfillState
        {
            Base = "USD", Quote = "SGD", LastAttemptedAt = Now, LastSuccessAt = Now, LastRunSuccess = true,
            CoveredFrom = new DateOnly(2026, 7, 4), CoveredTo = FxCap,
        });
        await _db.SaveChangesAsync();

        var plan = await CreateSut(calendar: CalendarWithFixedCap(marketCapMonday)).PlanAsync(CancellationToken.None);

        plan.PriceHistory.CurrenciesToFetch.Should().BeEmpty(
            "the provider was already successfully asked through fxCap — it simply had no Sunday bar to return");
        _ = z74; // seeded only to keep Z74's own price-history leg out of ToFetch
    }

    [Fact]
    public async Task Fx_TwelveDataMissingASundayBar_NoStateYet_IsMissingOnce_ThenCoveredAfterASuccessfulFetchRecordsState()
    {
        var marketCapMonday = new DateOnly(2026, 8, 10);
        SeedZ74WithPriceHistoryCoveredButNoFxState(new DateOnly(2026, 7, 4), marketCapMonday);
        _db.FxRates.Add(new FxRate { Date = new DateOnly(2026, 8, 7), Base = "USD", Quote = "SGD", Rate = 1.29m });
        await _db.SaveChangesAsync();

        var sut = CreateSut(calendar: CalendarWithFixedCap(marketCapMonday));

        var firstPlan = await sut.PlanAsync(CancellationToken.None);
        firstPlan.PriceHistory.CurrenciesToFetch.Should().Contain("SGD", "no stored bar and no state reaches fxCap — genuinely missing");

        // Simulate the catch-up leg's own successful fetch recording state (RunAsyncCore's own job,
        // exercised separately in PriceBackfillServiceTests) — CoveredTo is the REQUESTED fxCap, not
        // a date any bar actually landed on.
        _db.FxPairBackfillStates.Add(new FxPairBackfillState
        {
            Base = "USD", Quote = "SGD", LastAttemptedAt = Now, LastSuccessAt = Now, LastRunSuccess = true,
            CoveredFrom = new DateOnly(2026, 7, 4), CoveredTo = FxCap,
        });
        await _db.SaveChangesAsync();

        var secondPlan = await sut.PlanAsync(CancellationToken.None);
        secondPlan.PriceHistory.CurrenciesToFetch.Should().BeEmpty("the successful attempt above now covers fxCap, even with no Sunday bar on file");
    }

    [Fact]
    public async Task Fx_RecentlyFailed_IsRetryPending_ReportedAsUsdSgdInThePriceHistoryLegsRetryPendingSymbols()
    {
        var marketCapMonday = new DateOnly(2026, 8, 10);
        SeedZ74WithPriceHistoryCoveredButNoFxState(new DateOnly(2026, 7, 4), marketCapMonday);
        _db.FxRates.Add(new FxRate { Date = new DateOnly(2026, 8, 7), Base = "USD", Quote = "SGD", Rate = 1.29m });
        _db.FxPairBackfillStates.Add(new FxPairBackfillState
        {
            Base = "USD", Quote = "SGD", LastAttemptedAt = Now.AddMinutes(-5), LastRunSuccess = false, LastError = "HTTP 429",
        });
        await _db.SaveChangesAsync();

        // Default FailedRunRetryDelay is 15 minutes — 5 minutes ago is still within it.
        var plan = await CreateSut(calendar: CalendarWithFixedCap(marketCapMonday)).PlanAsync(CancellationToken.None);

        plan.PriceHistory.RetryPendingSymbols.Should().Contain("USD/SGD");
        plan.PriceHistory.CurrenciesToFetch.Should().BeEmpty();
    }

    [Fact]
    public async Task Fx_AnAssetOfThatCurrencyBeingFetched_ForcesThePair_EvenWithARecentFailure()
    {
        // Rule (a) — "still fetch when an asset of that currency is being fetched" — is a hard
        // requirement, never deferred by retry pacing: FxRateResolver throws without a rate for a
        // date being converted, so a recently-failed FX attempt must not block an asset that
        // genuinely needs converting this run.
        var marketCapMonday = new DateOnly(2026, 8, 10);
        var z74 = MakeZ74();
        _db.Assets.Add(z74);
        AddTransaction(z74, new DateOnly(2026, 7, 4)); // no PriceHistory at all — genuinely missing, forces a fetch
        _db.FxPairBackfillStates.Add(new FxPairBackfillState
        {
            Base = "USD", Quote = "SGD", LastAttemptedAt = Now.AddMinutes(-5), LastRunSuccess = false, LastError = "HTTP 429",
        });
        await _db.SaveChangesAsync();

        var plan = await CreateSut(calendar: CalendarWithFixedCap(marketCapMonday)).PlanAsync(CancellationToken.None);

        plan.PriceHistory.ToFetch.Should().ContainSingle(t => t.Symbol == "Z74");
        plan.PriceHistory.CurrenciesToFetch.Should().Contain("SGD");
        plan.PriceHistory.RetryPendingSymbols.Should().NotContain("USD/SGD");
    }

    // --- Dividends: no state / failed / null CoveredFrom / back-dated, and retry pacing.

    [Fact]
    public async Task Dividends_NoStateRow_IsMissing()
    {
        var aapl = MakeAapl();
        _db.Assets.Add(aapl);
        AddTransaction(aapl, new DateOnly(2026, 7, 4));
        await _db.SaveChangesAsync();

        var plan = await CreateSut().PlanAsync(CancellationToken.None);

        plan.Dividends.ToFetch.Should().ContainSingle(t => t.Symbol == "AAPL");
    }

    [Fact]
    public async Task Dividends_LastRunFailed_IsMissing_UnlessRecentEnoughToBeRetryPending()
    {
        var aapl = MakeAapl();
        _db.Assets.Add(aapl);
        AddTransaction(aapl, new DateOnly(2026, 7, 4));
        _db.AssetDividendStates.Add(new AssetDividendState
        {
            AssetId = aapl.Id, LastAttemptedAt = Now.AddMinutes(-45), LastRunSuccess = false, LastError = "HTTP 500",
            CoveredFrom = new DateOnly(2026, 7, 4),
        });
        await _db.SaveChangesAsync();

        // Default FailedAssetRetryInterval is 30 minutes — 45 minutes ago is past it.
        var plan = await CreateSut().PlanAsync(CancellationToken.None);

        plan.Dividends.ToFetch.Should().ContainSingle(t => t.Symbol == "AAPL");
        plan.Dividends.RetryPendingSymbols.Should().BeEmpty();
    }

    [Fact]
    public async Task Dividends_RecentlyFailed_IsRetryPending()
    {
        var aapl = MakeAapl();
        _db.Assets.Add(aapl);
        AddTransaction(aapl, new DateOnly(2026, 7, 4));
        _db.AssetDividendStates.Add(new AssetDividendState
        {
            AssetId = aapl.Id, LastAttemptedAt = Now.AddMinutes(-5), LastRunSuccess = false, LastError = "HTTP 500",
            CoveredFrom = new DateOnly(2026, 7, 4),
        });
        await _db.SaveChangesAsync();

        var plan = await CreateSut().PlanAsync(CancellationToken.None);

        plan.Dividends.RetryPendingSymbols.Should().Contain("AAPL");
        plan.Dividends.ToFetch.Should().BeEmpty();
    }

    [Fact]
    public async Task Dividends_NullCoveredFrom_PreMigrationRow_IsMissing()
    {
        var aapl = MakeAapl();
        _db.Assets.Add(aapl);
        AddTransaction(aapl, new DateOnly(2026, 7, 4));
        _db.AssetDividendStates.Add(new AssetDividendState
        {
            AssetId = aapl.Id, LastAttemptedAt = Now, LastSuccessAt = Now, LastRunSuccess = true, CoveredFrom = null,
        });
        await _db.SaveChangesAsync();

        var plan = await CreateSut().PlanAsync(CancellationToken.None);

        plan.Dividends.ToFetch.Should().ContainSingle(t => t.Symbol == "AAPL");
    }

    [Fact]
    public async Task Dividends_BackDatedTransactionEarlierThanCoveredFrom_IsMissing()
    {
        var aapl = MakeAapl();
        _db.Assets.Add(aapl);
        AddTransaction(aapl, new DateOnly(2026, 6, 1)); // earlier than CoveredFrom below
        _db.AssetDividendStates.Add(new AssetDividendState
        {
            AssetId = aapl.Id, LastAttemptedAt = Now, LastSuccessAt = Now, LastRunSuccess = true,
            CoveredFrom = new DateOnly(2026, 7, 4),
        });
        await _db.SaveChangesAsync();

        var plan = await CreateSut().PlanAsync(CancellationToken.None);

        plan.Dividends.ToFetch.Should().ContainSingle(t => t.Symbol == "AAPL");
    }

    [Fact]
    public async Task Dividends_CoveredFromAtOrBeforeFirstTrade_AndLastRunSucceeded_IsNotMissing()
    {
        var aapl = MakeAapl();
        _db.Assets.Add(aapl);
        var firstTrade = new DateOnly(2026, 7, 4);
        AddTransaction(aapl, firstTrade);
        _db.AssetDividendStates.Add(new AssetDividendState
        {
            AssetId = aapl.Id, LastAttemptedAt = Now, LastSuccessAt = Now, LastRunSuccess = true,
            CoveredFrom = firstTrade,
        });
        await _db.SaveChangesAsync();

        var plan = await CreateSut().PlanAsync(CancellationToken.None);

        plan.Dividends.ToFetch.Should().BeEmpty();
        plan.Dividends.RetryPendingSymbols.Should().BeEmpty();
    }
}
