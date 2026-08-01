using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Portfolio.Application.Services;
using Portfolio.Application.Services.Calculators;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;
using Portfolio.Infrastructure.Persistence;
using Portfolio.UnitTests.TestSupport;

namespace Portfolio.UnitTests.Services;

/// <summary>
/// <see cref="PortfolioSummaryService"/> — cost basis, unrealised/realised P&amp;L and allocation
/// for one asset class, including the multi-currency (SGD) conversion at each transaction's own
/// historical FX rate rather than today's.
/// </summary>
public sealed class PortfolioSummaryServiceTests : IDisposable
{
    private readonly PortfolioDbContext _db;
    private readonly PortfolioSummaryService _sut;
    private readonly FixedTimeProvider _timeProvider;

    public PortfolioSummaryServiceTests()
    {
        var options = new DbContextOptionsBuilder<PortfolioDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new PortfolioDbContext(options);

        // "Today" for market-value conversion, deliberately after the only stored FX rate so the
        // carry-forward path is exercised for the live quote too, not only historical legs.
        _timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
        _sut = new PortfolioSummaryService(_db, _timeProvider, new AverageCostCalculator());
    }

    public void Dispose() => _db.Dispose();

    private Asset AddAsset(int id, string symbol, AssetClass assetClass, string currency)
    {
        var asset = new Asset
        {
            Id = id,
            Symbol = symbol,
            Name = symbol,
            AssetClass = assetClass,
            Currency = currency,
            QuoteProviderKind = assetClass == AssetClass.Stock ? QuoteProviderKind.TwelveData : QuoteProviderKind.CoinGecko,
        };
        _db.Assets.Add(asset);
        return asset;
    }

    [Fact]
    public async Task GetSummaryAsync_ComputesCostBasisMarketValueAndUnrealizedPnl_ForUsdAsset()
    {
        var aapl = AddAsset(1, "AAPL", AssetClass.Stock, "USD");
        _db.Transactions.Add(new Transaction
        {
            AssetId = aapl.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 10m, PricePerUnit = 100m, Fees = 5m, Currency = "USD",
        });
        _db.PriceQuotes.Add(new PriceQuote { AssetId = aapl.Id, Price = 120m, Currency = "USD", AsOf = DateTimeOffset.UtcNow });
        await _db.SaveChangesAsync();

        var summary = await _sut.GetSummaryAsync(AssetClass.Stock, CancellationToken.None);

        summary.Holdings.Should().ContainSingle();
        var holding = summary.Holdings[0];
        holding.QuantityHeld.Should().Be(10m);
        holding.CostBasisUsd.Should().Be(1005m); // 1000 + 5 fee
        holding.MarketValueUsd.Should().Be(1200m); // 10 * 120
        holding.UnrealizedPnlUsd.Should().Be(195m); // 1200 - 1005
        holding.RealizedPnlUsd.Should().Be(0m);

        summary.TotalCostBasisUsd.Should().Be(1005m);
        summary.TotalMarketValueUsd.Should().Be(1200m);
        summary.TotalUnrealizedPnlUsd.Should().Be(195m);
    }

    [Fact]
    public async Task GetSummaryAsync_MultiCurrencySgdHolding_ConvertsAtHistoricalRate_NotTodaysRate()
    {
        var z74 = AddAsset(3, "Z74", AssetClass.Stock, "SGD");
        _db.Transactions.Add(new Transaction
        {
            AssetId = z74.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 100m, PricePerUnit = 4m, Fees = 1m, Currency = "SGD",
        });
        _db.PriceQuotes.Add(new PriceQuote { AssetId = z74.Id, Price = 4.20m, Currency = "SGD", AsOf = DateTimeOffset.UtcNow });
        // Only one stored FX rate, dated at the trade — "today" (2026-02-01, per _timeProvider)
        // has no rate of its own, so the live market value must carry this one forward too.
        _db.FxRates.Add(new FxRate { Date = new DateOnly(2026, 1, 1), Base = "USD", Quote = "SGD", Rate = 1.25m });
        await _db.SaveChangesAsync();

        var summary = await _sut.GetSummaryAsync(AssetClass.Stock, CancellationToken.None);

        var holding = summary.Holdings.Should().ContainSingle().Subject;
        // (100 * 4 + 1) / 1.25 = 401 / 1.25 = 320.8
        holding.CostBasisUsd.Should().Be(320.8m);
        // 4.20 / 1.25 = 3.36; 100 * 3.36 = 336
        holding.CurrentPriceUsd.Should().Be(3.36m);
        holding.MarketValueUsd.Should().Be(336m);
        holding.UnrealizedPnlUsd.Should().Be(15.2m); // 336 - 320.8
    }

