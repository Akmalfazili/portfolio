using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Portfolio.Application.Abstractions;
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
/// D4 — a stored <see cref="PriceQuote"/> must not be reported as a live price merely because the
/// row exists. There is exactly one quote row per asset, overwritten in place, carrying the
/// <i>provider's</i> own timestamp, so a quote fetched on a day the exchange never traded is
/// byte-for-byte indistinguishable from a fresh one unless something checks its date.
///
/// <para>The case this was written for: <see cref="SgxHolidayCalendar"/> models only Gregorian
/// fixed-date holidays. Singapore's lunar holidays — Chinese New Year, Vesak, Hari Raya,
/// Deepavali — are not in it and, per the D4 decision, deliberately never will be. So on those
/// days the calendar says SGX is open, the refresh service polls Yahoo anyway, and Yahoo returns
/// the previous session's close. The calendar stays wrong; what these tests pin is that the
/// <i>reported price</i> stops pretending otherwise.</para>
/// </summary>
public sealed class QuoteStalenessTests : IDisposable
{
    /// <summary>
    /// Chinese New Year 2026, a Tuesday. SGX is genuinely shut; <see cref="SgxHolidayCalendar"/>
    /// has no idea. 05:00 UTC is 13:00 SGT — inside the afternoon session the calendar believes is
    /// running, which is what makes this the hard case rather than a weekend.
    /// </summary>
    private static readonly DateTimeOffset ChineseNewYearMidSession =
        new(2026, 2, 17, 5, 0, 0, TimeSpan.Zero);

    /// <summary>The previous real SGX session's close: Monday 2026-02-16 at 17:00 SGT.</summary>
    private static readonly DateTimeOffset PreviousSgxClose =
        new(2026, 2, 16, 9, 0, 0, TimeSpan.Zero);

    private readonly PortfolioDbContext _db;

