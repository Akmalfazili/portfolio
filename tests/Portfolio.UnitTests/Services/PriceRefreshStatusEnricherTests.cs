using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;
using Portfolio.Infrastructure.Persistence;

namespace Portfolio.UnitTests.Services;

/// <summary>
/// Exercises <see cref="PriceRefreshStatusEnricher"/> against the EF Core InMemory provider — good
/// enough to prove the enrichment's own selection/aggregation rules, but NOT good enough to prove
/// the LINQ actually translates against real SQL Server (<c>BackfillTriggers.Contains</c>, the
/// correlated <c>Max</c> subquery, the nullable <c>Market!.Value</c> projection). See
/// <c>PriceRefreshStatusEnricherIntegrationTests</c> for that half.
/// </summary>
public sealed class PriceRefreshStatusEnricherTests : IDisposable
{
    private readonly PortfolioDbContext _db;
    private readonly ITwelveDataCreditThrottle _creditThrottle = Substitute.For<ITwelveDataCreditThrottle>();
    private readonly PriceRefreshOptions _options = new();

    private static readonly PriceRefreshStatus BareStatus = new(
        LastRefreshedAt: new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero),
        NyseOpen: true,
        SgxOpen: false,
        NextScheduledRunAt: null,
        Sources: []);

    public PriceRefreshStatusEnricherTests()
    {
        _db = new PortfolioDbContext(
            new DbContextOptionsBuilder<PortfolioDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        _creditThrottle.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(new TwelveDataCreditStatus(50, 800, 750));
    }

    public void Dispose() => _db.Dispose();

    private PriceRefreshStatusEnricher CreateSut() =>
        new(_db, _creditThrottle, Options.Create(_options));

    private static Asset StockAsset(int id, string symbol, QuoteProviderKind kind, bool isActive = true) => new()
    {
        Id = id,
        Symbol = symbol,
        Name = symbol,
        AssetClass = AssetClass.Stock,
        Currency = kind == QuoteProviderKind.Yahoo ? "SGD" : "USD",
        QuoteProviderKind = kind,
        ProviderSymbol = symbol,
        IsActive = isActive,
    };

    private static Transaction Buy(int assetId) => new()
    {
        AssetId = assetId,
        Type = TransactionType.Buy,
        TradeDate = new DateOnly(2026, 1, 1),
        Quantity = 1,
        PricePerUnit = 100,
        Fees = 0,
        Currency = "USD",
    };

    private static PriceHistory Close(int assetId, DateOnly date, decimal close = 100m) => new()
    {
        AssetId = assetId,
        Date = date,
        Close = close,
        Currency = "USD",
    };

    // ---- The three pre-existing Twelve Data fields, still populated ----

    [Fact]
    public async Task EnrichAsync_PopulatesTheThreeTwelveDataFields_FromTheCreditLedgerAndActiveAssetCount()
    {
        _db.Assets.Add(StockAsset(1, "AAPL", QuoteProviderKind.TwelveData));
        await _db.SaveChangesAsync();

        var result = await CreateSut().EnrichAsync(BareStatus, CancellationToken.None);

        result.CreditsUsedToday.Should().Be(50);
        result.CreditBudget.Should().Be(800);
        result.EffectiveTwelveDataIntervalSeconds.Should().NotBeNull();
    }

    // ---- Closes: shape and "never attempted" rules ----

    [Fact]
    public async Task EnrichAsync_Closes_HasExactlyOneEntryPerProviderMarket()
    {
        var result = await CreateSut().EnrichAsync(BareStatus, CancellationToken.None);

        result.Closes.Should().NotBeNull();
        result.Closes!.Select(c => c.Market).Should().BeEquivalentTo([Market.Nyse, Market.Sgx]);
    }

    [Fact]
    public async Task EnrichAsync_Closes_AllFieldsAreNull_ForAMarketNeverAttempted_NeverFalseOrZero()
    {
        // No assets, no runs at all — the "never attempted" state.
        var result = await CreateSut().EnrichAsync(BareStatus, CancellationToken.None);

        var nyse = result.Closes!.Single(c => c.Market == Market.Nyse);
        nyse.LatestCloseDate.Should().BeNull();
        nyse.LastAttemptedAt.Should().BeNull();
        nyse.LastSuccessAt.Should().BeNull();
        nyse.LastRunSuccess.Should().BeNull();
        nyse.LastError.Should().BeNull();
    }

    // ---- LatestCloseDate: min-of-max, and the null cases ----

    [Fact]
    public async Task EnrichAsync_Closes_LatestCloseDate_IsTheMinOfEachQualifyingAssetsOwnMaxDate()
    {
        var aapl = StockAsset(1, "AAPL", QuoteProviderKind.TwelveData);
        var msft = StockAsset(2, "MSFT", QuoteProviderKind.TwelveData);
        _db.Assets.AddRange(aapl, msft);
        _db.Transactions.AddRange(Buy(1), Buy(2));
        _db.PriceHistories.AddRange(
            Close(1, new DateOnly(2026, 9, 10)),
            Close(1, new DateOnly(2026, 9, 11)), // AAPL's own max = 9/11
            Close(2, new DateOnly(2026, 9, 9))); // MSFT's own max = 9/9 — lags behind
        await _db.SaveChangesAsync();

        var result = await CreateSut().EnrichAsync(BareStatus, CancellationToken.None);

        // The MIN of the two per-asset maxima, not the MAX — MSFT is the honest floor.
        result.Closes!.Single(c => c.Market == Market.Nyse).LatestCloseDate.Should().Be(new DateOnly(2026, 9, 9));
    }

    [Fact]
    public async Task EnrichAsync_Closes_LatestCloseDate_IsNull_WhenAnyQualifyingAssetHasNoPriceHistoryAtAll()
    {
        var aapl = StockAsset(1, "AAPL", QuoteProviderKind.TwelveData);
        var msft = StockAsset(2, "MSFT", QuoteProviderKind.TwelveData); // never priced
        _db.Assets.AddRange(aapl, msft);
        _db.Transactions.AddRange(Buy(1), Buy(2));
        _db.PriceHistories.Add(Close(1, new DateOnly(2026, 9, 11)));
        await _db.SaveChangesAsync();

        var result = await CreateSut().EnrichAsync(BareStatus, CancellationToken.None);

        result.Closes!.Single(c => c.Market == Market.Nyse).LatestCloseDate.Should().BeNull();
    }

    [Fact]
    public async Task EnrichAsync_Closes_ExcludesAssetsWithNoTransactions()
    {
        // A priced asset with zero transactions must not gate (or contribute to) the min-of-max —
        // it is not actually held.
        var aapl = StockAsset(1, "AAPL", QuoteProviderKind.TwelveData);
        var neverBought = StockAsset(2, "NEVR", QuoteProviderKind.TwelveData);
        _db.Assets.AddRange(aapl, neverBought);
        _db.Transactions.Add(Buy(1));
        // neverBought has no PriceHistory either, which would otherwise force LatestCloseDate null.
        _db.PriceHistories.Add(Close(1, new DateOnly(2026, 9, 11)));
        await _db.SaveChangesAsync();

        var result = await CreateSut().EnrichAsync(BareStatus, CancellationToken.None);

        result.Closes!.Single(c => c.Market == Market.Nyse).LatestCloseDate.Should().Be(new DateOnly(2026, 9, 11));
    }

    [Fact]
    public async Task EnrichAsync_Closes_ExcludesInactiveAssets()
    {
        var aapl = StockAsset(1, "AAPL", QuoteProviderKind.TwelveData);
        var delisted = StockAsset(2, "DEAD", QuoteProviderKind.TwelveData, isActive: false);
        _db.Assets.AddRange(aapl, delisted);
        _db.Transactions.AddRange(Buy(1), Buy(2));
        _db.PriceHistories.Add(Close(1, new DateOnly(2026, 9, 11)));
        // delisted has no PriceHistory — would force null if it counted.
        await _db.SaveChangesAsync();

        var result = await CreateSut().EnrichAsync(BareStatus, CancellationToken.None);

        result.Closes!.Single(c => c.Market == Market.Nyse).LatestCloseDate.Should().Be(new DateOnly(2026, 9, 11));
    }

    [Fact]
    public async Task EnrichAsync_Closes_ExcludesCrypto_WhichHasNoMarketAtAll()
    {
        _db.Assets.Add(new Asset
        {
            Id = 9,
            Symbol = "ETH",
            Name = "Ethereum",
            AssetClass = AssetClass.Crypto,
            Currency = "USD",
            QuoteProviderKind = QuoteProviderKind.CoinGecko,
            ProviderCoinId = "ethereum",
        });
        await _db.SaveChangesAsync();

        var result = await CreateSut().EnrichAsync(BareStatus, CancellationToken.None);

        // Still exactly Nyse + Sgx — crypto never introduces (or is expected under) a third entry.
        result.Closes!.Select(c => c.Market).Should().BeEquivalentTo([Market.Nyse, Market.Sgx]);
        result.Closes!.Should().OnlyContain(c => c.LatestCloseDate == null);
    }

    // ---- RefreshRun filtering: triggers, Market nullability ----

    [Fact]
    public async Task EnrichAsync_Closes_IgnoresLiveQuoteTriggers_ScheduledAndManual()
    {
        _db.RefreshRuns.AddRange(
            new RefreshRun
            {
                Trigger = RefreshTrigger.Scheduled,
                Market = Market.Nyse,
                CompletedAt = new DateTimeOffset(2026, 9, 11, 20, 0, 0, TimeSpan.Zero),
                Success = true,
            },
            new RefreshRun
            {
                Trigger = RefreshTrigger.Manual,
                Market = Market.Nyse,
                CompletedAt = new DateTimeOffset(2026, 9, 11, 21, 0, 0, TimeSpan.Zero),
                Success = true,
            });
        await _db.SaveChangesAsync();

        var result = await CreateSut().EnrichAsync(BareStatus, CancellationToken.None);

        var nyse = result.Closes!.Single(c => c.Market == Market.Nyse);
        nyse.LastAttemptedAt.Should().BeNull();
        nyse.LastSuccessAt.Should().BeNull();
        nyse.LastRunSuccess.Should().BeNull();
    }

    [Fact]
    public async Task EnrichAsync_Closes_IgnoresPreD47Rows_WhereMarketIsNull()
    {
        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.BackfillScheduled,
            Market = null, // legacy, pre-D47
            CompletedAt = new DateTimeOffset(2026, 9, 1, 20, 0, 0, TimeSpan.Zero),
            Success = true,
        });
        await _db.SaveChangesAsync();

        var result = await CreateSut().EnrichAsync(BareStatus, CancellationToken.None);

        result.Closes!.Should().OnlyContain(c =>
            c.LastAttemptedAt == null && c.LastSuccessAt == null && c.LastRunSuccess == null);
    }

    [Fact]
    public async Task EnrichAsync_Closes_IgnoresIncompleteRuns_WhereCompletedAtIsNull()
    {
        _db.RefreshRuns.Add(new RefreshRun
        {
            Trigger = RefreshTrigger.BackfillScheduled,
            Market = Market.Sgx,
            StartedAt = new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero),
            CompletedAt = null, // still in flight
            Success = false,
        });
        await _db.SaveChangesAsync();

        var result = await CreateSut().EnrichAsync(BareStatus, CancellationToken.None);

        result.Closes!.Single(c => c.Market == Market.Sgx).LastAttemptedAt.Should().BeNull();
    }

    [Fact]
    public async Task EnrichAsync_Closes_LastAttemptedAt_IsTheMostRecentCompletedBackfillRun_BothScheduledAndManual()
    {
        _db.RefreshRuns.AddRange(
            new RefreshRun
            {
                Trigger = RefreshTrigger.BackfillScheduled,
                Market = Market.Sgx,
                CompletedAt = new DateTimeOffset(2026, 9, 11, 14, 48, 0, TimeSpan.Zero),
                Success = true,
            },
            new RefreshRun
            {
                Trigger = RefreshTrigger.BackfillManual,
                Market = Market.Sgx,
                CompletedAt = new DateTimeOffset(2026, 9, 13, 17, 1, 0, TimeSpan.Zero),
                Success = true,
                SymbolsRefreshed = 1,
            });
        await _db.SaveChangesAsync();

        var result = await CreateSut().EnrichAsync(BareStatus, CancellationToken.None);

        var sgx = result.Closes!.Single(c => c.Market == Market.Sgx);
        sgx.LastAttemptedAt.Should().Be(new DateTimeOffset(2026, 9, 13, 17, 1, 0, TimeSpan.Zero));
        sgx.LastSuccessAt.Should().Be(new DateTimeOffset(2026, 9, 13, 17, 1, 0, TimeSpan.Zero));
        sgx.LastRunSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task EnrichAsync_Closes_LastSuccessAt_SurvivesALaterFailure()
    {
        var succeededAt = new DateTimeOffset(2026, 9, 11, 14, 48, 0, TimeSpan.Zero);
        var failedAt = new DateTimeOffset(2026, 9, 12, 14, 46, 0, TimeSpan.Zero);
        _db.RefreshRuns.AddRange(
            new RefreshRun
            {
                Trigger = RefreshTrigger.BackfillScheduled,
                Market = Market.Nyse,
                CompletedAt = succeededAt,
                Success = true,
                SymbolsRefreshed = 21,
            },
            new RefreshRun
            {
                Trigger = RefreshTrigger.BackfillScheduled,
                Market = Market.Nyse,
                CompletedAt = failedAt,
                Success = false,
                ErrorMessage = "Twelve Data request failed",
                SymbolsRefreshed = 0,
            });
        await _db.SaveChangesAsync();

        var result = await CreateSut().EnrichAsync(BareStatus, CancellationToken.None);

        var nyse = result.Closes!.Single(c => c.Market == Market.Nyse);
        nyse.LastAttemptedAt.Should().Be(failedAt, "the most recent completed run, win or lose");
        nyse.LastRunSuccess.Should().BeFalse();
        nyse.LastError.Should().Be("Twelve Data request failed");
        nyse.LastSuccessAt.Should().Be(succeededAt, "a later failure must never blank out the last real success");
    }

    [Fact]
    public async Task EnrichAsync_Closes_KeepsEachMarketIndependent()
    {
        _db.RefreshRuns.AddRange(
            new RefreshRun
            {
                Trigger = RefreshTrigger.BackfillScheduled,
                Market = Market.Nyse,
                CompletedAt = new DateTimeOffset(2026, 9, 11, 20, 0, 0, TimeSpan.Zero),
                Success = true,
            },
            new RefreshRun
            {
                Trigger = RefreshTrigger.BackfillScheduled,
                Market = Market.Sgx,
                CompletedAt = new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero),
                Success = false,
                ErrorMessage = "Yahoo request failed",
            });
        await _db.SaveChangesAsync();

        var result = await CreateSut().EnrichAsync(BareStatus, CancellationToken.None);

        result.Closes!.Single(c => c.Market == Market.Nyse).LastRunSuccess.Should().BeTrue();
        result.Closes!.Single(c => c.Market == Market.Sgx).LastRunSuccess.Should().BeFalse();
    }
}
