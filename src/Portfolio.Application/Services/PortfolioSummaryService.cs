using Microsoft.EntityFrameworkCore;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services.Calculators;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services;

/// <summary>See <see cref="IPortfolioSummaryService"/>.</summary>
public sealed class PortfolioSummaryService(
    IPortfolioDbContext db, TimeProvider timeProvider, ICostBasisCalculator costBasisCalculator)
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

        return new PortfolioSummaryDto(
            assetClass,
            totalCostBasis,
            totalMarketValue,
            totalUnrealized,
            totalCostBasis > 0m ? DisplayRounding.Percent(totalUnrealized / totalCostBasis * 100m) : null,
            totalRealized,
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
                total > 0m ? DisplayRounding.Percent(h.MarketValueUsd / total * 100m) : 0m))
            .ToList();

        return new PortfolioAllocationDto(assetClass, total, items);
    }

    private async Task<IReadOnlyList<HoldingDto>> BuildHoldingsAsync(AssetClass assetClass, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

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

        var fxRatesByCurrency = await LoadFxRatesAsync(assets, transactionsByAsset.Keys, cancellationToken);

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

            decimal? currentPriceUsd = null;
            var marketValueUsd = 0m;

            if (quote is not null)
            {
                var todayRate = asset.Currency == ReportingCurrency
                    ? 1m
                    : FxRateResolver.Resolve(fxRates, today);
                currentPriceUsd = quote.Price / todayRate;
                marketValueUsd = finalStep.QuantityHeld * currentPriceUsd.Value;
            }

            var costBasisUsd = DisplayRounding.Money(finalStep.CostBasisUsd);
            marketValueUsd = DisplayRounding.Money(marketValueUsd);
            var unrealizedPnlUsd = marketValueUsd - costBasisUsd;
            var unrealizedPnlPercent = costBasisUsd > 0m
                ? DisplayRounding.Percent(unrealizedPnlUsd / costBasisUsd * 100m)
                : (decimal?)null;

            holdings.Add(new HoldingDto(
                asset.Id,
                asset.Symbol,
                asset.Name,
                asset.AssetClass,
                asset.Currency,
                finalStep.QuantityHeld,
                costBasisUsd,
                quote?.Price,
                DisplayRounding.Price(currentPriceUsd),
                quote?.AsOf,
                marketValueUsd,
                unrealizedPnlUsd,
                unrealizedPnlPercent,
                DisplayRounding.Money(finalStep.RealizedPnlUsd)));
        }

        return holdings;
    }

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
