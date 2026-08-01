using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Portfolio.Application.Common;
using Portfolio.Application.Services;
using Portfolio.Application.Services.Calculators;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;
using Portfolio.Infrastructure.Persistence;

namespace Portfolio.UnitTests.Services;

/// <summary>
/// <see cref="PortfolioPerformanceService"/> — stocks only. A crypto asset id must come back as a
/// clean validation error, never an empty series that would render as a flat line at zero.
/// </summary>
public sealed class PortfolioPerformanceServiceTests : IDisposable
{
    private readonly PortfolioDbContext _db;
    private readonly PortfolioPerformanceService _sut;

    public PortfolioPerformanceServiceTests()
    {
        var options = new DbContextOptionsBuilder<PortfolioDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new PortfolioDbContext(options);

        _sut = new PortfolioPerformanceService(
            _db,
            new AverageCostCalculator(),
            new PerformanceSeriesBuilder(),
            new AnnualReturnCalculator());
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
    public async Task GetAssetPerformanceAsync_CryptoAsset_ReturnsValidationError_NotEmptySeries()
    {
        var eth = AddAsset(4, "ETH", AssetClass.Crypto, "USD");
        await _db.SaveChangesAsync();

        var result = await _sut.GetAssetPerformanceAsync(eth.Id, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ServiceErrorKind.Validation);
        result.Error.ValidationErrors.Should().ContainKey("assetClass");
    }

    [Fact]
    public async Task GetAssetPerformanceAsync_UnknownAssetId_ReturnsNotFound()
    {
        var result = await _sut.GetAssetPerformanceAsync(999, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ServiceErrorKind.NotFound);
    }

    [Fact]
    public async Task GetAssetPerformanceAsync_StockWithNoTransactions_ReturnsSuccessWithEmptyPoints()
    {
        var aapl = AddAsset(1, "AAPL", AssetClass.Stock, "USD");
        await _db.SaveChangesAsync();

        var result = await _sut.GetAssetPerformanceAsync(aapl.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Points.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAssetPerformanceAsync_UsdStock_BuildsCostBasisAndMarketValueSeries()
    {
        var aapl = AddAsset(1, "AAPL", AssetClass.Stock, "USD");
        _db.Transactions.Add(new Transaction
        {
            AssetId = aapl.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 10m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        _db.PriceHistories.Add(new PriceHistory { AssetId = aapl.Id, Date = new DateOnly(2026, 1, 1), Close = 100m, Currency = "USD" });
        _db.PriceHistories.Add(new PriceHistory { AssetId = aapl.Id, Date = new DateOnly(2026, 1, 2), Close = 105m, Currency = "USD" });
        await _db.SaveChangesAsync();

        var result = await _sut.GetAssetPerformanceAsync(aapl.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Points.Should().HaveCount(2);
        result.Value.Points[0].CostBasisUsd.Should().Be(1000m);
        result.Value.Points[0].MarketValueUsd.Should().Be(1000m);
        result.Value.Points[1].MarketValueUsd.Should().Be(1050m); // 10 * 105
    }

    [Fact]
    public async Task GetAnnualReturnsAsync_ExcludesCrypto_AndComputesTwrForStocksOnly()
    {
        var aapl = AddAsset(1, "AAPL", AssetClass.Stock, "USD");
        _db.Transactions.Add(new Transaction
        {
            AssetId = aapl.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 2),
            Quantity = 10m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        _db.PriceHistories.Add(new PriceHistory { AssetId = aapl.Id, Date = new DateOnly(2026, 1, 2), Close = 100m, Currency = "USD" });
        _db.PriceHistories.Add(new PriceHistory { AssetId = aapl.Id, Date = new DateOnly(2026, 6, 30), Close = 110m, Currency = "USD" });
        _db.PriceHistories.Add(new PriceHistory { AssetId = aapl.Id, Date = new DateOnly(2026, 12, 31), Close = 121m, Currency = "USD" });

        // A crypto asset with its own transactions must not blow up (no PriceHistory ever exists
        // for it, by design) and must not influence the stock-only result at all.
        var eth = AddAsset(4, "ETH", AssetClass.Crypto, "USD");
        _db.Transactions.Add(new Transaction
        {
            AssetId = eth.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 3, 1),
            Quantity = 1m, PricePerUnit = 2000m, Fees = 0m, Currency = "USD",
        });
        await _db.SaveChangesAsync();

        var result = await _sut.GetAnnualReturnsAsync(CancellationToken.None);

        // 1000 -> 1100 -> 1210 with the initial buy itself contributing no return: (1.1*1.1)-1 = 21%.
        result.Years.Should().ContainSingle();
        result.Years[0].Year.Should().Be(2026);
        result.Years[0].TimeWeightedReturnPercent.Should().Be(21.00m);
    }

    [Fact]
    public async Task GetAnnualReturnsAsync_NoStockTransactions_ReturnsEmpty()
    {
        var result = await _sut.GetAnnualReturnsAsync(CancellationToken.None);

        result.Years.Should().BeEmpty();
    }
}
