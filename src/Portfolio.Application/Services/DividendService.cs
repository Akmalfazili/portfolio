using Microsoft.EntityFrameworkCore;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Common;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services.Calculators;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services;

/// <summary>See <see cref="IDividendService"/>.</summary>
public sealed class DividendService(
    IPortfolioDbContext db,
    IDividendIncomeCalculator calculator,
    TimeProvider timeProvider) : IDividendService
{
    private const string ReportingCurrency = CostBasisTransactionFactory.ReportingCurrency;

    public async Task<ServiceResult<AssetDividendHistoryDto>> GetAssetDividendHistoryAsync(
        int assetId, CancellationToken cancellationToken)
    {
        var asset = await db.FindAssetAsync(assetId, cancellationToken);
        if (asset is null)
        {
            return ServiceResult<AssetDividendHistoryDto>.Failure(ServiceError.NotFound());
        }

        if (asset.AssetClass != AssetClass.Stock)
        {
            return ServiceResult<AssetDividendHistoryDto>.Failure(ServiceError.Validation(
                new Dictionary<string, string[]>
                {
                    ["assetClass"] =
                    [
                        "Dividend history is only available for Stock assets. Crypto pays no " +
                        "dividends and is out of scope, by design.",
                    ],
                }));
        }

        var transactions = await db.Transactions
            .Where(t => t.AssetId == assetId)
            .OrderBy(t => t.TradeDate)
            .ThenBy(t => t.Id)
            .ToListAsync(cancellationToken);

        var events = await db.DividendEvents
            .Where(d => d.AssetId == assetId)
            .OrderBy(d => d.ExDate)
            .ToListAsync(cancellationToken);

        var state = await db.AssetDividendStates
            .FirstOrDefaultAsync(s => s.AssetId == assetId, cancellationToken);
        var status = CoverageStatusFor(state);

        var fxRates = await LoadFxRatesAsync(asset.Currency, cancellationToken);
        var lines = calculator.Calculate(transactions, ToCalculatorInputs(events, asset.Currency, fxRates));

        var (trailing12Month, allTime) = Totals(lines, status);

        var payments = lines
            .Select(l => new DividendPaymentDto(
                l.ExDate,
                DisplayRounding.Price(l.AmountPerShareNative),
                l.Currency,
                l.UnitsHeldAtExDate,
                DisplayRounding.Money(l.IncomeUsd)))
            .OrderByDescending(p => p.ExDate)
            .ToList();

        return ServiceResult<AssetDividendHistoryDto>.Success(new AssetDividendHistoryDto(
            asset.Id, asset.Symbol, asset.Name, asset.Currency, trailing12Month, allTime, status, payments));
    }

    public async Task<IReadOnlyDictionary<int, AssetDividendSummary>> GetSummariesAsync(
        IReadOnlyCollection<int> stockAssetIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, AssetDividendSummary>();
        if (stockAssetIds.Count == 0)
        {
            return result;
        }

        var assets = await db.Assets
            .Where(a => stockAssetIds.Contains(a.Id))
            .ToListAsync(cancellationToken);

        var transactionsByAsset = (await db.Transactions
                .Where(t => stockAssetIds.Contains(t.AssetId))
                .OrderBy(t => t.TradeDate)
                .ThenBy(t => t.Id)
                .ToListAsync(cancellationToken))
            .GroupBy(t => t.AssetId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var eventsByAsset = (await db.DividendEvents
                .Where(d => stockAssetIds.Contains(d.AssetId))
                .OrderBy(d => d.ExDate)
                .ToListAsync(cancellationToken))
            .GroupBy(d => d.AssetId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var statesByAsset = await db.AssetDividendStates
            .Where(s => stockAssetIds.Contains(s.AssetId))
            .ToDictionaryAsync(s => s.AssetId, cancellationToken);

        var fxRatesByCurrency = new Dictionary<string, IReadOnlyList<FxRate>>();

        foreach (var asset in assets)
        {
            if (!transactionsByAsset.TryGetValue(asset.Id, out var transactions))
            {
                // No activity for this asset — PortfolioSummaryService already excludes it from
                // the holdings list this feeds, so its absence here is consistent, not a bug.
                continue;
            }

            var events = eventsByAsset.TryGetValue(asset.Id, out var e) ? e : [];
            statesByAsset.TryGetValue(asset.Id, out var state);
            var status = CoverageStatusFor(state);

            IReadOnlyList<FxRate> fxRates = asset.Currency == ReportingCurrency
                ? []
                : await LoadCachedFxRatesAsync(fxRatesByCurrency, asset.Currency, cancellationToken);

            var lines = calculator.Calculate(transactions, ToCalculatorInputs(events, asset.Currency, fxRates));
            var (trailing12Month, allTime) = Totals(lines, status);

            result[asset.Id] = new AssetDividendSummary(trailing12Month, allTime, status);
        }

        return result;
    }

    /// <summary>
    /// Converts each stored <see cref="DividendEvent"/> (native currency) into the calculator's
    /// currency-agnostic input, resolving the FX rate for the event's own ex-date — never today's
    /// — exactly the historical-rate rule this codebase applies everywhere else.
    /// </summary>
    private static IReadOnlyList<DividendIncomeInput> ToCalculatorInputs(
        IReadOnlyList<DividendEvent> events, string assetCurrency, IReadOnlyList<FxRate> fxRatesAscending) =>
        events
            .Select(e => new DividendIncomeInput(
                e.ExDate,
                e.AmountPerShare,
                e.Currency,
                assetCurrency == ReportingCurrency
                    ? e.AmountPerShare
                    : e.AmountPerShare / FxRateResolver.Resolve(fxRatesAscending, e.ExDate)))
            .ToList();

    /// <summary>
    /// Trailing-12-month and all-time USD totals, rounded once at this boundary. Both are null
    /// exactly when <paramref name="status"/> is <see cref="DividendCoverageStatus.NotYetFetched"/>
    /// — nothing has ever been computed, so a real (possibly zero) number here would misrepresent
    /// ignorance as a known answer. A <see cref="DividendCoverageStatus.FetchFailed"/> asset still
    /// reports whatever was computed from its last successful attempt, which may be incomplete but
    /// is not fabricated.
    /// </summary>
    private (decimal? Trailing12Month, decimal? AllTime) Totals(
        IReadOnlyList<DividendIncomeLine> lines, DividendCoverageStatus status)
    {
        if (status == DividendCoverageStatus.NotYetFetched)
        {
            return (null, null);
        }

        var today = ReportingClock.Today(timeProvider);
        var trailing12MonthStart = today.AddYears(-1);

        var trailing12Month = DisplayRounding.Money(
            lines.Where(l => l.ExDate >= trailing12MonthStart && l.ExDate <= today).Sum(l => l.IncomeUsd));
        var allTime = DisplayRounding.Money(lines.Sum(l => l.IncomeUsd));

        return (trailing12Month, allTime);
    }

    private static DividendCoverageStatus CoverageStatusFor(AssetDividendState? state) => state switch
    {
        null => DividendCoverageStatus.NotYetFetched,
        { LastAttemptedAt: null } => DividendCoverageStatus.NotYetFetched,
        { LastRunSuccess: true } => DividendCoverageStatus.Covered,
        _ => DividendCoverageStatus.FetchFailed,
    };

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

    private async Task<IReadOnlyList<FxRate>> LoadCachedFxRatesAsync(
        Dictionary<string, IReadOnlyList<FxRate>> cache, string currency, CancellationToken cancellationToken)
    {
        if (!cache.TryGetValue(currency, out var rates))
        {
            rates = await LoadFxRatesAsync(currency, cancellationToken);
            cache[currency] = rates;
        }

        return rates;
    }
}
