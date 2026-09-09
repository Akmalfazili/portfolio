using Microsoft.EntityFrameworkCore;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services.Calculators;
using Portfolio.Application.Services.Calendar;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services;

/// <summary>See <see cref="IPortfolioSummaryService"/>.</summary>
public sealed class PortfolioSummaryService(
    IPortfolioDbContext db,
    TimeProvider timeProvider,
    ICostBasisCalculator costBasisCalculator,
    IMarketCalendar calendar,
    IDividendService dividendService)
    : IPortfolioSummaryService
{
    private const string ReportingCurrency = CostBasisTransactionFactory.ReportingCurrency;

    public async Task<PortfolioSummaryDto> GetSummaryAsync(AssetClass assetClass, CancellationToken cancellationToken)
    {
        var holdings = await BuildHoldingsAsync(assetClass, cancellationToken);

        // Sum the already-rounded per-holding figures (DisplayRounding was applied when each
        // HoldingDto was built) so the totals foot exactly to the rows a caller can see.
        var totalCostBasis = holdings.Sum(h => h.CostBasisUsd);
        var totalMarketValue = holdings.Sum(h => h.MarketValueUsd);
        var totalUnrealized = holdings.Sum(h => h.UnrealizedPnlUsd);
        var totalRealized = holdings.Sum(h => h.RealizedPnlUsd);

        // D17: only currently-held positions need a price at all — a fully closed position has
        // zero market value by construction and nothing to caveat.
        var unpricedHoldingsCount = holdings.Count(h => h.QuantityHeld > 0m && h.PriceSource is null);

        // Dividends are Stock-only — a Crypto summary reports null totals (the concept does not
        // apply) and a DividendsUncoveredCount of zero, which falls out naturally below since no
        // holding in a Crypto summary has AssetClass.Stock.
        decimal? totalDividends12m = assetClass == AssetClass.Stock
            ? holdings.Sum(h => h.DividendsTrailing12MonthUsd ?? 0m)
            : null;
        decimal? totalDividendsAllTime = assetClass == AssetClass.Stock
            ? holdings.Sum(h => h.DividendsAllTimeUsd ?? 0m)
            : null;
        var dividendsUncoveredCount = holdings.Count(
            h => h.AssetClass == AssetClass.Stock && h.DividendCoverageStatus != DividendCoverageStatus.Covered);

        return new PortfolioSummaryDto(
            assetClass,
            totalCostBasis,
            totalMarketValue,
            totalUnrealized,
            totalCostBasis > 0m ? DisplayRounding.Percent(totalUnrealized / totalCostBasis * 100m) : null,
            totalRealized,
            unpricedHoldingsCount,
            totalDividends12m,
            totalDividendsAllTime,
            dividendsUncoveredCount,
            holdings);
    }

    public async Task<PortfolioAllocationDto> GetAllocationAsync(AssetClass assetClass, CancellationToken cancellationToken)
    {
        var holdings = await BuildHoldingsAsync(assetClass, cancellationToken);
        var open = holdings.Where(h => h.QuantityHeld > 0m).ToList();
        var total = open.Sum(h => h.MarketValueUsd);

        var items = open
            .Select(h => new AllocationItemDto(
                h.AssetId,
                h.Symbol,
                h.Name,
                h.MarketValueUsd,
                total > 0m ? DisplayRounding.Percent(h.MarketValueUsd / total * 100m) : 0m,
                // D17 residual. Note this is the same PriceSource null-check the summary uses, on
                // the same holdings built by the same BuildHoldingsAsync — so the pie and the
                // tiles can never disagree about which holdings are unpriced.
                h.PriceSource is not null))
            .ToList();

        return new PortfolioAllocationDto(
            assetClass,
            total,
            items,
            items.Count(i => !i.HasPrice));
    }

    private async Task<IReadOnlyList<HoldingDto>> BuildHoldingsAsync(AssetClass assetClass, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var today = ReportingClock.Today(timeProvider);

        var assets = await db.Assets
            .Where(a => a.AssetClass == assetClass)
            .ToListAsync(cancellationToken);
        var assetIds = assets.Select(a => a.Id).ToList();

        var transactionsByAsset = (await db.Transactions
                .Where(t => assetIds.Contains(t.AssetId))
                .OrderBy(t => t.TradeDate)
                .ThenBy(t => t.Id)
                .ToListAsync(cancellationToken))
            .GroupBy(t => t.AssetId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var quotesByAsset = await db.PriceQuotes
            .Where(q => assetIds.Contains(q.AssetId))
            .ToDictionaryAsync(q => q.AssetId, cancellationToken);

        // D20: the last-close fallback source, keyed to one row per asset (the newest date). Only
        // stocks ever have PriceHistory rows at all (crypto keeps none, by decision), so this is a
        // no-op dictionary for a crypto assetClass call — deliberately loaded generically rather
        // than special-cased so the fallback logic below does not need to know the asset class.
        var lastCloseByAsset = (await db.PriceHistories
                .Where(p => assetIds.Contains(p.AssetId))
                .OrderByDescending(p => p.Date)
                .ToListAsync(cancellationToken))
            .GroupBy(p => p.AssetId)
            .ToDictionary(g => g.Key, g => g.First());

        var fxRatesByCurrency = await LoadFxRatesAsync(assets, transactionsByAsset.Keys, cancellationToken);

        // Dividends are Stock-only — never even queried for a Crypto call, so every HoldingDto
        // built below simply gets the null triple for AssetClass.Crypto (see the ?? fallback
        // where each HoldingDto is constructed).
        var dividendsByAsset = assetClass == AssetClass.Stock
            ? await dividendService.GetSummariesAsync(transactionsByAsset.Keys.ToList(), cancellationToken)
            : new Dictionary<int, AssetDividendSummary>();

        var holdings = new List<HoldingDto>();

        foreach (var asset in assets)
        {
            if (!transactionsByAsset.TryGetValue(asset.Id, out var assetTransactions) || assetTransactions.Count == 0)
            {
                // No activity for this asset in this portfolio — nothing to report.
                continue;
            }

            IReadOnlyList<FxRate> fxRates = asset.Currency == ReportingCurrency
                ? []
                : fxRatesByCurrency[asset.Currency];

            var costBasisTransactions = assetTransactions
                .Select(t => CostBasisTransactionFactory.ToUsd(t, asset.Currency, fxRates))
                .ToList();
            var finalStep = costBasisCalculator.Calculate(costBasisTransactions)[^1];

            quotesByAsset.TryGetValue(asset.Id, out var quote);
            lastCloseByAsset.TryGetValue(asset.Id, out var lastClose);

            decimal? currentPriceNative = null;
            decimal? currentPriceUsd = null;
            DateTimeOffset? priceAsOf = null;
            PriceSource? priceSource = null;
            var marketValueUsd = 0m;

            // D4: a stored quote is only "live" if it belongs to the current trading session. See
            // IsQuoteLive — a quote from an earlier session is a close, and is reported as one.
            var quoteIsLive = quote is not null && IsQuoteLive(asset, quote.AsOf, now);

            // D4: a quote that is NOT live is still a real price, and is usually newer than the
            // last backfilled close (backfill runs daily; quotes go stale within hours). Treat it
            // as another close candidate rather than discarding it, and let the newer of the two
            // win, so falling back never shows an older number than the one already on hand.
            //
            // D48: strictly newer, not "newer or equal". A PriceHistory row and a stale PriceQuote
            // stamped the same date are NOT the same kind of number — PriceHistory is the official
            // session close from /time_series, while a stale quote is an arbitrary mid-session
            // snapshot that happened to be the last one polled before the market-hours gate in
            // PriceRefreshService.RunCycleAsync stopped refreshing it. Twelve Data's quote.timestamp
            // is the daily bar's OPEN, not the sample instant, so every US quote's date collides
            // with that same day's close once the evening backfill lands. A same-date tie must go
            // to PriceHistory, or the app reports the opening-bell snapshot as tonight's close on
            // every US symbol, every single night.
            var staleQuoteDate = !quoteIsLive && quote is not null
                ? DateOnly.FromDateTime(quote.AsOf.UtcDateTime)
                : (DateOnly?)null;
            var useStaleQuoteAsClose = staleQuoteDate is { } sqd
                && (lastClose is null || sqd > lastClose.Date);

            if (quoteIsLive)
            {
                var todayRate = asset.Currency == ReportingCurrency
                    ? 1m
                    : FxRateResolver.Resolve(fxRates, today);
                currentPriceNative = quote!.Price;
                currentPriceUsd = quote.Price / todayRate;
                priceAsOf = quote.AsOf;
                priceSource = PriceSource.Live;
                marketValueUsd = finalStep.QuantityHeld * currentPriceUsd.Value;
            }
            else if (useStaleQuoteAsClose && (asset.Currency == ReportingCurrency || fxRates.Count > 0))
            {
                // D4: the price Yahoo/Twelve Data handed us, from a session that has already
                // ended. Converted at that session's own FX rate, not today's — the same rule the
                // PriceHistory branch below follows, and the reason this cannot just fall through
                // to the Live branch with a different label.
                var quoteDate = staleQuoteDate!.Value;
                var quoteRate = asset.Currency == ReportingCurrency
                    ? 1m
                    : FxRateResolver.Resolve(fxRates, quoteDate);
                currentPriceNative = quote!.Price;
                currentPriceUsd = quote.Price / quoteRate;
                priceAsOf = quote.AsOf;
                priceSource = PriceSource.Close;
                marketValueUsd = finalStep.QuantityHeld * currentPriceUsd.Value;
            }
            else if (lastClose is not null && (asset.Currency == ReportingCurrency || fxRates.Count > 0))
            {
                // D20: no live quote yet (market closed, or the asset was only just added and the
                // next refresh cycle has not ticked) — fall back to the newest stored daily close
                // rather than reporting zero market value for a real position. priceAsOf carries
                // the CLOSE'S OWN date, never "now", and PriceSource.Close tells the caller exactly
                // which state this is rather than leaving it to infer from the date alone — the
                // D4 mistake (a stale price wearing a fresh-looking timestamp) must not repeat.
                // Converted at the close's own date's rate, not today's — consistent with the rest
                // of this codebase's "historical series use historical rates" rule (FxRateResolver
                // carries the nearest prior rate forward, or the earliest rate back, so it only
                // throws for a currency with zero stored rates at all). The fxRates.Count > 0 guard
                // is defensive: reaching this branch already implies at least one transaction in
                // this currency converted successfully via CostBasisTransactionFactory above, which
                // itself requires a non-empty rate list or throws — so this is currently
                // unreachable in practice, kept only so a future change to that invariant fails
                // safe (unpriced, see PortfolioSummaryDto.UnpricedHoldingsCount) rather than
                // mis-converting at an implicit 1:1 rate.
                var closeRate = asset.Currency == ReportingCurrency
                    ? 1m
                    : FxRateResolver.Resolve(fxRates, lastClose.Date);
                currentPriceNative = lastClose.Close;
                currentPriceUsd = lastClose.Close / closeRate;
                priceAsOf = new DateTimeOffset(lastClose.Date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                priceSource = PriceSource.Close;
                marketValueUsd = finalStep.QuantityHeld * currentPriceUsd.Value;
            }

            var costBasisUsd = DisplayRounding.Money(finalStep.CostBasisUsd);

            // Null on a fully closed position rather than 0m — see HoldingDto.AverageCostUsd.
            // Divides the already-rounded costBasisUsd above (not finalStep.CostBasisUsd) so the
            // displayed average cost is arithmetically consistent with the displayed cost basis.
            var averageCostUsd = finalStep.QuantityHeld > 0m
                ? DisplayRounding.Price(costBasisUsd / finalStep.QuantityHeld)
                : (decimal?)null;

            marketValueUsd = DisplayRounding.Money(marketValueUsd);
            var unrealizedPnlUsd = marketValueUsd - costBasisUsd;
            var unrealizedPnlPercent = costBasisUsd > 0m
                ? DisplayRounding.Percent(unrealizedPnlUsd / costBasisUsd * 100m)
                : (decimal?)null;

            dividendsByAsset.TryGetValue(asset.Id, out var dividends);

            holdings.Add(new HoldingDto(
                asset.Id,
                asset.Symbol,
                asset.Name,
                asset.AssetClass,
                asset.Currency,
                finalStep.QuantityHeld,
                costBasisUsd,
                averageCostUsd,
                currentPriceNative,
                DisplayRounding.Price(currentPriceUsd),
                priceAsOf,
                priceSource,
                marketValueUsd,
                unrealizedPnlUsd,
                unrealizedPnlPercent,
                DisplayRounding.Money(finalStep.RealizedPnlUsd),
                dividends?.Trailing12MonthIncomeUsd,
                dividends?.AllTimeIncomeUsd,
                dividends?.CoverageStatus));
        }

        return holdings;
    }

    /// <summary>D4 — see <see cref="QuoteFreshness"/>, which owns this rule so the read path here
    /// and the SignalR broadcast path in <c>PriceRefreshService</c> cannot drift apart.</summary>
    private bool IsQuoteLive(Asset asset, DateTimeOffset quoteAsOf, DateTimeOffset now) =>
        QuoteFreshness.Classify(calendar, asset.QuoteProviderKind, quoteAsOf, now) == PriceSource.Live;

    private async Task<Dictionary<string, List<FxRate>>> LoadFxRatesAsync(
        List<Asset> assets, IEnumerable<int> assetIdsWithTransactions, CancellationToken cancellationToken)
    {
        var idsWithTransactions = assetIdsWithTransactions.ToHashSet();
        var currencies = assets
            .Where(a => idsWithTransactions.Contains(a.Id) && a.Currency != ReportingCurrency)
            .Select(a => a.Currency)
            .Distinct()
            .ToList();

        var result = new Dictionary<string, List<FxRate>>();
        foreach (var currency in currencies)
        {
            result[currency] = await db.FxRates
                .Where(f => f.Base == ReportingCurrency && f.Quote == currency)
                .OrderBy(f => f.Date)
                .ToListAsync(cancellationToken);
        }

        return result;
    }
}
