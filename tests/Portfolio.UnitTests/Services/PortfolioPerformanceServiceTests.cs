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

    /// <summary>
    /// D50, the Z74 shape: A and B are bought on the same day, but B's first stored close only
    /// lands on the next valuation (a holiday on B's own market, or simply no close yet). Prices
    /// are flat throughout, so the true time-weighted return is 0%. Before the fix, B's flow was
    /// dated on its raw trade date — day 1, the series' opening valuation — so it was silently
    /// swallowed as "base of the chain" while day 1's own V never included B, and B's entire $1000
    /// cost re-appeared as pure gain on day 2 (the live case: Z74 bought 2020-07-10, no close until
    /// 2020-07-13, read as +82% for FSLY's ordinary day).
    /// </summary>
    [Fact]
    public async Task GetAnnualReturnsAsync_AssetBoughtBeforeItsFirstClose_DoesNotInventAGain()
    {
        var a = AddAsset(1, "A", AssetClass.Stock, "USD");
        var b = AddAsset(2, "B", AssetClass.Stock, "USD");

        _db.Transactions.Add(new Transaction
        {
            AssetId = a.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 10m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        _db.Transactions.Add(new Transaction
        {
            // Bought the same day as A, but B has no stored close until the next valuation.
            AssetId = b.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 5m, PricePerUnit = 200m, Fees = 0m, Currency = "USD",
        });

        _db.PriceHistories.Add(new PriceHistory { AssetId = a.Id, Date = new DateOnly(2026, 1, 1), Close = 100m, Currency = "USD" });
        _db.PriceHistories.Add(new PriceHistory { AssetId = a.Id, Date = new DateOnly(2026, 1, 2), Close = 100m, Currency = "USD" });
        _db.PriceHistories.Add(new PriceHistory { AssetId = b.Id, Date = new DateOnly(2026, 1, 2), Close = 200m, Currency = "USD" });
        await _db.SaveChangesAsync();

        var result = await _sut.GetAnnualReturnsAsync(CancellationToken.None);

        result.Years.Should().ContainSingle();
        result.Years[0].TimeWeightedReturnPercent.Should().Be(0.00m);
    }

    /// <summary>
    /// D50, the ARVLF shape: B is bought on a date already inside the timeline (not the opening
    /// valuation), but its first stored close only arrives several valuations later, and the span
    /// crosses a calendar-year boundary. Prices are flat throughout, so both years' true return is
    /// 0%. Before the fix, B's flow read as a false loss on its trade date (nothing in V(t) yet
    /// reflects the purchase) and a matching false gain when the close finally appeared — split
    /// across two different years here, so a net-zero compounded total across both years would not
    /// be enough to prove the fix; each year must independently be 0%.
    /// </summary>
    [Fact]
    public async Task GetAnnualReturnsAsync_AssetFirstClosedSeveralValuationsAfterPurchase_IsZeroInBothYears()
    {
        var a = AddAsset(1, "A", AssetClass.Stock, "USD");
        var b = AddAsset(2, "B", AssetClass.Stock, "USD");

        _db.Transactions.Add(new Transaction
        {
            AssetId = a.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2025, 12, 30),
            Quantity = 10m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        _db.Transactions.Add(new Transaction
        {
            // Bought inside the timeline (not day 0), first close arrives after the year rolls over.
            AssetId = b.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2025, 12, 31),
            Quantity = 5m, PricePerUnit = 200m, Fees = 0m, Currency = "USD",
        });

        _db.PriceHistories.Add(new PriceHistory { AssetId = a.Id, Date = new DateOnly(2025, 12, 30), Close = 100m, Currency = "USD" });
        _db.PriceHistories.Add(new PriceHistory { AssetId = a.Id, Date = new DateOnly(2025, 12, 31), Close = 100m, Currency = "USD" });
        _db.PriceHistories.Add(new PriceHistory { AssetId = a.Id, Date = new DateOnly(2026, 1, 2), Close = 100m, Currency = "USD" });
        _db.PriceHistories.Add(new PriceHistory { AssetId = a.Id, Date = new DateOnly(2026, 1, 5), Close = 100m, Currency = "USD" });
        _db.PriceHistories.Add(new PriceHistory { AssetId = b.Id, Date = new DateOnly(2026, 1, 5), Close = 200m, Currency = "USD" });
        await _db.SaveChangesAsync();

        var result = await _sut.GetAnnualReturnsAsync(CancellationToken.None);

        result.Years.Should().HaveCount(2);
        result.Years[0].Year.Should().Be(2025);
        result.Years[0].TimeWeightedReturnPercent.Should().Be(0.00m);
        result.Years[1].Year.Should().Be(2026);
        result.Years[1].TimeWeightedReturnPercent.Should().Be(0.00m);
    }

    /// <summary>
    /// D50: a held position with transactions but zero stored price history at all — the same
    /// "never priced" case <c>GetPortfolioPerformanceAsync</c> reports via
    /// <c>UnchartedSymbols</c> — must not enter the cash-flow series either. Before the fix, its
    /// cost was subtracted from V(t) on whichever valuation its trade date happened to land on
    /// even though the asset itself never contributes a single dollar to V, which reads as a false
    /// loss purely equal to its own cost (this test's unfixed-code result is 4.5%, not the true
    /// 21% AAPL alone would show, computed by hand and confirmed against the pre-fix code path).
    /// </summary>
    [Fact]
    public async Task GetAnnualReturnsAsync_NeverPricedAsset_DoesNotProduceALoss()
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

        var nvda = AddAsset(2, "NVDA", AssetClass.Stock, "USD"); // held, never priced
        _db.Transactions.Add(new Transaction
        {
            AssetId = nvda.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 6, 30),
            Quantity = 3m, PricePerUnit = 50m, Fees = 0m, Currency = "USD",
        });
        await _db.SaveChangesAsync();

        var result = await _sut.GetAnnualReturnsAsync(CancellationToken.None);

        // Same 1000 -> 1100 -> 1210 chain as AAPL alone: 21%, exactly as if NVDA were never held.
        result.Years.Should().ContainSingle();
        result.Years[0].TimeWeightedReturnPercent.Should().Be(21.00m);
    }

    [Fact]
    public async Task GetPortfolioPerformanceAsync_NoStockTransactions_ReturnsEmpty()
    {
        var result = await _sut.GetPortfolioPerformanceAsync(CancellationToken.None);

        result.Points.Should().BeEmpty();
        result.UnchartedSymbols.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPortfolioPerformanceAsync_IgnoresCryptoTransactionsAndHistory()
    {
        var eth = AddAsset(4, "ETH", AssetClass.Crypto, "USD");
        _db.Transactions.Add(new Transaction
        {
            AssetId = eth.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 1m, PricePerUnit = 2000m, Fees = 0m, Currency = "USD",
        });
        await _db.SaveChangesAsync();

        var result = await _sut.GetPortfolioPerformanceAsync(CancellationToken.None);

        result.Points.Should().BeEmpty();
        result.UnchartedSymbols.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPortfolioPerformanceAsync_TwoAssetsDifferentCalendars_CarriesForwardOnMissingDate()
    {
        var aapl = AddAsset(1, "AAPL", AssetClass.Stock, "USD");
        var msft = AddAsset(2, "MSFT", AssetClass.Stock, "USD");

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

        // AAPL has closes on both days; MSFT only on day 1 — day 2 must carry MSFT's day-1 close
        // forward rather than dropping MSFT from the total.
        _db.PriceHistories.Add(new PriceHistory { AssetId = aapl.Id, Date = new DateOnly(2026, 1, 1), Close = 100m, Currency = "USD" });
        _db.PriceHistories.Add(new PriceHistory { AssetId = aapl.Id, Date = new DateOnly(2026, 1, 2), Close = 110m, Currency = "USD" });
        _db.PriceHistories.Add(new PriceHistory { AssetId = msft.Id, Date = new DateOnly(2026, 1, 1), Close = 200m, Currency = "USD" });
        await _db.SaveChangesAsync();

        var result = await _sut.GetPortfolioPerformanceAsync(CancellationToken.None);

        result.Points.Should().HaveCount(2);
        result.Points[0].Date.Should().Be(new DateOnly(2026, 1, 1));
        result.Points[0].CostBasisUsd.Should().Be(1000m + 1000m); // AAPL 10*100 + MSFT 5*200
        result.Points[0].MarketValueUsd.Should().Be(1000m + 1000m);

        result.Points[1].Date.Should().Be(new DateOnly(2026, 1, 2));
        result.Points[1].CostBasisUsd.Should().Be(1000m + 1000m); // cost basis unchanged
        // AAPL 10*110 = 1100, MSFT carried forward at 5*200 = 1000
        result.Points[1].MarketValueUsd.Should().Be(1100m + 1000m);

        result.UnchartedSymbols.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPortfolioPerformanceAsync_InclusionRule_ExcludesAssetBeforeItsFirstClose()
    {
        var aapl = AddAsset(1, "AAPL", AssetClass.Stock, "USD");
        var msft = AddAsset(2, "MSFT", AssetClass.Stock, "USD");

        _db.Transactions.Add(new Transaction
        {
            AssetId = aapl.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 10m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        // MSFT is bought on day 1 too, but its first close only lands on day 2 — before that, MSFT
        // must contribute to neither line, not just to market value.
        _db.Transactions.Add(new Transaction
        {
            AssetId = msft.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 5m, PricePerUnit = 200m, Fees = 0m, Currency = "USD",
        });

        _db.PriceHistories.Add(new PriceHistory { AssetId = aapl.Id, Date = new DateOnly(2026, 1, 1), Close = 100m, Currency = "USD" });
        _db.PriceHistories.Add(new PriceHistory { AssetId = aapl.Id, Date = new DateOnly(2026, 1, 2), Close = 100m, Currency = "USD" });
        _db.PriceHistories.Add(new PriceHistory { AssetId = msft.Id, Date = new DateOnly(2026, 1, 2), Close = 200m, Currency = "USD" });
        await _db.SaveChangesAsync();

        var result = await _sut.GetPortfolioPerformanceAsync(CancellationToken.None);

        result.Points.Should().HaveCount(2);

        // Day 1: only AAPL has a close, so MSFT's cost basis is excluded too, not just its market value.
        result.Points[0].Date.Should().Be(new DateOnly(2026, 1, 1));
        result.Points[0].CostBasisUsd.Should().Be(1000m);
        result.Points[0].MarketValueUsd.Should().Be(1000m);

        // Day 2: MSFT now has its first close, so it joins both lines.
        result.Points[1].Date.Should().Be(new DateOnly(2026, 1, 2));
        result.Points[1].CostBasisUsd.Should().Be(1000m + 1000m);
        result.Points[1].MarketValueUsd.Should().Be(1000m + 1000m);
    }

    [Fact]
    public async Task GetPortfolioPerformanceAsync_HeldAssetWithNoHistory_IsUncharted_ClosedAssetIsNot()
    {
        var aapl = AddAsset(1, "AAPL", AssetClass.Stock, "USD");
        var nvda = AddAsset(2, "NVDA", AssetClass.Stock, "USD"); // held, never priced
        var tsla = AddAsset(3, "TSLA", AssetClass.Stock, "USD"); // bought then fully sold, never priced

        _db.Transactions.Add(new Transaction
        {
            AssetId = aapl.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 10m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        _db.PriceHistories.Add(new PriceHistory { AssetId = aapl.Id, Date = new DateOnly(2026, 1, 1), Close = 100m, Currency = "USD" });

        _db.Transactions.Add(new Transaction
        {
            AssetId = nvda.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 3m, PricePerUnit = 50m, Fees = 0m, Currency = "USD",
        });

        _db.Transactions.Add(new Transaction
        {
            AssetId = tsla.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 2m, PricePerUnit = 300m, Fees = 0m, Currency = "USD",
        });
        _db.Transactions.Add(new Transaction
        {
            AssetId = tsla.Id, Type = TransactionType.Sell, TradeDate = new DateOnly(2026, 1, 2),
            Quantity = 2m, PricePerUnit = 300m, Fees = 0m, Currency = "USD",
        });
        await _db.SaveChangesAsync();

        var result = await _sut.GetPortfolioPerformanceAsync(CancellationToken.None);

        result.UnchartedSymbols.Should().ContainSingle().Which.Should().Be("NVDA");
    }

    [Fact]
    public async Task GetPortfolioPerformanceAsync_SgdAsset_ConvertsAtPerDateFxRate()
    {
        var z74 = AddAsset(5, "Z74", AssetClass.Stock, "SGD");
        _db.Transactions.Add(new Transaction
        {
            AssetId = z74.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 100m, PricePerUnit = 5m, Fees = 0m, Currency = "SGD", // 500 SGD gross
        });
        _db.PriceHistories.Add(new PriceHistory { AssetId = z74.Id, Date = new DateOnly(2026, 1, 1), Close = 5m, Currency = "SGD" });
        _db.PriceHistories.Add(new PriceHistory { AssetId = z74.Id, Date = new DateOnly(2026, 1, 2), Close = 5m, Currency = "SGD" });
        _db.FxRates.Add(new FxRate { Base = "USD", Quote = "SGD", Date = new DateOnly(2026, 1, 1), Rate = 1.25m });
        _db.FxRates.Add(new FxRate { Base = "USD", Quote = "SGD", Date = new DateOnly(2026, 1, 2), Rate = 1.30m }); // rate moved the next day
        await _db.SaveChangesAsync();

        var result = await _sut.GetPortfolioPerformanceAsync(CancellationToken.None);

        result.Points.Should().HaveCount(2);
        result.Points[0].MarketValueUsd.Should().Be(400m); // 500 / 1.25, exact
        result.Points[1].MarketValueUsd.Should().Be(Math.Round(500m / 1.30m, 4)); // day-2 rate, not day-1's
    }
}
