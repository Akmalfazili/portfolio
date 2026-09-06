using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Common;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services;
using Portfolio.Application.Services.Calendar;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;
using Portfolio.Infrastructure.Persistence;
using Portfolio.UnitTests.TestSupport;

namespace Portfolio.UnitTests.Services;

/// <summary>
/// <see cref="ZakatService"/> — see zakat.md end to end. Covers every one of the six
/// <see cref="ZakatAssetStatus"/> outcomes reaching the report (zakat.md §6, and the
/// D10/D26/D33/D35/D38/D45 defect family the taxonomy exists to prevent repeating), the FX
/// direction pin (§4.3 — the single easiest thing in this feature to get backwards), and the
/// zakat payment ledger's own validation rules.
///
/// <para>Unless a test configures it otherwise, <see cref="_fxSpotRateService"/> returns null (an
/// NSubstitute Task-returning member defaults to a completed task carrying <c>default</c>, i.e.
/// null here) — every pre-existing test in this file therefore exercises the "spot unavailable,
/// fall back to the daily close" branch exactly as it did before the live-spot change, and the
/// spot-specific behaviour gets its own tests below.</para>
/// </summary>
public sealed class ZakatServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 3);

    private readonly PortfolioDbContext _db;
    private readonly IFxSpotRateService _fxSpotRateService = Substitute.For<IFxSpotRateService>();
    private readonly ZakatService _sut;

    public ZakatServiceTests()
    {
        var options = new DbContextOptionsBuilder<PortfolioDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new PortfolioDbContext(options);

        _sut = new ZakatService(_db, new FixedTimeProvider(Now), new MarketCalendar(), _fxSpotRateService);
    }

    public void Dispose() => _db.Dispose();

    private Asset AddStock(int id, string symbol, string currency, int? fyMonth, int? fyDay)
    {
        var asset = new Asset
        {
            Id = id,
            Symbol = symbol,
            Name = symbol,
            AssetClass = AssetClass.Stock,
            Currency = currency,
            QuoteProviderKind = currency == "SGD" ? QuoteProviderKind.Yahoo : QuoteProviderKind.TwelveData,
            FiscalYearEndMonth = fyMonth,
            FiscalYearEndDay = fyDay,
        };
        _db.Assets.Add(asset);
        return asset;
    }

    private Asset AddCrypto(int id, string symbol)
    {
        var asset = new Asset
        {
            Id = id,
            Symbol = symbol,
            Name = symbol,
            AssetClass = AssetClass.Crypto,
            Currency = "USD",
            QuoteProviderKind = QuoteProviderKind.CoinGecko,
        };
        _db.Assets.Add(asset);
        return asset;
    }

    private void AddUsdSgdRate(DateOnly date, decimal rate) =>
        _db.FxRates.Add(new FxRate { Date = date, Base = "USD", Quote = "SGD", Rate = rate });

    [Fact]
    public async Task GetReportAsync_UsdAsset_Included_MultipliesByRate_NotDivides()
    {
        // zakat.md §4.3: FxRate.Rate is SGD per USD. A USD value must convert to MORE SGD.
        var msft = AddStock(1, "MSFT", "USD", 6, 30);
        _db.Transactions.Add(new Transaction
        {
            AssetId = msft.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 10m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        _db.PriceHistories.Add(new PriceHistory { AssetId = msft.Id, Date = new DateOnly(2026, 6, 30), Close = 200m, Currency = "USD" });
        AddUsdSgdRate(new DateOnly(2026, 6, 30), 1.30m);
        await _db.SaveChangesAsync();

        var report = await _sut.GetReportAsync(Today, CancellationToken.None);

        var line = report.Stocks.Should().ContainSingle().Subject;
        line.Status.Should().Be(ZakatAssetStatus.Included);
        line.FiscalYearEndDate.Should().Be(new DateOnly(2026, 6, 30));
        line.QuantityHeld.Should().Be(10m);
        line.CloseNative.Should().Be(200m);
        line.CloseDateExact.Should().BeTrue();
        line.FxDateUsed.Should().Be(new DateOnly(2026, 6, 30));
        line.FxCarriedBack.Should().BeFalse();
        line.FxRateUsed.Should().Be(1.30m);

        // 10 units * 200 USD = 2000 USD; multiplied (not divided) by 1.30 = 2600 SGD.
        // Dividing instead of multiplying would give ~1538.46 SGD — the D-class mistake this pins.
        line.ValueSgd.Should().Be(2600m);
        // ValueSgd must reconcile against the arithmetic FxRateUsed claims to describe — never a
        // rate that merely looks plausible alongside a value computed some other way.
        line.ValueSgd.Should().Be(DisplayRoundingSum(line.QuantityHeld!.Value * line.CloseNative!.Value * line.FxRateUsed!.Value));
        report.StockZakatableSgd.Should().Be(2600m);
        report.TotalZakatableSgd.Should().Be(2600m);
        report.ZakatPayableSgd.Should().Be(65m); // 2.5% of 2600
        report.ExcludedAssetCount.Should().Be(0);
    }

    [Fact]
    public async Task GetReportAsync_UsdAsset_FxResolvedAtFiscalYearEnd_NotAtTheCarriedForwardCloseDate()
    {
        // Mirrors the measured AVGO/HLAL divergence: the fiscal year end (2025-11-02, a Sunday) has
        // no price history of its own — the equity market was shut — so the close carries back to
        // the prior trading day, 2025-10-31. The FX series runs its own calendar and DOES have a row
        // dated exactly 2025-11-02 (Twelve Data's USD/SGD daily series is not weekday-only). The FX
        // leg must resolve against the fiscal year end, not the close date it happens to have carried
        // back to — reverting Application/Services/ZakatService.cs's BuildStockLine to resolve FX
        // against closeRow.Date instead of fyEnd.Value must fail this test.
        var avgo = AddStock(1, "AVGO", "USD", 11, 2);
        _db.Transactions.Add(new Transaction
        {
            AssetId = avgo.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2025, 1, 1),
            Quantity = 5m, PricePerUnit = 250m, Fees = 0m, Currency = "USD",
        });
        // No PriceHistory row on 2025-11-02 itself — carries back to the prior trading day.
        _db.PriceHistories.Add(new PriceHistory { AssetId = avgo.Id, Date = new DateOnly(2025, 10, 31), Close = 300m, Currency = "USD" });
        // The close date's own rate — what the OLD (reverted) coupling would use.
        AddUsdSgdRate(new DateOnly(2025, 10, 31), 1.30160m);
        // The fiscal year end's own rate — what the NEW behaviour under test must use.
        AddUsdSgdRate(new DateOnly(2025, 11, 2), 1.28000m);
        await _db.SaveChangesAsync();

        var report = await _sut.GetReportAsync(Today, CancellationToken.None);

        var line = report.Stocks.Should().ContainSingle().Subject;
        line.Status.Should().Be(ZakatAssetStatus.Included);
        line.FiscalYearEndDate.Should().Be(new DateOnly(2025, 11, 2));
        line.CloseDateUsed.Should().Be(new DateOnly(2025, 10, 31));
        line.CloseDateExact.Should().BeFalse(); // the equity close carried back
        line.FxDateUsed.Should().Be(new DateOnly(2025, 11, 2)); // the FX leg did NOT carry back
        line.FxCarriedBack.Should().BeFalse(); // an exact FX row exists for the year end
        line.FxRateUsed.Should().Be(1.28000m);

        // 5 units * 300 USD * 1.28000 = 1920 SGD — the year-end rate, never the close date's 1.30160
        // (which would give 1952.40 SGD instead).
        line.ValueSgd.Should().Be(1920m);
        report.StockZakatableSgd.Should().Be(1920m);
    }

    [Fact]
    public async Task GetReportAsync_SgdAsset_Included_NoFxConversionAtAll()
    {
        var z74 = AddStock(1, "Z74", "SGD", 3, 31);
        _db.Transactions.Add(new Transaction
        {
            AssetId = z74.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 100m, PricePerUnit = 4m, Fees = 0m, Currency = "SGD",
        });
        _db.PriceHistories.Add(new PriceHistory { AssetId = z74.Id, Date = new DateOnly(2026, 3, 31), Close = 4.20m, Currency = "SGD" });
        // A USD/SGD rate exists but must not be touched for an already-SGD asset — a double
        // conversion would be wrong even though it happens to leave the currency label correct.
        AddUsdSgdRate(new DateOnly(2026, 3, 31), 1.30m);
        await _db.SaveChangesAsync();

        var report = await _sut.GetReportAsync(Today, CancellationToken.None);

        var line = report.Stocks.Should().ContainSingle().Subject;
        line.Status.Should().Be(ZakatAssetStatus.Included);
        line.FxDateUsed.Should().BeNull();
        line.FxCarriedBack.Should().BeNull();
        // SGD-native (Z74) does no FX conversion at all — never 1.0, which would be indistinguishable
        // from a genuine unit rate. Null means "no conversion happened".
        line.FxRateUsed.Should().BeNull();
        line.ValueSgd.Should().Be(420m); // 100 * 4.20, no FX at all
    }

    [Fact]
    public async Task GetReportAsync_QuantityZeroAtFiscalYearEnd_ReportsNotHeldAtFiscalYearEnd_AsZero_NotExcluded()
    {
        // Bought AFTER the last fiscal year end (2025-12-31) — zakat.md §2.8.
        var jnj = AddStock(1, "JNJ", "USD", 12, 31);
        _db.Transactions.Add(new Transaction
        {
            AssetId = jnj.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 3, 24),
            Quantity = 5m, PricePerUnit = 150m, Fees = 0m, Currency = "USD",
        });
        await _db.SaveChangesAsync();

        var report = await _sut.GetReportAsync(Today, CancellationToken.None);

        var line = report.Stocks.Should().ContainSingle().Subject;
        line.Status.Should().Be(ZakatAssetStatus.NotHeldAtFiscalYearEnd);
        line.QuantityHeld.Should().Be(0m);
        line.ValueSgd.Should().Be(0m); // counted, as zero
        report.TotalZakatableSgd.Should().Be(0m);
        report.ExcludedAssetCount.Should().Be(0); // NOT excluded — a correct answer, not a missing input
    }

    [Fact]
    public async Task GetReportAsync_NoFiscalYearEndConfigured_IsExcluded_NotZero()
    {
        var aapl = AddStock(1, "AAPL", "USD", null, null);
        _db.Transactions.Add(new Transaction
        {
            AssetId = aapl.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 5m, PricePerUnit = 150m, Fees = 0m, Currency = "USD",
        });
        await _db.SaveChangesAsync();

        var report = await _sut.GetReportAsync(Today, CancellationToken.None);

        var line = report.Stocks.Should().ContainSingle().Subject;
        line.Status.Should().Be(ZakatAssetStatus.FiscalYearEndNotConfigured);
        line.FiscalYearEndDate.Should().BeNull();
        line.QuantityHeld.Should().BeNull();
        line.ValueSgd.Should().BeNull(); // excluded, never presented as zero
        report.ExcludedAssetCount.Should().Be(1);
    }

    [Fact]
    public async Task GetReportAsync_NoCloseOnOrBeforeFiscalYearEnd_IsExcluded()
    {
        // FYE 12/31 as of "today" 2026-09-03 resolves to 2025-12-31 (this year's has not happened
        // yet) — the trade date must be at or before THAT date for the position to be held then.
        var ttd = AddStock(1, "TTD", "USD", 12, 31);
        _db.Transactions.Add(new Transaction
        {
            AssetId = ttd.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2025, 1, 1),
            Quantity = 5m, PricePerUnit = 50m, Fees = 0m, Currency = "USD",
        });
        // No PriceHistory rows at all for this asset.
        await _db.SaveChangesAsync();

        var report = await _sut.GetReportAsync(Today, CancellationToken.None);

        var line = report.Stocks.Should().ContainSingle().Subject;
        line.Status.Should().Be(ZakatAssetStatus.NoCloseOnOrBeforeFiscalYearEnd);
        line.QuantityHeld.Should().Be(5m); // quantity WAS resolved — only the close lookup failed
        line.ValueSgd.Should().BeNull();
        report.ExcludedAssetCount.Should().Be(1);
    }

    [Fact]
    public async Task GetReportAsync_NoFxRateAtAll_ForAUsdAsset_IsExcluded()
    {
        // FYE 11/1 as of "today" 2026-09-03 resolves to 2025-11-01 (this year's has not happened
        // yet) — the trade date must be at or before THAT date.
        var avgo = AddStock(1, "AVGO", "USD", 11, 1);
        _db.Transactions.Add(new Transaction
        {
            AssetId = avgo.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2025, 1, 1),
            Quantity = 5m, PricePerUnit = 50m, Fees = 0m, Currency = "USD",
        });
        _db.PriceHistories.Add(new PriceHistory { AssetId = avgo.Id, Date = new DateOnly(2025, 10, 30), Close = 300m, Currency = "USD" });
        // No FxRate rows at all — zakat.md §7.4's structural risk if Z74 is ever sold entirely.
        await _db.SaveChangesAsync();

        var report = await _sut.GetReportAsync(Today, CancellationToken.None);

        var line = report.Stocks.Should().ContainSingle().Subject;
        line.Status.Should().Be(ZakatAssetStatus.NoFxRateForCloseDate);
        line.CloseNative.Should().Be(300m); // close WAS found — only FX failed
        line.ValueSgd.Should().BeNull();
        report.ExcludedAssetCount.Should().Be(1);
    }

    [Fact]
    public async Task GetReportAsync_CryptoWithNoQuote_IsExcluded()
    {
        AddCrypto(1, "ETH");
        _db.Transactions.Add(new Transaction
        {
            AssetId = 1, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 0.5m, PricePerUnit = 2000m, Fees = 0m, Currency = "USD",
        });
        // No PriceQuote at all.
        await _db.SaveChangesAsync();

        var report = await _sut.GetReportAsync(Today, CancellationToken.None);

        var line = report.Crypto.Should().ContainSingle().Subject;
        line.Status.Should().Be(ZakatAssetStatus.NoQuote);
        line.QuantityHeld.Should().Be(0.5m); // always computed regardless of status
        line.ValueSgd.Should().BeNull();
        report.ExcludedAssetCount.Should().Be(1);
    }

    [Fact]
    public async Task GetReportAsync_CryptoWithQuote_Included_ValuedAtTodaysPrice()
    {
        var eth = AddCrypto(1, "ETH");
        _db.Transactions.Add(new Transaction
        {
            AssetId = eth.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 0.5m, PricePerUnit = 2000m, Fees = 0m, Currency = "USD",
        });
        _db.PriceQuotes.Add(new PriceQuote { AssetId = eth.Id, Price = 2400.46m, Currency = "USD", AsOf = Now });
        AddUsdSgdRate(Today, 1.29m);
        await _db.SaveChangesAsync();

        var report = await _sut.GetReportAsync(Today, CancellationToken.None);

        var line = report.Crypto.Should().ContainSingle().Subject;
        line.Status.Should().Be(ZakatAssetStatus.Included);
        line.PriceUsd.Should().Be(2400.46m);
        line.PriceSource.Should().Be(PriceSource.Live);
        line.FxCarriedBack.Should().BeFalse();
        line.FxRateUsed.Should().Be(1.29m);
        line.ValueSgd.Should().Be(DisplayRoundingSum(0.5m * 2400.46m * 1.29m));
        // Reconcile ValueSgd against the reported quantity/price/rate directly, so FxRateUsed cannot
        // drift away from the arithmetic it claims to describe.
        line.ValueSgd.Should().Be(DisplayRoundingSum(line.QuantityHeld * line.PriceUsd!.Value * line.FxRateUsed!.Value));
        report.CryptoZakatableSgd.Should().Be(line.ValueSgd);

        // The substitute's default null response means a spot WAS attempted (referenceDate is
        // today) and came back unavailable — the warning source, not the historical one.
        line.FxSource.Should().Be(ZakatFxSource.DailyCloseSpotUnavailable);
        line.FxAsOf.Should().BeNull(); // a daily close has no time of day
        await _fxSpotRateService.Received(1).GetOrRefreshAsync("USD", "SGD", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetReportAsync_CryptoWithLiveSpot_UsesSpotRate_IncludedEvenWithNoFxRatesAtAll()
    {
        // No USD/SGD FxRate rows at all — the empty-table guard must not fire when a spot is
        // available, because this branch never reads FxRates.
        var eth = AddCrypto(1, "ETH");
        _db.Transactions.Add(new Transaction
        {
            AssetId = eth.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 0.5m, PricePerUnit = 2000m, Fees = 0m, Currency = "USD",
        });
        _db.PriceQuotes.Add(new PriceQuote { AssetId = eth.Id, Price = 2400.46m, Currency = "USD", AsOf = Now });
        await _db.SaveChangesAsync();

        // 2026-09-03T11:31:00Z = 2026-09-03 19:31 SGT — same SGT calendar day as Today/Now.
        var spotAsOf = new DateTimeOffset(2026, 9, 3, 11, 31, 0, TimeSpan.Zero);
        var spot = new FxSpotQuote { Base = "USD", Quote = "SGD", Rate = 1.26691m, AsOf = spotAsOf, FetchedAt = Now };
        _fxSpotRateService.GetOrRefreshAsync("USD", "SGD", Arg.Any<CancellationToken>()).Returns(spot);

        var report = await _sut.GetReportAsync(Today, CancellationToken.None);

        var line = report.Crypto.Should().ContainSingle().Subject;
        line.Status.Should().Be(ZakatAssetStatus.Included);
        line.FxSource.Should().Be(ZakatFxSource.Spot);
        line.FxAsOf.Should().Be(spotAsOf);
        line.FxDateUsed.Should().Be(new DateOnly(2026, 9, 3));
        line.FxCarriedBack.Should().BeFalse();
        line.FxRateUsed.Should().Be(1.26691m);
        // FX direction pin, extended to the spot path: a USD value must convert to MORE SGD, not
        // less — fails immediately if the multiply here were ever swapped for a divide.
        (0.5m * 2400.46m * 1.26691m).Should().BeGreaterThan(0.5m * 2400.46m);
        line.ValueSgd.Should().Be(DisplayRoundingSum(0.5m * 2400.46m * 1.26691m));
    }

    [Fact]
    public async Task GetReportAsync_CryptoWithHistoricalAsOf_NeverAttemptsASpot_UsesDailyClose()
    {
        var pastDate = new DateOnly(2026, 6, 1);
        var eth = AddCrypto(1, "ETH");
        _db.Transactions.Add(new Transaction
        {
            AssetId = eth.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 0.5m, PricePerUnit = 2000m, Fees = 0m, Currency = "USD",
        });
        _db.PriceQuotes.Add(new PriceQuote { AssetId = eth.Id, Price = 2400.46m, Currency = "USD", AsOf = Now });
        AddUsdSgdRate(pastDate, 1.30m);
        await _db.SaveChangesAsync();

        var report = await _sut.GetReportAsync(pastDate, CancellationToken.None);

        var line = report.Crypto.Should().ContainSingle().Subject;
        line.Status.Should().Be(ZakatAssetStatus.Included);
        line.FxSource.Should().Be(ZakatFxSource.DailyCloseHistoricalAsOf);
        line.FxAsOf.Should().BeNull();
        // A historical ?asOf= must never even ask for a spot — valuing a past position at today's
        // live rate would be silently wrong regardless of whether the spot call would have
        // succeeded.
        await _fxSpotRateService.DidNotReceive().GetOrRefreshAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetReportAsync_ExcludesEveryAssetClassSegregationRuleFor_OneSanctionedException()
    {
        var msft = AddStock(1, "MSFT", "USD", 6, 30);
        _db.Transactions.Add(new Transaction
        {
            AssetId = msft.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 1m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        _db.PriceHistories.Add(new PriceHistory { AssetId = msft.Id, Date = new DateOnly(2026, 6, 30), Close = 100m, Currency = "USD" });
        AddUsdSgdRate(new DateOnly(2026, 6, 30), 1.30m);

        var eth = AddCrypto(2, "ETH");
        _db.Transactions.Add(new Transaction
        {
            AssetId = eth.Id, Type = TransactionType.Buy, TradeDate = new DateOnly(2026, 1, 1),
            Quantity = 1m, PricePerUnit = 100m, Fees = 0m, Currency = "USD",
        });
        _db.PriceQuotes.Add(new PriceQuote { AssetId = eth.Id, Price = 100m, Currency = "USD", AsOf = Now });
        AddUsdSgdRate(Today, 1.29m);
        await _db.SaveChangesAsync();

        var report = await _sut.GetReportAsync(Today, CancellationToken.None);

        // Stocks and crypto stay in separate lists with separate subtotals — the total is the only
        // place they meet (zakat.md §9's "one sanctioned exception" to asset-class segregation).
        report.Stocks.Should().ContainSingle(l => l.Symbol == "MSFT");
        report.Crypto.Should().ContainSingle(l => l.Symbol == "ETH");
        report.TotalZakatableSgd.Should().Be(report.StockZakatableSgd + report.CryptoZakatableSgd);
    }

    [Fact]
    public async Task GetReportAsync_NoAsOfGiven_AtSgtMidnightBoundary_DefaultsToTheNewSgtDay()
    {
        // 2026-09-03T16:30:00Z = 2026-09-04 00:30 SGT. With no ?asOf= supplied, the report's
        // reference date must be the SGT calendar day, not the still-2026-09-03 UTC day.
        var boundaryTimeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 9, 3, 16, 30, 0, TimeSpan.Zero));
        var sut = new ZakatService(_db, boundaryTimeProvider, new MarketCalendar(), _fxSpotRateService);

        var report = await sut.GetReportAsync(null, CancellationToken.None);

        report.AsOf.Should().Be(new DateOnly(2026, 9, 4));
    }

    [Fact]
    public async Task CreatePaymentAsync_RejectsZeroOrNegativeAmount()
    {
        var result = await _sut.CreatePaymentAsync(new CreateZakatPaymentRequest(Today, 0m), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ServiceErrorKind.Validation);
        result.Error.ValidationErrors.Should().ContainKey("amountSgd");
    }

    [Fact]
    public async Task CreatePaymentAsync_RejectsFuturePaidOnDate()
    {
        var future = Today.AddDays(1);

        var result = await _sut.CreatePaymentAsync(new CreateZakatPaymentRequest(future, 100m), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.ValidationErrors.Should().ContainKey("paidOn");
    }

    [Fact]
    public async Task CreatePaymentAsync_AtSgtMidnightBoundary_AcceptsTheNewSgtDayPaidOnDate()
    {
        // 2026-09-03T16:30:00Z = 2026-09-04 00:30 SGT. Under the old UTC-derived "today" this
        // paidOn date would have been rejected as a day in the future — the same SGT/UTC boundary
        // bug the reporting clock closes for trade dates.
        var boundaryTimeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 9, 3, 16, 30, 0, TimeSpan.Zero));
        var sut = new ZakatService(_db, boundaryTimeProvider, new MarketCalendar(), _fxSpotRateService);

        var result = await sut.CreatePaymentAsync(
            new CreateZakatPaymentRequest(new DateOnly(2026, 9, 4), 100m), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CreatePaymentAsync_ValidRequest_Persists_AndListsNewestFirst()
    {
        await _sut.CreatePaymentAsync(new CreateZakatPaymentRequest(new DateOnly(2026, 1, 1), 100m), CancellationToken.None);
        await _sut.CreatePaymentAsync(new CreateZakatPaymentRequest(new DateOnly(2026, 6, 1), 250.50m), CancellationToken.None);

        var payments = await _sut.ListPaymentsAsync(CancellationToken.None);

        payments.Should().HaveCount(2);
        payments[0].PaidOn.Should().Be(new DateOnly(2026, 6, 1)); // newest first
        payments[0].AmountSgd.Should().Be(250.50m);
    }

    [Fact]
    public async Task UpdatePaymentAsync_UnknownId_ReturnsNotFound()
    {
        var result = await _sut.UpdatePaymentAsync(999, new UpdateZakatPaymentRequest(Today, 100m), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ServiceErrorKind.NotFound);
    }

    [Fact]
    public async Task DeletePaymentAsync_RemovesTheRecord()
    {
        var created = await _sut.CreatePaymentAsync(new CreateZakatPaymentRequest(Today, 100m), CancellationToken.None);

        var deleted = await _sut.DeletePaymentAsync(created.Value!.Id, CancellationToken.None);

        deleted.Should().BeTrue();
        (await _sut.ListPaymentsAsync(CancellationToken.None)).Should().BeEmpty();
    }

    private static decimal DisplayRoundingSum(decimal value) => Math.Round(value, 4, MidpointRounding.ToEven);
}
