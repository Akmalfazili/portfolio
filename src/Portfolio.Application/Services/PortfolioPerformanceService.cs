using Microsoft.EntityFrameworkCore;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Common;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services.Calculators;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services;

/// <summary>See <see cref="IPortfolioPerformanceService"/>.</summary>
public sealed class PortfolioPerformanceService(
    IPortfolioDbContext db,
    ICostBasisCalculator costBasisCalculator,
    IPerformanceSeriesBuilder seriesBuilder,
    IAnnualReturnCalculator annualReturnCalculator) : IPortfolioPerformanceService
{
    private const string ReportingCurrency = CostBasisTransactionFactory.ReportingCurrency;

    public async Task<ServiceResult<AssetPerformanceDto>> GetAssetPerformanceAsync(
        int assetId, CancellationToken cancellationToken)
    {
        var asset = await db.FindAssetAsync(assetId, cancellationToken);
        if (asset is null)
        {
            return ServiceResult<AssetPerformanceDto>.Failure(ServiceError.NotFound());
        }

        if (asset.AssetClass != AssetClass.Stock)
        {
            return ServiceResult<AssetPerformanceDto>.Failure(ServiceError.Validation(
                new Dictionary<string, string[]>
                {
                    ["assetClass"] =
                    [
                        "Performance series are only available for Stock assets. Crypto is gain/loss " +
                        "only and keeps no price history, by design.",
                    ],
                }));
        }

        var transactions = await db.Transactions
            .Where(t => t.AssetId == assetId)
            .OrderBy(t => t.TradeDate)
            .ThenBy(t => t.Id)
            .ToListAsync(cancellationToken);

        if (transactions.Count == 0)
        {
            return ServiceResult<AssetPerformanceDto>.Success(
                new AssetPerformanceDto(asset.Id, asset.Symbol, asset.Name, asset.Currency, []));
        }

        var fxRates = await LoadFxRatesAsync(asset.Currency, cancellationToken);

        var costBasisTransactions = transactions
            .Select(t => CostBasisTransactionFactory.ToUsd(t, asset.Currency, fxRates))
            .ToList();
        var steps = costBasisCalculator.Calculate(costBasisTransactions);

        var priceHistory = await db.PriceHistories
            .Where(p => p.AssetId == assetId)
            .OrderBy(p => p.Date)
            .ToListAsync(cancellationToken);

        var dailyCloseUsd = priceHistory
            .Select(p => (p.Date, CloseUsd: ToUsd(p.Close, asset.Currency, fxRates, p.Date)))
            .ToList();

        var points = seriesBuilder.Build(steps, dailyCloseUsd)
            .Select(p => new PerformancePointDto(p.Date, DisplayRounding.Money(p.CostBasisUsd), DisplayRounding.Money(p.MarketValueUsd)))
            .ToList();

        return ServiceResult<AssetPerformanceDto>.Success(
            new AssetPerformanceDto(asset.Id, asset.Symbol, asset.Name, asset.Currency, points));
    }

    public async Task<AnnualReturnsDto> GetAnnualReturnsAsync(CancellationToken cancellationToken)
    {
        var stockAssets = await db.Assets
            .Where(a => a.AssetClass == AssetClass.Stock)
            .ToListAsync(cancellationToken);
        var assetIds = stockAssets.Select(a => a.Id).ToList();

        var transactionsByAsset = (await db.Transactions
                .Where(t => assetIds.Contains(t.AssetId))
                .OrderBy(t => t.TradeDate)
                .ThenBy(t => t.Id)
                .ToListAsync(cancellationToken))
            .GroupBy(t => t.AssetId)
            .ToDictionary(g => g.Key, g => g.ToList());

        if (transactionsByAsset.Count == 0)
        {
            return new AnnualReturnsDto([]);
        }

        var priceHistoryByAsset = (await db.PriceHistories
                .Where(p => assetIds.Contains(p.AssetId))
                .OrderBy(p => p.Date)
                .ToListAsync(cancellationToken))
            .GroupBy(p => p.AssetId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var cashFlows = new List<PortfolioCashFlow>();
        var quantityStepsByAsset = new Dictionary<int, List<CostBasisStep>>();
        var dailyCloseUsdByAsset = new Dictionary<int, List<(DateOnly Date, decimal CloseUsd)>>();

        foreach (var asset in stockAssets)
        {
            if (!transactionsByAsset.TryGetValue(asset.Id, out var assetTransactions))
            {
                continue;
            }

            var fxRates = await LoadFxRatesAsync(asset.Currency, cancellationToken);

            var costBasisTransactions = assetTransactions
                .Select(t => CostBasisTransactionFactory.ToUsd(t, asset.Currency, fxRates))
                .ToList();
            quantityStepsByAsset[asset.Id] = costBasisCalculator.Calculate(costBasisTransactions).ToList();

            foreach (var t in assetTransactions)
            {
                var rate = asset.Currency == ReportingCurrency ? 1m : FxRateResolver.Resolve(fxRates, t.TradeDate);
                var grossUsd = t.Quantity * t.PricePerUnit / rate;
                var feesUsd = t.Fees / rate;
                // A buy is a net investment into the portfolio (positive flow); a sell's net
                // proceeds leave the portfolio (negative flow) — the sign the TWR formula needs.
                var amountUsd = t.Type == TransactionType.Buy ? grossUsd + feesUsd : -(grossUsd - feesUsd);
                cashFlows.Add(new PortfolioCashFlow(t.TradeDate, amountUsd));
            }

            dailyCloseUsdByAsset[asset.Id] = priceHistoryByAsset.TryGetValue(asset.Id, out var history)
                ? history.Select(p => (p.Date, CloseUsd: ToUsd(p.Close, asset.Currency, fxRates, p.Date))).ToList()
                : [];
        }

        var dailyValues = BuildDailyPortfolioValues(quantityStepsByAsset, dailyCloseUsdByAsset);
        var annualReturns = annualReturnCalculator.Calculate(dailyValues, cashFlows);

        return new AnnualReturnsDto(
            annualReturns.Select(a => new AnnualReturnDto(a.Year, DisplayRounding.Percent(a.TimeWeightedReturnPercent))).ToList());
    }

    /// <summary>
    /// Merges every stock asset's cost-basis steps (quantity held) and USD close series onto one
    /// timeline — the union of every date any asset has a close — carrying forward each asset's
    /// last known quantity and close for dates its own market was shut (a weekend, a Z74 holiday
    /// while NYSE traded) rather than dropping it from that day's total.
    /// </summary>
    private static IReadOnlyList<DailyPortfolioValue> BuildDailyPortfolioValues(
        IReadOnlyDictionary<int, List<CostBasisStep>> stepsByAsset,
        IReadOnlyDictionary<int, List<(DateOnly Date, decimal CloseUsd)>> closesByAsset)
    {
        var allDates = closesByAsset.Values
            .SelectMany(c => c.Select(x => x.Date))
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        if (allDates.Count == 0)
        {
            return [];
        }

        var stepCursor = stepsByAsset.Keys.ToDictionary(id => id, _ => 0);
        var closeCursor = closesByAsset.Keys.ToDictionary(id => id, _ => 0);
        var lastQuantity = stepsByAsset.Keys.ToDictionary(id => id, _ => 0m);
        var lastCloseUsd = closesByAsset.Keys.ToDictionary(id => id, _ => (decimal?)null);

        var results = new List<DailyPortfolioValue>(allDates.Count);

        foreach (var date in allDates)
        {
            var total = 0m;

            foreach (var assetId in stepsByAsset.Keys)
            {
                var steps = stepsByAsset[assetId];
                var stepIdx = stepCursor[assetId];
                while (stepIdx < steps.Count && steps[stepIdx].TradeDate <= date)
                {
                    lastQuantity[assetId] = steps[stepIdx].QuantityHeld;
                    stepIdx++;
                }
                stepCursor[assetId] = stepIdx;

                var closes = closesByAsset[assetId];
                var closeIdx = closeCursor[assetId];
                while (closeIdx < closes.Count && closes[closeIdx].Date <= date)
                {
                    lastCloseUsd[assetId] = closes[closeIdx].CloseUsd;
                    closeIdx++;
                }
                closeCursor[assetId] = closeIdx;

                if (lastCloseUsd[assetId] is { } closeUsd)
                {
                    total += lastQuantity[assetId] * closeUsd;
                }
            }

            results.Add(new DailyPortfolioValue(date, total));
        }

        return results;
    }

    private async Task<IReadOnlyList<FxRate>> LoadFxRatesAsync(string currency, CancellationToken cancellationToken)
    {
        if (currency == ReportingCurrency)
        {
            return [];
        }

        return await db.FxRates
            .Where(f => f.Base == ReportingCurrency && f.Quote == currency)
            .OrderBy(f => f.Date)
            .ToListAsync(cancellationToken);
    }

    private static decimal ToUsd(decimal nativeAmount, string currency, IReadOnlyList<FxRate> fxRatesAscending, DateOnly date)
    {
        if (currency == ReportingCurrency)
        {
            return nativeAmount;
        }

        return nativeAmount / FxRateResolver.Resolve(fxRatesAscending, date);
    }
}
