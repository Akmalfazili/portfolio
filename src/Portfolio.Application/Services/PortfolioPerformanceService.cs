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
        var assetData = await LoadStockAssetDataAsync(cancellationToken);
        if (assetData.Count == 0)
        {
            return new AnnualReturnsDto([]);
        }

        // Cash flows are re-derived from the already-USD-converted CostBasisTransaction fields
        // (GrossAmountUsd / FeesUsd) computed once in LoadStockAssetDataAsync, rather than
        // resolving FX a second time here — the two are numerically identical, since
        // CostBasisTransactionFactory.ToUsd performs exactly this native/rate division.
        var cashFlows = new List<PortfolioCashFlow>();
        foreach (var data in assetData)
        {
            foreach (var t in data.CostBasisTransactions)
            {
                // A buy is a net investment into the portfolio (positive flow); a sell's net
                // proceeds leave the portfolio (negative flow) — the sign the TWR formula needs.
                var amountUsd = t.Type == TransactionType.Buy ? t.GrossAmountUsd + t.FeesUsd : -(t.GrossAmountUsd - t.FeesUsd);
                cashFlows.Add(new PortfolioCashFlow(t.TradeDate, amountUsd));
            }
        }

        var stepsByAsset = assetData.ToDictionary(d => d.Asset.Id, d => d.Steps);
        var closesByAsset = assetData.ToDictionary(d => d.Asset.Id, d => d.Closes);

        var dailyValues = BuildDailyPortfolioValues(stepsByAsset, closesByAsset);
        var annualReturns = annualReturnCalculator.Calculate(dailyValues, cashFlows);

        return new AnnualReturnsDto(
            annualReturns.Select(a => new AnnualReturnDto(a.Year, DisplayRounding.Percent(a.TimeWeightedReturnPercent))).ToList());
    }

    public async Task<PortfolioPerformanceDto> GetPortfolioPerformanceAsync(CancellationToken cancellationToken)
    {
        var assetData = await LoadStockAssetDataAsync(cancellationToken);
        if (assetData.Count == 0)
        {
            return new PortfolioPerformanceDto([], []);
        }

        var stepsByAsset = assetData.ToDictionary(d => d.Asset.Id, d => d.Steps);
        var closesByAsset = assetData.ToDictionary(d => d.Asset.Id, d => d.Closes);
        var symbolByAsset = assetData.ToDictionary(d => d.Asset.Id, d => d.Asset.Symbol);

        return BuildPortfolioPerformance(stepsByAsset, closesByAsset, symbolByAsset);
    }

    /// <summary>
    /// One <see cref="Domain.Entities.Asset"/>'s already-USD-converted transactions, running
    /// cost-basis steps and USD close series — the shared load-and-convert step behind both
    /// <see cref="GetAnnualReturnsAsync"/> and <see cref="GetPortfolioPerformanceAsync"/>, so FX
    /// resolution and cost-basis calculation happen exactly once per asset regardless of which
    /// (or both) of those callers run.
    /// </summary>
    private sealed record StockAssetData(
        Asset Asset,
        IReadOnlyList<CostBasisTransaction> CostBasisTransactions,
        IReadOnlyList<CostBasisStep> Steps,
        IReadOnlyList<(DateOnly Date, decimal CloseUsd)> Closes);

    /// <summary>
    /// Loads every <see cref="AssetClass.Stock"/> asset that has at least one transaction — the
    /// same asset set <see cref="GetAnnualReturnsAsync"/> has always used (no <c>IsActive</c>
    /// filter) — along with its cost-basis steps and USD close series. An asset with transactions
    /// but zero <see cref="Domain.Entities.PriceHistory"/> rows still gets an entry, with an empty
    /// <see cref="StockAssetData.Closes"/> list, so <see cref="GetPortfolioPerformanceAsync"/> can
    /// detect it for <see cref="PortfolioPerformanceDto.UnchartedSymbols"/>. Returns an empty list
    /// if no stock asset has any transaction.
    /// </summary>
    private async Task<IReadOnlyList<StockAssetData>> LoadStockAssetDataAsync(CancellationToken cancellationToken)
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
            return [];
        }

        var priceHistoryByAsset = (await db.PriceHistories
                .Where(p => assetIds.Contains(p.AssetId))
                .OrderBy(p => p.Date)
                .ToListAsync(cancellationToken))
            .GroupBy(p => p.AssetId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<StockAssetData>();

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
            var steps = costBasisCalculator.Calculate(costBasisTransactions).ToList();

            var closes = priceHistoryByAsset.TryGetValue(asset.Id, out var history)
                ? history.Select(p => (p.Date, CloseUsd: ToUsd(p.Close, asset.Currency, fxRates, p.Date))).ToList()
                : [];

            result.Add(new StockAssetData(asset, costBasisTransactions, steps, closes));
        }

        return result;
    }

    /// <summary>
    /// Merges every stock asset's cost-basis steps (quantity held) and USD close series onto one
    /// timeline — the union of every date any asset has a close — carrying forward each asset's
    /// last known quantity and close for dates its own market was shut (a weekend, a Z74 holiday
    /// while NYSE traded) rather than dropping it from that day's total.
    /// </summary>
    private static IReadOnlyList<DailyPortfolioValue> BuildDailyPortfolioValues(
        IReadOnlyDictionary<int, IReadOnlyList<CostBasisStep>> stepsByAsset,
        IReadOnlyDictionary<int, IReadOnlyList<(DateOnly Date, decimal CloseUsd)>> closesByAsset)
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

    /// <summary>
    /// The portfolio-wide counterpart to <see cref="BuildDailyPortfolioValues"/>: the same
    /// union-of-close-dates timeline and per-asset carry-forward walk, but tracking cost basis
    /// alongside market value and applying the inclusion rule from
    /// <see cref="PortfolioPerformanceDto"/> — an asset contributes to a date's totals only once it
    /// has <em>both</em> a cost-basis step and a close on or before that date, never one without the
    /// other. A point is only emitted for a date at least one asset contributes to; a stretch where
    /// every position is fully closed still emits zero-valued points rather than nothing, since a
    /// closed position (quantity zero, cost basis zero) is a real contribution of zero, not an
    /// absent one.
    /// </summary>
    private static PortfolioPerformanceDto BuildPortfolioPerformance(
        IReadOnlyDictionary<int, IReadOnlyList<CostBasisStep>> stepsByAsset,
        IReadOnlyDictionary<int, IReadOnlyList<(DateOnly Date, decimal CloseUsd)>> closesByAsset,
        IReadOnlyDictionary<int, string> symbolByAsset)
    {
        var allDates = closesByAsset.Values
            .SelectMany(c => c.Select(x => x.Date))
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        var stepCursor = stepsByAsset.Keys.ToDictionary(id => id, _ => 0);
        var closeCursor = closesByAsset.Keys.ToDictionary(id => id, _ => 0);
        var lastStep = stepsByAsset.Keys.ToDictionary(id => id, _ => (CostBasisStep?)null);
        var lastCloseUsd = closesByAsset.Keys.ToDictionary(id => id, _ => (decimal?)null);

        var points = new List<PerformancePointDto>();

        foreach (var date in allDates)
        {
            var costTotal = 0m;
            var marketTotal = 0m;
            var anyContributed = false;

            foreach (var assetId in stepsByAsset.Keys)
            {
                var steps = stepsByAsset[assetId];
                var stepIdx = stepCursor[assetId];
                while (stepIdx < steps.Count && steps[stepIdx].TradeDate <= date)
                {
                    lastStep[assetId] = steps[stepIdx];
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

                // Inclusion rule: only once this asset has both a cost-basis step and a close as of
                // this date does it contribute to either line — never cost without market value or
                // vice versa.
                if (lastStep[assetId] is { } step && lastCloseUsd[assetId] is { } closeUsd)
                {
                    costTotal += step.CostBasisUsd;
                    marketTotal += step.QuantityHeld * closeUsd;
                    anyContributed = true;
                }
            }

            if (anyContributed)
            {
                points.Add(new PerformancePointDto(date, DisplayRounding.Money(costTotal), DisplayRounding.Money(marketTotal)));
            }
        }

        var unchartedSymbols = stepsByAsset
            .Where(kvp => kvp.Value.Count > 0
                && kvp.Value[^1].QuantityHeld > 0m
                && closesByAsset[kvp.Key].Count == 0)
            .Select(kvp => symbolByAsset[kvp.Key])
            .OrderBy(symbol => symbol, StringComparer.Ordinal)
            .ToList();

        return new PortfolioPerformanceDto(points, unchartedSymbols);
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