    public QuoteStalenessTests()
    {
        var options = new DbContextOptionsBuilder<PortfolioDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new PortfolioDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    private PortfolioSummaryService SutAt(DateTimeOffset now)
    {
        var timeProvider = new FixedTimeProvider(now);
        return new(
            _db,
            timeProvider,
            new AverageCostCalculator(),
            new MarketCalendar(),
            new DividendService(_db, new DividendIncomeCalculator(), timeProvider));
    }

    private Asset AddAsset(int id, string symbol, AssetClass assetClass, string currency, QuoteProviderKind provider)
    {
        var asset = new Asset
        {
            Id = id,
            Symbol = symbol,
            Name = symbol,
            AssetClass = assetClass,
            Currency = currency,
            QuoteProviderKind = provider,
            ProviderSymbol = provider == QuoteProviderKind.CoinGecko ? null : symbol,
            ProviderCoinId = provider == QuoteProviderKind.CoinGecko ? symbol.ToLowerInvariant() : null,
        };
        _db.Assets.Add(asset);
        return asset;
    }

    private void AddBuy(int assetId, DateOnly tradeDate, decimal quantity, decimal price, string currency) =>
        _db.Transactions.Add(new Transaction
        {
            AssetId = assetId,
            Type = TransactionType.Buy,
            TradeDate = tradeDate,
            Quantity = quantity,
            PricePerUnit = price,
            Fees = 0m,
            Currency = currency,
        });

    /// <summary>
    /// Establishes the premise the rest of this class depends on, rather than asserting it in
    /// prose. If Singapore's lunar holidays were ever added to the table, this test fails and tells
    /// the next reader that the D4 workaround is no longer load-bearing for this date — which is
    /// information, not breakage.
    /// </summary>
    [Fact]
    public void Premise_TheCalendarIsWrongOnChineseNewYear()
    {
        new MarketCalendar()
            .IsOpen(Market.Sgx, ChineseNewYearMidSession)
            .Should()
            .BeTrue("D4's whole point is that the holiday table does not know about lunar holidays");
    }

    /// <summary>The D4 defect itself, at the exact moment it used to occur.</summary>
    [Fact]
    public async Task SgxQuoteFromAnEarlierSession_OnAnUnmodelledLunarHoliday_IsReportedAsClose_NotLive()
    {
        var z74 = AddAsset(1, "Z74", AssetClass.Stock, "SGD", QuoteProviderKind.Yahoo);
        AddBuy(z74.Id, new DateOnly(2026, 1, 5), 100m, 4m, "SGD");
        _db.FxRates.Add(new FxRate { Date = new DateOnly(2026, 1, 5), Base = "USD", Quote = "SGD", Rate = 1.25m });

        // What the refresh service stores on a lunar holiday: Yahoo answers the poll with the
        // PREVIOUS session's close, correctly stamped with that session's regularMarketTime.
        _db.PriceQuotes.Add(new PriceQuote
        {
            AssetId = z74.Id, Price = 4.50m, Currency = "SGD", AsOf = PreviousSgxClose,
        });
        await _db.SaveChangesAsync();

        var summary = await SutAt(ChineseNewYearMidSession).GetSummaryAsync(AssetClass.Stock, CancellationToken.None);

        var holding = summary.Holdings.Should().ContainSingle().Subject;

        holding.PriceSource.Should().Be(
            PriceSource.Close,
            "Z74 did not trade today; this number is Monday's close and must say so");

        // The caption the frontend renders is driven by this field. It must carry the close's own
        // instant, never "now" — that substitution is the original D4 mistake in miniature.
        holding.PriceAsOf.Should().Be(PreviousSgxClose);

        // Still a real, usable price — this is about honesty, not about withholding the number.
        holding.CurrentPriceUsd.Should().Be(3.6m); // 4.50 / 1.25
        holding.MarketValueUsd.Should().Be(360m);
        summary.UnpricedHoldingsCount.Should().Be(0);
    }

    /// <summary>
    /// The second half of the rule, covering the date check's blind spot: a day the calendar
    /// <i>does</i> understand, after the session has ended. The quote's local date is still
    /// "today", so only the market-open half catches this.
    /// </summary>
    [Fact]
    public async Task NyseQuoteAfterTheSessionHasClosed_IsReportedAsClose_EvenOnTheSameDay()
    {
        var aapl = AddAsset(1, "AAPL", AssetClass.Stock, "USD", QuoteProviderKind.TwelveData);
        AddBuy(aapl.Id, new DateOnly(2026, 2, 2), 10m, 100m, "USD");

        // 20:59 UTC = 15:59 ET — the last tick before the bell, on Monday 2026-02-02.
        var lastTickBeforeClose = new DateTimeOffset(2026, 2, 2, 20, 59, 0, TimeSpan.Zero);
        _db.PriceQuotes.Add(new PriceQuote
        {
            AssetId = aapl.Id, Price = 120m, Currency = "USD", AsOf = lastTickBeforeClose,
        });
        await _db.SaveChangesAsync();

        // 23:00 UTC = 18:00 ET, same calendar day, two hours after NYSE closed.
        var afterHours = new DateTimeOffset(2026, 2, 2, 23, 0, 0, TimeSpan.Zero);
        var summary = await SutAt(afterHours).GetSummaryAsync(AssetClass.Stock, CancellationToken.None);

        var holding = summary.Holdings.Should().ContainSingle().Subject;
        holding.PriceSource.Should().Be(PriceSource.Close, "the market shut two hours ago");
        holding.PriceAsOf.Should().Be(lastTickBeforeClose);
        holding.CurrentPriceUsd.Should().Be(120m);
    }

    /// <summary>The rule must not fire while a market is genuinely trading.</summary>
    [Fact]
    public async Task NyseQuoteDuringTheSession_IsStillReportedAsLive()
    {
        var aapl = AddAsset(1, "AAPL", AssetClass.Stock, "USD", QuoteProviderKind.TwelveData);
        AddBuy(aapl.Id, new DateOnly(2026, 2, 2), 10m, 100m, "USD");

        var openingTick = new DateTimeOffset(2026, 2, 2, 14, 35, 0, TimeSpan.Zero); // 09:35 ET
        _db.PriceQuotes.Add(new PriceQuote
        {
            AssetId = aapl.Id, Price = 120m, Currency = "USD", AsOf = openingTick,
        });
        await _db.SaveChangesAsync();

        // 15:00 ET — same session, hours later. A naive "quote must be minutes old" staleness rule
        // would wrongly demote this; the session-based rule does not.
        var midSession = new DateTimeOffset(2026, 2, 2, 20, 0, 0, TimeSpan.Zero);
        var summary = await SutAt(midSession).GetSummaryAsync(AssetClass.Stock, CancellationToken.None);

        summary.Holdings.Should().ContainSingle().Which.PriceSource.Should().Be(PriceSource.Live);
    }

    /// <summary>
    /// Crypto has no market, no session and no calendar gate, so "which session does this belong
    /// to?" is not a question that applies. Pinned because the obvious implementation — compare
    /// the quote's UTC date to today's — would demote a two-minute-old crypto quote to "close" the
    /// instant the clock passes midnight UTC, and crypto stores no closes to fall back to.
    /// </summary>
    [Fact]
    public async Task CryptoQuote_IsAlwaysLive_EvenAcrossAUtcMidnightRollover()
    {
        var eth = AddAsset(1, "ETH", AssetClass.Crypto, "USD", QuoteProviderKind.CoinGecko);
        AddBuy(eth.Id, new DateOnly(2026, 2, 1), 2m, 1500m, "USD");

        var justBeforeMidnight = new DateTimeOffset(2026, 2, 2, 23, 59, 0, TimeSpan.Zero);
        _db.PriceQuotes.Add(new PriceQuote
        {
            AssetId = eth.Id, Price = 1800m, Currency = "USD", AsOf = justBeforeMidnight,
        });
        await _db.SaveChangesAsync();

        var justAfterMidnight = new DateTimeOffset(2026, 2, 3, 0, 1, 0, TimeSpan.Zero);
        var summary = await SutAt(justAfterMidnight).GetSummaryAsync(AssetClass.Crypto, CancellationToken.None);

        var holding = summary.Holdings.Should().ContainSingle().Subject;
        holding.PriceSource.Should().Be(PriceSource.Live, "a two-minute-old crypto quote is live");
        holding.MarketValueUsd.Should().Be(3600m);
    }

    /// <summary>
    /// When a quote goes stale it becomes another close candidate, not a discarded one. Backfill
    /// runs daily while quotes go stale within hours, so the stale quote is usually the <i>newer</i>
    /// of the two — falling straight through to <c>PriceHistory</c> would report an older number
    /// than the one already on hand.
    /// </summary>
    [Fact]
    public async Task StaleQuoteNewerThanTheLastStoredClose_Wins_AndIsStillLabelledClose()
    {
        var z74 = AddAsset(1, "Z74", AssetClass.Stock, "SGD", QuoteProviderKind.Yahoo);
        AddBuy(z74.Id, new DateOnly(2026, 1, 5), 100m, 4m, "SGD");
        _db.FxRates.Add(new FxRate { Date = new DateOnly(2026, 1, 5), Base = "USD", Quote = "SGD", Rate = 1.25m });

        // Backfill last ran a week ago; the stale quote is from Monday, five days newer.
        _db.PriceHistories.Add(new PriceHistory
        {
            AssetId = z74.Id, Date = new DateOnly(2026, 2, 11), Close = 4.00m, Currency = "SGD",
        });
        _db.PriceQuotes.Add(new PriceQuote
        {
            AssetId = z74.Id, Price = 4.50m, Currency = "SGD", AsOf = PreviousSgxClose,
        });
        await _db.SaveChangesAsync();

        var summary = await SutAt(ChineseNewYearMidSession).GetSummaryAsync(AssetClass.Stock, CancellationToken.None);

        var holding = summary.Holdings.Should().ContainSingle().Subject;
        holding.PriceSource.Should().Be(PriceSource.Close);
        holding.CurrentPriceUsd.Should().Be(3.6m); // 4.50 (the newer quote), not 4.00 (the older close)
        holding.PriceAsOf.Should().Be(PreviousSgxClose);
    }

    /// <summary>
    /// D48 — the tie the suite missed. Twelve Data's <c>quote.timestamp</c> is the daily bar's
    /// OPEN, not the sample instant, so a US quote polled anytime during the session — and left
    /// stale once <see cref="PriceRefreshService"/> stops polling at the close — carries the
    /// SAME date as that evening's backfilled <see cref="PriceHistory"/> row. The two are not
    /// the same kind of number: PriceHistory is the official close, the quote is an arbitrary
    /// mid-session snapshot. On a date collision PriceHistory must win, or the app reports a
    /// mid-session price as tonight's close on every US symbol, every night.
    /// </summary>
    [Fact]
    public async Task StaleQuoteSameDateAsTheStoredClose_CloseWins()
    {
        var amzn = AddAsset(1, "AMZN", AssetClass.Stock, "USD", QuoteProviderKind.TwelveData);
        AddBuy(amzn.Id, new DateOnly(2026, 2, 2), 10m, 100m, "USD");

        // Twelve Data stamps quote.timestamp as the daily bar's OPEN — 13:30 UTC = 09:30 ET,
        // the opening bell — carrying a mid-session price sampled sometime that day.
        var barOpenTimestamp = new DateTimeOffset(2026, 2, 17, 13, 30, 0, TimeSpan.Zero);
        _db.PriceQuotes.Add(new PriceQuote
        {
            AssetId = amzn.Id, Price = 257.21m, Currency = "USD", AsOf = barOpenTimestamp,
        });

        // The evening backfill has already landed the official close for that same date.
        _db.PriceHistories.Add(new PriceHistory
        {
            AssetId = amzn.Id, Date = new DateOnly(2026, 2, 17), Close = 256.97m, Currency = "USD",
        });
        await _db.SaveChangesAsync();

        // Well after the close, the same evening.
        var afterClose = new DateTimeOffset(2026, 2, 18, 2, 0, 0, TimeSpan.Zero);
        var summary = await SutAt(afterClose).GetSummaryAsync(AssetClass.Stock, CancellationToken.None);

        var holding = summary.Holdings.Should().ContainSingle().Subject;
        holding.PriceSource.Should().Be(PriceSource.Close);
        holding.CurrentPriceUsd.Should().Be(256.97m, "the official close must outrank a same-date mid-session quote");
        holding.MarketValueUsd.Should().Be(2569.7m);
        holding.PriceAsOf.Should().Be(
            new DateTimeOffset(2026, 2, 17, 0, 0, 0, TimeSpan.Zero),
            "PriceAsOf must carry the close's own date, never the quote's bar-open timestamp");
    }

    /// <summary>
    /// The mirror case: when <c>PriceHistory</c> genuinely is newer than the stale quote, it wins.
    /// Guards against "prefer the quote" being hardcoded rather than actually comparing dates.
    /// </summary>
    [Fact]
    public async Task StoredCloseNewerThanTheStaleQuote_Wins()
    {
        var z74 = AddAsset(1, "Z74", AssetClass.Stock, "SGD", QuoteProviderKind.Yahoo);
        AddBuy(z74.Id, new DateOnly(2026, 1, 5), 100m, 4m, "SGD");
        _db.FxRates.Add(new FxRate { Date = new DateOnly(2026, 1, 5), Base = "USD", Quote = "SGD", Rate = 1.25m });

        // A quote left over from January, and a close backfilled the day before "now".
        _db.PriceQuotes.Add(new PriceQuote
        {
            AssetId = z74.Id, Price = 4.50m, Currency = "SGD",
            AsOf = new DateTimeOffset(2026, 1, 20, 9, 0, 0, TimeSpan.Zero),
        });
        _db.PriceHistories.Add(new PriceHistory
        {
            AssetId = z74.Id, Date = new DateOnly(2026, 2, 16), Close = 4.00m, Currency = "SGD",
        });
        await _db.SaveChangesAsync();

        var summary = await SutAt(ChineseNewYearMidSession).GetSummaryAsync(AssetClass.Stock, CancellationToken.None);

        var holding = summary.Holdings.Should().ContainSingle().Subject;
        holding.PriceSource.Should().Be(PriceSource.Close);
        holding.CurrentPriceUsd.Should().Be(3.2m); // 4.00 / 1.25 — the newer close
        holding.PriceAsOf.Should().Be(new DateTimeOffset(2026, 2, 16, 0, 0, 0, TimeSpan.Zero));
    }
}
