using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services;
using Portfolio.Application.Services.Calculators;
using Portfolio.Application.Services.Calendar;
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
    /// <summary>
    /// "Now" for every test here: Monday 2026-02-02, 15:00 UTC — which is 10:00 ET, inside the
    /// NYSE session. Deliberately after the only stored FX rate, so the carry-forward path is
    /// exercised for the live quote too and not only for historical legs.
    ///
    /// <para>D4 made the choice of instant load-bearing. A stored quote now only counts as
    /// <see cref="PriceSource.Live"/> if it belongs to the current trading session, so a fixture
    /// frozen on a Sunday (as this one was) would classify every quote below as a stale close and
    /// quietly stop testing the live path at all.</para>
    /// </summary>
    private static readonly DateTimeOffset NyseSessionNow = new(2026, 2, 2, 15, 0, 0, TimeSpan.Zero);

    private readonly PortfolioDbContext _db;
    private readonly PortfolioSummaryService _sut;
    private readonly FixedTimeProvider _timeProvider;

    public PortfolioSummaryServiceTests()
    {
        var options = new DbContextOptionsBuilder<PortfolioDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new PortfolioDbContext(options);

        _timeProvider = new FixedTimeProvider(NyseSessionNow);
        // The real calendar, not a stub: D4's classification depends on genuine exchange-local
        // date arithmetic and real session hours, and a stub would only assert this test's own
        // assumptions back at it.
        _sut = new PortfolioSummaryService(_db, _timeProvider, new AverageCostCalculator(), new MarketCalendar());
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
        _db.PriceQuotes.Add(new PriceQuote { AssetId = aapl.Id, Price = 120m, Currency = "USD", AsOf = NyseSessionNow });
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
        _db.PriceQuotes.Add(new PriceQuote { AssetId = z74.Id, Price = 4.20m, Currency = "SGD", AsOf = NyseSessionNow });
        // Only one stored FX rate, dated at the trade — "today" (2026-02-02, per _timeProvider)
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
        _db.PriceQuotes.Add(new PriceQuote { AssetId = aapl.Id, Price = 100m, Currency = "USD", AsOf = NyseSessionNow });

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
        // D17: no live quote AND no stored close at all — this is the genuinely unpriced case, so
        // it must be surfaced on the summary rather than silently folded into a $0 total.
        holding.PriceSource.Should().BeNull();
        summary.UnpricedHoldingsCount.Should().Be(1);
    }

    /// <summary>
    /// D17 residual. The summary tiles have carried an unpriced caveat since D17, but the
    /// allocation payload had no equivalent, so an unpriced holding arrived as a bare
    /// <c>marketValueUsd: 0 / percentageOfTotal: 0</c> — indistinguishable on the wire from a
    /// holding that really is worth nothing.
    /// </summary>
    [Fact]
    public async Task GetAllocationAsync_UnpricedHolding_IsFlagged_NotSilently0Percent()
    {
        var aapl = AddAsset(1, "AAPL", AssetClass.Stock, "USD");
        var msft = AddAsset(2, "MSFT", AssetClass.Stock, "USD");

        // AAPL: real cost basis, no quote and no stored close at all — genuinely unpriced.
        _db.Transactions.Add(new Transaction
        {
            AssetId = aapl.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 10m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });

        // MSFT: priced normally, so the pie still has something real in it.
        _db.Transactions.Add(new Transaction
        {
            AssetId = msft.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 5m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        _db.PriceQuotes.Add(new PriceQuote { AssetId = msft.Id, Price = 200m, Currency = "USD", AsOf = NyseSessionNow });
        await _db.SaveChangesAsync();

        var allocation = await _sut.GetAllocationAsync(AssetClass.Stock, CancellationToken.None);

        var unpriced = allocation.Items.Should().ContainSingle(i => i.Symbol == "AAPL").Subject;
        unpriced.HasPrice.Should().BeFalse();
        unpriced.MarketValueUsd.Should().Be(0m, "nothing is guessed — the value is simply unknown");

        allocation.Items.Should().ContainSingle(i => i.Symbol == "MSFT")
            .Which.HasPrice.Should().BeTrue();

        allocation.UnpricedHoldingsCount.Should().Be(
            1,
            "a caller fetching only this endpoint must be able to tell the pie is partial");
    }

    [Fact]
    public async Task GetSummaryAsync_NoLiveQuote_FallsBackToLastStoredClose_TaggedAsCloseWithItsOwnDate()
    {
        // D20: AAPL has a stored close (from a backfill) but no live PriceQuote — the market-closed
        // case D20 exists for. The fallback must use the close's own date, not "today" (2026-02-02
        // per _timeProvider), both for the reported priceAsOf and for which historical value to
        // report to the caller.
        var aapl = AddAsset(1, "AAPL", AssetClass.Stock, "USD");
        _db.Transactions.Add(new Transaction
        {
            AssetId = aapl.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 10m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        _db.PriceHistories.Add(new PriceHistory
        {
            AssetId = aapl.Id, Date = new DateOnly(2026, 1, 30), Close = 150m, Currency = "USD",
        });
        await _db.SaveChangesAsync();

        var summary = await _sut.GetSummaryAsync(AssetClass.Stock, CancellationToken.None);

        var holding = summary.Holdings.Should().ContainSingle().Subject;
        holding.PriceSource.Should().Be(PriceSource.Close);
        holding.CurrentPriceUsd.Should().Be(150m);
        holding.PriceAsOf.Should().Be(new DateTimeOffset(2026, 1, 30, 0, 0, 0, TimeSpan.Zero));
        holding.MarketValueUsd.Should().Be(1500m); // 10 * 150, not "today"'s (nonexistent) price
        summary.UnpricedHoldingsCount.Should().Be(0);
    }

    [Fact]
    public async Task GetSummaryAsync_LiveQuotePresent_TakesPriorityOverStoredClose()
    {
        var aapl = AddAsset(1, "AAPL", AssetClass.Stock, "USD");
        _db.Transactions.Add(new Transaction
        {
            AssetId = aapl.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 10m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        _db.PriceHistories.Add(new PriceHistory
        {
            AssetId = aapl.Id, Date = new DateOnly(2026, 1, 30), Close = 150m, Currency = "USD",
        });
        // 14:30 UTC = 09:30 ET on the same trading day as NyseSessionNow — a genuinely live quote.
        var liveAsOf = new DateTimeOffset(2026, 2, 2, 14, 30, 0, TimeSpan.Zero);
        _db.PriceQuotes.Add(new PriceQuote { AssetId = aapl.Id, Price = 160m, Currency = "USD", AsOf = liveAsOf });
        await _db.SaveChangesAsync();

        var summary = await _sut.GetSummaryAsync(AssetClass.Stock, CancellationToken.None);

        var holding = summary.Holdings.Should().ContainSingle().Subject;
        holding.PriceSource.Should().Be(PriceSource.Live);
        holding.CurrentPriceUsd.Should().Be(160m);
        holding.PriceAsOf.Should().Be(liveAsOf);
    }

    [Fact]
    public async Task GetSummaryAsync_BuyOnlyPosition_AverageCostUsd_IsCostBasisDividedByQuantity()
    {
        var aapl = AddAsset(1, "AAPL", AssetClass.Stock, "USD");
        _db.Transactions.Add(new Transaction
        {
            AssetId = aapl.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 10m, PricePerUnit = 100m, Fees = 5m, Currency = "USD",
        });
        await _db.SaveChangesAsync();

        var summary = await _sut.GetSummaryAsync(AssetClass.Stock, CancellationToken.None);

        var holding = summary.Holdings.Should().ContainSingle().Subject;
        holding.CostBasisUsd.Should().Be(1005m); // 1000 + 5 fee
        holding.AverageCostUsd.Should().Be(100.5m); // 1005 / 10
    }

    [Fact]
    public async Task GetSummaryAsync_MultipleBuysAtDifferentPrices_AverageCostUsd_IsBlendedAcrossBoth()
    {
        var aapl = AddAsset(1, "AAPL", AssetClass.Stock, "USD");
        _db.Transactions.Add(new Transaction
        {
            AssetId = aapl.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 10m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        _db.Transactions.Add(new Transaction
        {
            AssetId = aapl.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 10),
            Quantity = 10m, PricePerUnit = 200m, Fees = 0m, Currency = "USD",
        });
        await _db.SaveChangesAsync();

        var summary = await _sut.GetSummaryAsync(AssetClass.Stock, CancellationToken.None);

        var holding = summary.Holdings.Should().ContainSingle().Subject;
        holding.QuantityHeld.Should().Be(20m);
        holding.CostBasisUsd.Should().Be(3000m); // (10*100) + (10*200)
        holding.AverageCostUsd.Should().Be(150m); // 3000 / 20, blended across both buys
    }

    /// <summary>
    /// An average-cost sell costs the units sold out at the average cost per unit immediately
    /// before the sale (see <see cref="AverageCostCalculator"/>), so it removes cost and quantity
    /// in the same proportion — the remaining position's average cost per unit is unchanged by the
    /// sale itself, even though both <see cref="HoldingDto.CostBasisUsd"/> and
    /// <see cref="HoldingDto.QuantityHeld"/> shrink.
    /// </summary>
    [Fact]
    public async Task GetSummaryAsync_PartiallySoldPosition_AverageCostUsd_IsUnchangedByTheSell()
    {
        var aapl = AddAsset(1, "AAPL", AssetClass.Stock, "USD");
        _db.Transactions.Add(new Transaction
        {
            AssetId = aapl.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 20m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        _db.Transactions.Add(new Transaction
        {
            AssetId = aapl.Id, Type = TransactionType.Sell, TradeDate = new DateOnly(2026, 1, 15),
            Quantity = 10m, PricePerUnit = 150m, Fees = 0m, Currency = "USD",
        });
        await _db.SaveChangesAsync();

        var summary = await _sut.GetSummaryAsync(AssetClass.Stock, CancellationToken.None);

        var holding = summary.Holdings.Should().ContainSingle().Subject;
        holding.QuantityHeld.Should().Be(10m);
        holding.CostBasisUsd.Should().Be(1000m); // 2000 basis - (100 avg * 10 sold)
        holding.AverageCostUsd.Should().Be(100m); // unchanged: still 100/unit, same as before the sell
    }

    [Fact]
    public async Task GetSummaryAsync_FullyClosedPosition_AverageCostUsd_IsNull_NotZero()
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
        holding.AverageCostUsd.Should().BeNull("a fully sold-down position has realised P&L, not an average cost");
    }

    /// <summary>
    /// Pins the <see cref="DisplayRounding.Price"/> (10 dp), not <c>Money</c> (4 dp), rounding for
    /// <see cref="HoldingDto.AverageCostUsd"/>. A sub-cent asset priced like ANVL
    /// (~$0.0005326/unit) forces a repeating decimal once divided back out over a quantity that
    /// does not divide evenly, so this also proves the division happens after money-rounding the
    /// cost basis, not before: 3 * 0.0005326 = 0.0015978, rounded to 0.0016 at 4 dp, then
    /// 0.0016 / 3 = 0.0005333333333333... rounded to 10 dp = 0.0005333333.
    /// </summary>
    [Fact]
    public async Task GetSummaryAsync_SubCentAsset_AverageCostUsd_RoundsToTenDecimalPlaces_NotFour()
    {
        var anvl = AddAsset(5, "ANVL", AssetClass.Crypto, "USD");
        _db.Transactions.Add(new Transaction
        {
            AssetId = anvl.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 3m, PricePerUnit = 0.0005326m, Fees = 0m, Currency = "USD",
        });
        await _db.SaveChangesAsync();

        var summary = await _sut.GetSummaryAsync(AssetClass.Crypto, CancellationToken.None);

        var holding = summary.Holdings.Should().ContainSingle().Subject;
        holding.CostBasisUsd.Should().Be(0.0016m);
        holding.AverageCostUsd.Should().Be(0.0005333333m);
    }

    [Fact]
    public async Task GetSummaryAsync_NoLiveQuote_SgdAsset_ClosePriorToTheOnlyStoredFxRate_StillConverts_ByCarryingRateBack()
    {
        // The close predates every stored FX rate (2026-01-15 close, earliest rate 2026-01-20) —
        // FxRateResolver falls back to the earliest available rate rather than throwing, and the
        // D20 fallback must use that same carried-back rate, not silently skip the holding.
        var z74 = AddAsset(3, "Z74", AssetClass.Stock, "SGD");
        _db.Transactions.Add(new Transaction
        {
            AssetId = z74.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 20),
            Quantity = 100m, PricePerUnit = 4m, Fees = 0m, Currency = "SGD",
        });
        _db.PriceHistories.Add(new PriceHistory
        {
            AssetId = z74.Id, Date = new DateOnly(2026, 1, 15), Close = 4.5m, Currency = "SGD",
        });
        _db.FxRates.Add(new FxRate { Date = new DateOnly(2026, 1, 20), Base = "USD", Quote = "SGD", Rate = 1.25m });
        await _db.SaveChangesAsync();

        var summary = await _sut.GetSummaryAsync(AssetClass.Stock, CancellationToken.None);

        var holding = summary.Holdings.Should().ContainSingle().Subject;
        holding.PriceSource.Should().Be(PriceSource.Close);
        holding.CurrentPriceUsd.Should().Be(3.6m); // 4.5 / 1.25
        summary.UnpricedHoldingsCount.Should().Be(0);
    }
}
