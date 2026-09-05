using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Portfolio.Application.Common;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services;
using Portfolio.Application.Services.Calculators;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;
using Portfolio.Infrastructure.Persistence;
using Portfolio.UnitTests.TestSupport;

namespace Portfolio.UnitTests.Services;

/// <summary>
/// <see cref="DividendService"/> — coverage-status classification (D10/D26/D33/D35/D38's "not
/// attempted vs attempted-and-failed vs genuinely nothing" rule applied to dividends), the
/// trailing-12-month window edge, SGD→USD conversion at the ex-date's own rate, and the
/// stocks-only rejection mirrored from <see cref="IPortfolioPerformanceService"/>.
/// </summary>
public sealed class DividendServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2027, 3, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly PortfolioDbContext _db;
    private readonly DividendService _sut;

    public DividendServiceTests()
    {
        var options = new DbContextOptionsBuilder<PortfolioDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new PortfolioDbContext(options);
        _sut = new DividendService(_db, new DividendIncomeCalculator(), new FixedTimeProvider(Now));
    }

    public void Dispose() => _db.Dispose();

    private Asset AddAsset(int id, string symbol, AssetClass assetClass, string currency) => new()
    {
        Id = id,
        Symbol = symbol,
        Name = symbol,
        AssetClass = assetClass,
        Currency = currency,
        QuoteProviderKind = assetClass == AssetClass.Stock ? QuoteProviderKind.TwelveData : QuoteProviderKind.CoinGecko,
    };

    [Fact]
    public async Task GetAssetDividendHistoryAsync_CryptoAssetId_IsRejected_NotAnEmptyList()
    {
        var eth = AddAsset(1, "ETH", AssetClass.Crypto, "USD");
        _db.Assets.Add(eth);
        await _db.SaveChangesAsync();

        var result = await _sut.GetAssetDividendHistoryAsync(eth.Id, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ServiceErrorKind.Validation);
    }

    [Fact]
    public async Task GetAssetDividendHistoryAsync_UnknownAssetId_ReturnsNotFound()
    {
        var result = await _sut.GetAssetDividendHistoryAsync(999, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ServiceErrorKind.NotFound);
    }

    [Fact]
    public async Task GetAssetDividendHistoryAsync_NoAssetDividendState_IsNotYetFetched_WithNullTotals()
    {
        var aapl = AddAsset(1, "AAPL", AssetClass.Stock, "USD");
        _db.Assets.Add(aapl);
        _db.Transactions.Add(new Transaction
        {
            AssetId = aapl.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 10m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        await _db.SaveChangesAsync();

        var result = await _sut.GetAssetDividendHistoryAsync(aapl.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var dto = result.Value!;
        dto.CoverageStatus.Should().Be(DividendCoverageStatus.NotYetFetched);
        dto.Trailing12MonthIncomeUsd.Should().BeNull();
        dto.AllTimeIncomeUsd.Should().BeNull();
    }

    [Fact]
    public async Task GetAssetDividendHistoryAsync_LastAttemptFailed_IsFetchFailed()
    {
        var aapl = AddAsset(1, "AAPL", AssetClass.Stock, "USD");
        _db.Assets.Add(aapl);
        _db.Transactions.Add(new Transaction
        {
            AssetId = aapl.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 10m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        _db.AssetDividendStates.Add(new AssetDividendState
        {
            AssetId = aapl.Id,
            LastAttemptedAt = Now.AddDays(-1),
            LastSuccessAt = null,
            LastRunSuccess = false,
            LastError = "Yahoo returned HTTP 500.",
        });
        await _db.SaveChangesAsync();

        var result = await _sut.GetAssetDividendHistoryAsync(aapl.Id, CancellationToken.None);

        result.Value!.CoverageStatus.Should().Be(DividendCoverageStatus.FetchFailed);
    }

    [Fact]
    public async Task GetAssetDividendHistoryAsync_LastAttemptSucceeded_IsCovered_EvenWithZeroEvents()
    {
        // A real non-dividend-paying stock: the backfill succeeded and genuinely found nothing.
        var msft = AddAsset(2, "MSFT", AssetClass.Stock, "USD");
        _db.Assets.Add(msft);
        _db.Transactions.Add(new Transaction
        {
            AssetId = msft.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 10m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        _db.AssetDividendStates.Add(new AssetDividendState
        {
            AssetId = msft.Id, LastAttemptedAt = Now.AddDays(-1), LastSuccessAt = Now.AddDays(-1), LastRunSuccess = true,
        });
        await _db.SaveChangesAsync();

        var result = await _sut.GetAssetDividendHistoryAsync(msft.Id, CancellationToken.None);

        result.Value!.CoverageStatus.Should().Be(DividendCoverageStatus.Covered);
        result.Value!.Trailing12MonthIncomeUsd.Should().Be(0m);
        result.Value!.AllTimeIncomeUsd.Should().Be(0m);
    }

    [Fact]
    public async Task GetAssetDividendHistoryAsync_ExDateExactlyOneYearAgo_IsIncludedInTrailing12Months()
    {
        var aapl = AddAsset(1, "AAPL", AssetClass.Stock, "USD");
        _db.Assets.Add(aapl);
        _db.Transactions.Add(new Transaction
        {
            AssetId = aapl.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2020, 1, 1),
            Quantity = 10m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        var oneYearAgo = ReportingClock.DateFor(Now).AddYears(-1);
        _db.DividendEvents.Add(new DividendEvent { AssetId = aapl.Id, ExDate = oneYearAgo, AmountPerShare = 1m, Currency = "USD" });
        _db.AssetDividendStates.Add(new AssetDividendState { AssetId = aapl.Id, LastAttemptedAt = Now, LastSuccessAt = Now, LastRunSuccess = true });
        await _db.SaveChangesAsync();

        var result = await _sut.GetAssetDividendHistoryAsync(aapl.Id, CancellationToken.None);

        result.Value!.Trailing12MonthIncomeUsd.Should().Be(10m); // included, boundary is inclusive
        result.Value!.AllTimeIncomeUsd.Should().Be(10m);
    }

    [Fact]
    public async Task GetAssetDividendHistoryAsync_ExDateOneDayOlderThanOneYearAgo_IsExcludedFromTrailing12Months()
    {
        var aapl = AddAsset(1, "AAPL", AssetClass.Stock, "USD");
        _db.Assets.Add(aapl);
        _db.Transactions.Add(new Transaction
        {
            AssetId = aapl.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2020, 1, 1),
            Quantity = 10m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        var justOutsideWindow = ReportingClock.DateFor(Now).AddYears(-1).AddDays(-1);
        _db.DividendEvents.Add(new DividendEvent { AssetId = aapl.Id, ExDate = justOutsideWindow, AmountPerShare = 1m, Currency = "USD" });
        _db.AssetDividendStates.Add(new AssetDividendState { AssetId = aapl.Id, LastAttemptedAt = Now, LastSuccessAt = Now, LastRunSuccess = true });
        await _db.SaveChangesAsync();

        var result = await _sut.GetAssetDividendHistoryAsync(aapl.Id, CancellationToken.None);

        result.Value!.Trailing12MonthIncomeUsd.Should().Be(0m); // outside the trailing window
        result.Value!.AllTimeIncomeUsd.Should().Be(10m); // still counted all-time
    }

    [Fact]
    public async Task GetAssetDividendHistoryAsync_AtSgtMidnightBoundary_WindowIsAnchoredToTheNewSgtDay()
    {
        // 2026-09-03T16:30:00Z = 2026-09-04 00:30 SGT. The trailing-12-month window must anchor to
        // 2026-09-04 (the SGT "today"), not 2026-09-03 (the still-current UTC day) — an ex-date of
        // exactly 2025-09-04 is inside the window under the SGT anchor and would be excluded under
        // the old UTC one.
        var boundaryTimeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 9, 3, 16, 30, 0, TimeSpan.Zero));
        var sut = new DividendService(_db, new DividendIncomeCalculator(), boundaryTimeProvider);

        var aapl = AddAsset(1, "AAPL", AssetClass.Stock, "USD");
        _db.Assets.Add(aapl);
        _db.Transactions.Add(new Transaction
        {
            AssetId = aapl.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2020, 1, 1),
            Quantity = 10m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        var exactlyOneYearBeforeTheNewSgtDay = new DateOnly(2025, 9, 4);
        _db.DividendEvents.Add(new DividendEvent
        {
            AssetId = aapl.Id, ExDate = exactlyOneYearBeforeTheNewSgtDay, AmountPerShare = 1m, Currency = "USD",
        });
        _db.AssetDividendStates.Add(new AssetDividendState
        {
            AssetId = aapl.Id, LastAttemptedAt = boundaryTimeProvider.GetUtcNow(),
            LastSuccessAt = boundaryTimeProvider.GetUtcNow(), LastRunSuccess = true,
        });
        await _db.SaveChangesAsync();

        var result = await sut.GetAssetDividendHistoryAsync(aapl.Id, CancellationToken.None);

        result.Value!.Trailing12MonthIncomeUsd.Should().Be(10m);
    }

    [Fact]
    public async Task GetAssetDividendHistoryAsync_SgdAsset_ConvertsAtTheExDatesOwnHistoricalRate()
    {
        var z74 = AddAsset(3, "Z74", AssetClass.Stock, "SGD");
        _db.Assets.Add(z74);
        _db.Transactions.Add(new Transaction
        {
            AssetId = z74.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 100m, PricePerUnit = 4m, Fees = 0m, Currency = "SGD",
        });
        var exDate = new DateOnly(2026, 6, 1);
        _db.DividendEvents.Add(new DividendEvent { AssetId = z74.Id, ExDate = exDate, AmountPerShare = 0.103m, Currency = "SGD" });
        // Two stored rates - the resolver must pick the one at-or-before the ex-date, not today's.
        _db.FxRates.Add(new FxRate { Date = new DateOnly(2026, 1, 1), Base = "USD", Quote = "SGD", Rate = 1.25m });
        _db.FxRates.Add(new FxRate { Date = new DateOnly(2027, 1, 1), Base = "USD", Quote = "SGD", Rate = 1.40m }); // after the ex-date - must not be used
        _db.AssetDividendStates.Add(new AssetDividendState { AssetId = z74.Id, LastAttemptedAt = Now, LastSuccessAt = Now, LastRunSuccess = true });
        await _db.SaveChangesAsync();

        var result = await _sut.GetAssetDividendHistoryAsync(z74.Id, CancellationToken.None);

        // 100 units * 0.103 SGD / 1.25 = 8.24 USD
        result.Value!.AllTimeIncomeUsd.Should().Be(8.24m);
        result.Value!.Payments.Should().ContainSingle().Which.IncomeUsd.Should().Be(8.24m);
    }

    [Fact]
    public async Task GetSummariesAsync_CryptoIdsNotPassedIn_AreSimplyAbsentFromTheResult()
    {
        var summaries = await _sut.GetSummariesAsync([], CancellationToken.None);

        summaries.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSummariesAsync_EnrichesEachStockAssetIndependently()
    {
        var aapl = AddAsset(1, "AAPL", AssetClass.Stock, "USD");
        var msft = AddAsset(2, "MSFT", AssetClass.Stock, "USD");
        _db.Assets.AddRange(aapl, msft);
        _db.Transactions.Add(new Transaction
        {
            AssetId = aapl.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 10m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        _db.Transactions.Add(new Transaction
        {
            AssetId = msft.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 5m, PricePerUnit = 200m, Fees = 0m, Currency = "USD",
        });
        _db.DividendEvents.Add(new DividendEvent { AssetId = aapl.Id, ExDate = new DateOnly(2026, 6, 1), AmountPerShare = 1m, Currency = "USD" });
        _db.AssetDividendStates.Add(new AssetDividendState { AssetId = aapl.Id, LastAttemptedAt = Now, LastSuccessAt = Now, LastRunSuccess = true });
        // MSFT never fetched at all.
        await _db.SaveChangesAsync();

        var summaries = await _sut.GetSummariesAsync([aapl.Id, msft.Id], CancellationToken.None);

        summaries[aapl.Id].CoverageStatus.Should().Be(DividendCoverageStatus.Covered);
        summaries[aapl.Id].AllTimeIncomeUsd.Should().Be(10m);
        summaries[msft.Id].CoverageStatus.Should().Be(DividendCoverageStatus.NotYetFetched);
        summaries[msft.Id].AllTimeIncomeUsd.Should().BeNull();
    }
}