    [Fact]
    public async Task GetSummaryAsync_FullySoldPosition_StillCountsRealizedPnl_WithZeroMarketValue()
    {
        var aapl = AddAsset(1, "AAPL", AssetClass.Stock, "USD");
        _db.Transactions.Add(new Transaction
        {
            AssetId = aapl.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 10m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        _db.Transactions.Add(new Transaction
        {
            AssetId = aapl.Id, Type = TransactionType.Sell, TradeDate = new DateOnly(2026, 1, 15),
            Quantity = 10m, PricePerUnit = 150m, Fees = 0m, Currency = "USD",
        });
        await _db.SaveChangesAsync();

        var summary = await _sut.GetSummaryAsync(AssetClass.Stock, CancellationToken.None);

        var holding = summary.Holdings.Should().ContainSingle().Subject;
        holding.QuantityHeld.Should().Be(0m);
        holding.CostBasisUsd.Should().Be(0m);
        holding.MarketValueUsd.Should().Be(0m);
        holding.RealizedPnlUsd.Should().Be(500m); // (150-100) * 10
        summary.TotalRealizedPnlUsd.Should().Be(500m);
    }

    [Fact]
    public async Task GetSummaryAsync_AssetWithNoTransactions_IsExcluded()
    {
        AddAsset(2, "MSFT", AssetClass.Stock, "USD");
        await _db.SaveChangesAsync();

        var summary = await _sut.GetSummaryAsync(AssetClass.Stock, CancellationToken.None);

        summary.Holdings.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSummaryAsync_NeverAggregatesAcrossAssetClasses()
    {
        var aapl = AddAsset(1, "AAPL", AssetClass.Stock, "USD");
        var eth = AddAsset(4, "ETH", AssetClass.Crypto, "USD");
        _db.Transactions.Add(new Transaction
        {
            AssetId = aapl.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 1m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        _db.Transactions.Add(new Transaction
        {
            AssetId = eth.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 1m, PricePerUnit = 2000m, Fees = 0m, Currency = "USD",
        });
        await _db.SaveChangesAsync();

        var stockSummary = await _sut.GetSummaryAsync(AssetClass.Stock, CancellationToken.None);
        var cryptoSummary = await _sut.GetSummaryAsync(AssetClass.Crypto, CancellationToken.None);

        stockSummary.Holdings.Should().ContainSingle(h => h.Symbol == "AAPL");
        cryptoSummary.Holdings.Should().ContainSingle(h => h.Symbol == "ETH");
    }

    [Fact]
    public async Task GetAllocationAsync_ComputesPercentagesOfTotal_AndExcludesClosedPositions()
    {
        var aapl = AddAsset(1, "AAPL", AssetClass.Stock, "USD");
        var msft = AddAsset(2, "MSFT", AssetClass.Stock, "USD");
        _db.Transactions.Add(new Transaction
        {
            AssetId = aapl.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 10m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        _db.PriceQuotes.Add(new PriceQuote { AssetId = aapl.Id, Price = 100m, Currency = "USD", AsOf = DateTimeOffset.UtcNow });

        // MSFT bought and fully sold — must not appear in the allocation pie at all.
        _db.Transactions.Add(new Transaction
        {
            AssetId = msft.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 5m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        _db.Transactions.Add(new Transaction
        {
            AssetId = msft.Id, Type = TransactionType.Sell, TradeDate = new DateOnly(2026, 1, 5),
            Quantity = 5m, PricePerUnit = 110m, Fees = 0m, Currency = "USD",
        });
        await _db.SaveChangesAsync();

        var allocation = await _sut.GetAllocationAsync(AssetClass.Stock, CancellationToken.None);

        allocation.Items.Should().ContainSingle(i => i.Symbol == "AAPL");
        allocation.Items[0].PercentageOfTotal.Should().Be(100m);
        allocation.TotalMarketValueUsd.Should().Be(1000m);
    }

    [Fact]
    public async Task GetSummaryAsync_AssetWithNoQuoteYet_ContributesZeroMarketValue_NotAnError()
    {
        var aapl = AddAsset(1, "AAPL", AssetClass.Stock, "USD");
        _db.Transactions.Add(new Transaction
        {
            AssetId = aapl.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 10m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        await _db.SaveChangesAsync();

        var summary = await _sut.GetSummaryAsync(AssetClass.Stock, CancellationToken.None);

        var holding = summary.Holdings.Should().ContainSingle().Subject;
        holding.CurrentPriceUsd.Should().BeNull();
        holding.MarketValueUsd.Should().Be(0m);
    }
}
