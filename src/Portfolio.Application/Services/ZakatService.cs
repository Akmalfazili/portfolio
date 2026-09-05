using Microsoft.EntityFrameworkCore;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Common;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services.Calculators;
using Portfolio.Application.Services.Calendar;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services;

/// <summary>
/// Zakat on shares — see zakat.md end to end for the MUIS rules this implements, the conventions
/// chosen where MUIS is ambiguous, and every trap documented there. Computed fresh on every call;
/// nothing about the calculation is persisted (only <see cref="ZakatPayment"/>, a recorded fact
/// entirely independent of this calculation, is).
///
/// <para><b>USD/SGD conversion direction (zakat.md §4.3).</b> <c>FxRate.Rate</c> is stored as SGD
/// per USD (<c>Base = "USD"</c>, <c>Quote = "SGD"</c>). Every other caller in this codebase
/// converts INTO USD and therefore divides. This is the first caller that converts OUT of USD, and
/// therefore MULTIPLIES — see <see cref="ConvertUsdToSgd"/>, and
/// <c>FxDirectionTests</c> for the pinning test.</para>
///
/// <para><b>Crypto's USD/SGD rate is live, but only when the report is valuing "today".</b> A
/// carried-forward daily close can be up to a day stale, which matters for crypto specifically
/// because its price itself is a ~live CoinGecko quote — pairing a fresh price with a stale rate
/// silently understated or overstated the SGD figure. When <see cref="GetReportAsync"/>'s
/// reference date (<c>asOf</c>, or today when null) resolves to today, one
/// <see cref="IFxSpotRateService"/> call is made per report (not per crypto line) and used for
/// every crypto line. When it is a past date, a spot is never attempted — valuing a historical
/// position at today's rate would be wrong regardless of freshness — and the daily-close resolver
/// is used exactly as before. Stock lines are entirely unaffected: they always use the close
/// date's own <c>FxRate</c> row (zakat.md §4.2), which is the MUIS method, not a limitation this
/// file works around.</para>
/// </summary>
public sealed class ZakatService(
    IPortfolioDbContext db, TimeProvider timeProvider, IMarketCalendar calendar, IFxSpotRateService fxSpotRateService)
    : IZakatService
{
    private const string Usd = "USD";
    private const string Sgd = "SGD";

    public async Task<ZakatReportDto> GetReportAsync(DateOnly? asOf, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var today = ReportingClock.Today(timeProvider);
        var referenceDate = asOf ?? today;
        // A spot is only ever worth attempting when this report is valuing "today" — a historical
        // ?asOf= must keep using the daily-close resolver regardless of what the live rate is
        // right now (see the class remarks).
        var isReferenceDateToday = referenceDate == today;

        var stockAssets = await db.Assets
            .Where(a => a.AssetClass == AssetClass.Stock)
            .OrderBy(a => a.Symbol)
            .ToListAsync(cancellationToken);
        var cryptoAssets = await db.Assets
            .Where(a => a.AssetClass == AssetClass.Crypto)
            .OrderBy(a => a.Symbol)
            .ToListAsync(cancellationToken);

        var allAssetIds = stockAssets.Select(a => a.Id).Concat(cryptoAssets.Select(a => a.Id)).ToList();

        var transactionsByAsset = (await db.Transactions
                .Where(t => allAssetIds.Contains(t.AssetId))
                .ToListAsync(cancellationToken))
            .GroupBy(t => t.AssetId)
            .ToDictionary(g => g.Key, IReadOnlyList<Transaction> (g) => g.ToList());

        var stockAssetIds = stockAssets.Select(a => a.Id).ToList();
        var priceHistoryByAsset = (await db.PriceHistories
                .Where(p => stockAssetIds.Contains(p.AssetId))
                .OrderBy(p => p.Date)
                .ToListAsync(cancellationToken))
            .GroupBy(p => p.AssetId)
            .ToDictionary(g => g.Key, IReadOnlyList<PriceHistory> (g) => g.ToList());

        // The one USD/SGD pair this whole report depends on — see zakat.md §7.4: every row in it
        // exists only because Z74 is an SGD asset, so a genuinely empty list here is a real,
        // structurally possible outcome, not just a test fixture gap.
        var usdSgdRates = await db.FxRates
            .Where(f => f.Base == Usd && f.Quote == Sgd)
            .OrderBy(f => f.Date)
            .ToListAsync(cancellationToken);

        var cryptoAssetIds = cryptoAssets.Select(a => a.Id).ToList();
        var quotesByAsset = await db.PriceQuotes
            .Where(q => cryptoAssetIds.Contains(q.AssetId))
            .ToDictionaryAsync(q => q.AssetId, cancellationToken);

        // One spot fetch for the whole report, never one per crypto line — IFxSpotRateService
        // already caches behind a TTL, but there is no reason to hit even that cache N times for
        // one report. Never attempted for a historical ?asOf= — see the class remarks.
        var spot = isReferenceDateToday
            ? await fxSpotRateService.GetOrRefreshAsync(Usd, Sgd, cancellationToken)
            : null;

        var stockLines = stockAssets
            .Select(a => BuildStockLine(a, transactionsByAsset, priceHistoryByAsset, usdSgdRates, referenceDate))
            .ToList();
        var cryptoLines = cryptoAssets
            .Select(a => BuildCryptoLine(
                a, transactionsByAsset, quotesByAsset, usdSgdRates, referenceDate, now, isReferenceDateToday, spot))
            .ToList();

        // Sum the already-DisplayRounding'd per-line figures, matching PortfolioSummaryService's
        // convention, so the totals foot exactly to the rows a caller can see.
        var stockTotal = stockLines.Sum(l => l.ValueSgd ?? 0m);
        var cryptoTotal = cryptoLines.Sum(l => l.ValueSgd ?? 0m);
        var total = stockTotal + cryptoTotal;

        var excludedCount =
            stockLines.Count(l => l.Status is not (ZakatAssetStatus.Included or ZakatAssetStatus.NotHeldAtFiscalYearEnd)) +
            cryptoLines.Count(l => l.Status is not (ZakatAssetStatus.Included or ZakatAssetStatus.NotHeldAtFiscalYearEnd));

        return new ZakatReportDto(
            referenceDate,
            stockLines,
            cryptoLines,
            stockTotal,
            cryptoTotal,
            total,
            DisplayRounding.Money(total * 0.025m),
            excludedCount);
    }

    private static ZakatStockLineDto BuildStockLine(
        Asset asset,
        IReadOnlyDictionary<int, IReadOnlyList<Transaction>> transactionsByAsset,
        IReadOnlyDictionary<int, IReadOnlyList<PriceHistory>> priceHistoryByAsset,
        IReadOnlyList<FxRate> usdSgdRatesAscending,
        DateOnly referenceDate)
    {
        var fyEnd = FiscalYearEndResolver.Resolve(asset.FiscalYearEndMonth, asset.FiscalYearEndDay, referenceDate);
        if (fyEnd is null)
        {
            return NotAttempted(asset, ZakatAssetStatus.FiscalYearEndNotConfigured);
        }

        var transactions = transactionsByAsset.TryGetValue(asset.Id, out var t) ? t : [];
        var quantity = QuantityAsOf.Calculate(transactions, fyEnd.Value);

        if (quantity == 0m)
        {
            return new ZakatStockLineDto(
                asset.Id, asset.Symbol, asset.Name, asset.Currency, ZakatAssetStatus.NotHeldAtFiscalYearEnd,
                fyEnd, 0m, null, null, null, null, null, null, 0m);
        }

        var history = priceHistoryByAsset.TryGetValue(asset.Id, out var h) ? h : [];
        var closeRow = CloseAsOf.Resolve(history, fyEnd.Value);
        if (closeRow is null)
        {
            return new ZakatStockLineDto(
                asset.Id, asset.Symbol, asset.Name, asset.Currency, ZakatAssetStatus.NoCloseOnOrBeforeFiscalYearEnd,
                fyEnd, quantity, null, null, null, null, null, null, null);
        }

        var closeDateExact = closeRow.Date == fyEnd.Value;

        if (asset.Currency == Sgd)
        {
            // Already SGD — no FX conversion at all. Running an SGX close through a USD/SGD rate
            // would be a double conversion (zakat.md §4.2).
            var valueSgd = quantity * closeRow.Close;
            return new ZakatStockLineDto(
                asset.Id, asset.Symbol, asset.Name, asset.Currency, ZakatAssetStatus.Included,
                fyEnd, quantity, closeRow.Close, closeRow.Date, closeDateExact, null, null, null,
                DisplayRounding.Money(valueSgd));
        }

        if (asset.Currency == Usd)
        {
            if (usdSgdRatesAscending.Count == 0)
            {
                return new ZakatStockLineDto(
                    asset.Id, asset.Symbol, asset.Name, asset.Currency, ZakatAssetStatus.NoFxRateForCloseDate,
                    fyEnd, quantity, closeRow.Close, closeRow.Date, closeDateExact, null, null, null, null);
            }

            var fx = FxRateResolver.ResolveDetailed(usdSgdRatesAscending, closeRow.Date);
            var valueSgd = ConvertUsdToSgd(quantity * closeRow.Close, fx.Rate);
            return new ZakatStockLineDto(
                asset.Id, asset.Symbol, asset.Name, asset.Currency, ZakatAssetStatus.Included,
                fyEnd, quantity, closeRow.Close, closeRow.Date, closeDateExact, fx.ResolvedDate, fx.CarriedBack,
                fx.Rate, DisplayRounding.Money(valueSgd));
        }

        // Never fall back to an implicit 1:1 — zakat.md §4.2. Every asset in this portfolio today
        // trades in USD or SGD; a third currency reaching here is a data problem, not a reportable
        // per-asset outcome, so it fails loudly rather than joining the six-status taxonomy.
        throw new InvalidOperationException(
            $"Zakat report cannot convert asset '{asset.Symbol}' (id {asset.Id}) currency " +
            $"'{asset.Currency}' to SGD — only USD and SGD are supported.");
    }

    private ZakatCryptoLineDto BuildCryptoLine(
        Asset asset,
        IReadOnlyDictionary<int, IReadOnlyList<Transaction>> transactionsByAsset,
        IReadOnlyDictionary<int, PriceQuote> quotesByAsset,
        IReadOnlyList<FxRate> usdSgdRatesAscending,
        DateOnly referenceDate,
        DateTimeOffset now,
        bool isReferenceDateToday,
        FxSpotQuote? spot)
    {
        var transactions = transactionsByAsset.TryGetValue(asset.Id, out var t) ? t : [];
        var quantity = QuantityAsOf.Calculate(transactions, referenceDate);

        if (!quotesByAsset.TryGetValue(asset.Id, out var quote))
        {
            return new ZakatCryptoLineDto(
                asset.Id, asset.Symbol, asset.Name, asset.Currency, ZakatAssetStatus.NoQuote,
                quantity, null, null, null, null, null, null, null, null, null);
        }

        if (asset.Currency != Usd)
        {
            throw new InvalidOperationException(
                $"Zakat report cannot convert crypto asset '{asset.Symbol}' (id {asset.Id}) currency " +
                $"'{asset.Currency}' to SGD — only USD is supported for crypto.");
        }

        DateOnly fxDateUsed;
        bool fxCarriedBack;
        decimal fxRateUsed;
        DateTimeOffset? fxAsOf;
        ZakatFxSource fxSource;

        if (spot is not null)
        {
            // A live spot is available — used regardless of what is (or is not) in FxRates. See
            // zakat.md-adjacent reasoning above: an empty daily-close table is not a failure here,
            // because nothing about this branch reads from it.
            fxDateUsed = ReportingClock.DateFor(spot.AsOf);
            fxCarriedBack = false;
            fxRateUsed = spot.Rate;
            fxAsOf = spot.AsOf;
            fxSource = ZakatFxSource.Spot;
        }
        else
        {
            // No spot in play — either it was never attempted (a historical ?asOf=) or it was
            // attempted and came back empty (throttle/429/provider error). Either way this falls
            // back to the same daily-close resolver as before, and the same empty-table guard
            // still applies: a spot's absence does not excuse an empty FxRates table too.
            if (usdSgdRatesAscending.Count == 0)
            {
                return new ZakatCryptoLineDto(
                    asset.Id, asset.Symbol, asset.Name, asset.Currency, ZakatAssetStatus.NoFxRateForCloseDate,
                    quantity, quote.Price, null, quote.AsOf, null, null, null, null, null, null);
            }

            var fx = FxRateResolver.ResolveDetailed(usdSgdRatesAscending, referenceDate);
            fxDateUsed = fx.ResolvedDate;
            fxCarriedBack = fx.CarriedBack;
            fxRateUsed = fx.Rate;
            fxAsOf = null;
            fxSource = isReferenceDateToday
                ? ZakatFxSource.DailyCloseSpotUnavailable
                : ZakatFxSource.DailyCloseHistoricalAsOf;
        }

        var priceSource = QuoteFreshness.Classify(calendar, asset.QuoteProviderKind, quote.AsOf, now);
        var valueSgd = ConvertUsdToSgd(quantity * quote.Price, fxRateUsed);

        return new ZakatCryptoLineDto(
            asset.Id, asset.Symbol, asset.Name, asset.Currency, ZakatAssetStatus.Included,
            quantity, quote.Price, priceSource, quote.AsOf, fxDateUsed, fxCarriedBack,
            fxRateUsed, fxAsOf, fxSource, DisplayRounding.Money(valueSgd));
    }

    /// <summary>
    /// zakat.md §4.3 — <c>FxRate.Rate</c> is SGD per USD, so converting a USD amount INTO SGD
    /// MULTIPLIES by the rate. Every other conversion in this codebase divides, because every other
    /// one converts the other direction (into USD). Copying that idiom here is the single easiest
    /// mistake in this feature — it produces a plausible-looking number wrong by a factor of
    /// roughly the USD/SGD rate itself. See <c>FxDirectionTests</c>, which fails immediately if this
    /// operator is ever swapped.
    /// </summary>
    private static decimal ConvertUsdToSgd(decimal amountUsd, decimal sgdPerUsdRate) => amountUsd * sgdPerUsdRate;

    private static ZakatStockLineDto NotAttempted(Asset asset, ZakatAssetStatus status) =>
        new(asset.Id, asset.Symbol, asset.Name, asset.Currency, status, null, null, null, null, null, null, null, null, null);

    public async Task<IReadOnlyList<ZakatPaymentDto>> ListPaymentsAsync(CancellationToken cancellationToken) =>
        await db.ZakatPayments
            .OrderByDescending(p => p.PaidOn)
            .ThenByDescending(p => p.Id)
            .Select(p => new ZakatPaymentDto(p.Id, p.PaidOn, p.AmountSgd))
            .ToListAsync(cancellationToken);

    public async Task<ServiceResult<ZakatPaymentDto>> CreatePaymentAsync(
        CreateZakatPaymentRequest request, CancellationToken cancellationToken)
    {
        var errors = ValidatePayment(request.AmountSgd, request.PaidOn);
        if (errors.Count > 0)
        {
            return ServiceResult<ZakatPaymentDto>.Failure(ServiceError.Validation(errors));
        }

        var payment = new ZakatPayment { PaidOn = request.PaidOn, AmountSgd = request.AmountSgd };
        db.AddZakatPayment(payment);
        await db.SaveChangesAsync(cancellationToken);

        return ServiceResult<ZakatPaymentDto>.Success(new ZakatPaymentDto(payment.Id, payment.PaidOn, payment.AmountSgd));
    }

    public async Task<ServiceResult<ZakatPaymentDto>> UpdatePaymentAsync(
        int id, UpdateZakatPaymentRequest request, CancellationToken cancellationToken)
    {
        var payment = await db.FindZakatPaymentAsync(id, cancellationToken);
        if (payment is null)
        {
            return ServiceResult<ZakatPaymentDto>.Failure(ServiceError.NotFound());
        }

        var errors = ValidatePayment(request.AmountSgd, request.PaidOn);
        if (errors.Count > 0)
        {
            return ServiceResult<ZakatPaymentDto>.Failure(ServiceError.Validation(errors));
        }

        payment.PaidOn = request.PaidOn;
        payment.AmountSgd = request.AmountSgd;
        await db.SaveChangesAsync(cancellationToken);

        return ServiceResult<ZakatPaymentDto>.Success(new ZakatPaymentDto(payment.Id, payment.PaidOn, payment.AmountSgd));
    }

    public async Task<bool> DeletePaymentAsync(int id, CancellationToken cancellationToken)
    {
        var payment = await db.FindZakatPaymentAsync(id, cancellationToken);
        if (payment is null)
        {
            return false;
        }

        db.RemoveZakatPayment(payment);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private Dictionary<string, string[]> ValidatePayment(decimal amountSgd, DateOnly paidOn)
    {
        var errors = new Dictionary<string, string[]>();

        if (amountSgd <= 0)
        {
            errors["amountSgd"] = ["Amount must be greater than zero."];
        }

        var today = ReportingClock.Today(timeProvider);
        if (paidOn > today)
        {
            errors["paidOn"] = ["Paid-on date cannot be in the future."];
        }

        return errors;
    }
}
