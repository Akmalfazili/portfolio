using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Services.Calculators;
using Portfolio.Application.Services.Calendar;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services;

/// <summary>See <see cref="IRefreshCatchUpService"/>.</summary>
public sealed class RefreshCatchUpService(
    IPortfolioDbContext db,
    IMarketCalendar calendar,
    TimeProvider timeProvider,
    IOptions<PriceBackfillOptions> priceBackfillOptions,
    IOptions<DividendBackfillOptions> dividendBackfillOptions) : IRefreshCatchUpService
{
    private const string ReportingCurrency = "USD";

    public async Task<RefreshCatchUpCandidates> PlanAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        // Scope: active Stock assets with at least one transaction — identical filter to
        // PriceBackfillService/DividendBackfillService's own. Crypto never appears (locked
        // decision, see the class remarks).
        var assets = await db.Assets
            .Where(a => a.IsActive && a.AssetClass == AssetClass.Stock)
            .ToListAsync(cancellationToken);

        var earliestTradeDateByAsset = await db.Transactions
            .GroupBy(t => t.AssetId)
            .Select(g => new { AssetId = g.Key, Earliest = g.Min(t => t.TradeDate) })
            .ToDictionaryAsync(x => x.AssetId, x => x.Earliest, cancellationToken);

        assets = assets.Where(a => earliestTradeDateByAsset.ContainsKey(a.Id)).ToList();

        if (assets.Count == 0)
        {
            return new RefreshCatchUpCandidates(
                new PriceHistoryCatchUpCandidates([], [], [], [], []),
                new DividendsCatchUpCandidates([], []));
        }

        var priceHistoryPlan = await PlanPriceHistoryAsync(assets, earliestTradeDateByAsset, now, cancellationToken);
        var dividendsPlan = await PlanDividendsAsync(assets, earliestTradeDateByAsset, now, cancellationToken);

        return new RefreshCatchUpCandidates(priceHistoryPlan, dividendsPlan);
    }

    private async Task<PriceHistoryCatchUpCandidates> PlanPriceHistoryAsync(
        IReadOnlyList<Asset> assets,
        IReadOnlyDictionary<int, DateOnly> earliestTradeDateByAsset,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var toFetch = new List<CatchUpAssetTarget>();
        var retryPending = new List<string>();
        var notYetAvailable = new List<string>();
        var marketsInPlay = new HashSet<Market>();

        var priceHistoryRangeByAsset = await db.PriceHistories
            .GroupBy(p => p.AssetId)
            .Select(g => new { AssetId = g.Key, Min = g.Min(p => p.Date), Max = g.Max(p => p.Date) })
            .ToDictionaryAsync(x => x.AssetId, cancellationToken);

        var stateByAsset = await db.AssetPriceHistoryStates.ToDictionaryAsync(s => s.AssetId, cancellationToken);

        // Every Stock asset routes to TwelveData or Yahoo (CoinGecko is Crypto-only — see
        // ProviderMarkets), so ProviderMarkets.For always resolves for an asset reaching this loop.
        Market MarketFor(Asset asset) => ProviderMarkets.For(asset.QuoteProviderKind)!.Value;

        var capByMarket = new Dictionary<Market, DateOnly>();
        DateOnly CapFor(Market market) => capByMarket.TryGetValue(market, out var cap)
            ? cap
            : capByMarket[market] = PriceBackfillCapCalculator.ComputeCap(calendar, market, now, priceBackfillOptions.Value.CloseSettleDelay);

        foreach (var asset in assets)
        {
            var market = MarketFor(asset);
            var cap = CapFor(market);
            var firstTrade = earliestTradeDateByAsset[asset.Id];

            if (firstTrade > cap)
            {
                // No settled close exists yet to fetch — not an attempt, not missing. See
                // PriceBackfillService.RunAsyncCore's identical "AssetsSkippedTodayNotClosed" guard.
                notYetAvailable.Add(asset.Symbol);
                continue;
            }

            var storedCovered = priceHistoryRangeByAsset.TryGetValue(asset.Id, out var range)
                && range.Min <= firstTrade && range.Max >= cap;

            var state = stateByAsset.GetValueOrDefault(asset.Id);
            var stateCovered = state is { CoveredFrom: { } coveredFrom, CoveredTo: { } coveredTo }
                && coveredFrom <= firstTrade && coveredTo >= cap;

            if (storedCovered || stateCovered)
            {
                continue; // covered — either genuinely on file, or already successfully asked for
            }

            if (state is { LastRunSuccess: false, LastAttemptedAt: { } lastAttempted }
                && now - lastAttempted < priceBackfillOptions.Value.FailedRunRetryDelay)
            {
                retryPending.Add(asset.Symbol);
                continue;
            }

            toFetch.Add(new CatchUpAssetTarget(asset.Id, asset.Symbol, market));
            marketsInPlay.Add(market);
        }

        var (currenciesToFetch, fxRetryPending) =
            await PlanFxAsync(assets, toFetch, capByMarket, MarketFor, marketsInPlay, now, cancellationToken);
        retryPending.AddRange(fxRetryPending);

        return new PriceHistoryCatchUpCandidates(
            toFetch, currenciesToFetch, retryPending, notYetAvailable, marketsInPlay.ToList());
    }

    /// <summary>
    /// FX coverage, per non-USD currency an in-scope asset needs. <c>required</c> is
    /// <c>min(the latest settled cap among the markets that need this currency, fxCap)</c> —
    /// <see cref="Calculators.PriceBackfillCapCalculator.ComputeFxCap"/> is load-bearing here, not
    /// merely a floor: <c>RunAsyncCore</c> never requests or accepts a rate beyond it, so comparing
    /// stored/state coverage against a market's own (later) cap alone would read FX as missing for
    /// hours every day, after a market settles but before <c>fxCap</c> catches up — see
    /// <see cref="Domain.Entities.FxPairBackfillState"/>'s remarks for the live-measured defect this
    /// closed (both the SGX-evening window and Twelve Data's incomplete Sunday coverage).
    ///
    /// <para>Covered — and excluded from the result entirely — when EITHER stored
    /// <see cref="Domain.Entities.FxRate"/> spans through <c>required</c>, OR
    /// <see cref="Domain.Entities.FxPairBackfillState.CoveredTo"/> already reaches it (the provider
    /// was successfully asked, even if it had no bar for every date in between — the permanent-gap
    /// principle applied to FX).</para>
    ///
    /// <para>An in-scope asset of this currency already being fetched this run
    /// (<paramref name="toFetch"/>) forces the pair to be fetched regardless of the above — a hard
    /// requirement, not subject to retry pacing, because <c>FxRateResolver</c> throws (500ing every
    /// USD-reporting endpoint) without a rate for a date being converted. Only when NOT forced by
    /// that rule and NOT covered does a recent failure
    /// (<see cref="PriceBackfillOptions.FailedRunRetryDelay"/>) defer the pair to a retry-pending
    /// result instead of fetching it — reported as <c>"USD/{currency}"</c>, merged into the caller's
    /// asset-symbol retry-pending list.</para>
    /// </summary>
    private async Task<(List<string> ToFetch, List<string> RetryPending)> PlanFxAsync(
        IReadOnlyList<Asset> assets,
        IReadOnlyList<CatchUpAssetTarget> toFetch,
        IReadOnlyDictionary<Market, DateOnly> capByMarket,
        Func<Asset, Market> marketFor,
        HashSet<Market> marketsInPlay,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var fxCap = PriceBackfillCapCalculator.ComputeFxCap(now);
        var fetchAssetIds = toFetch.Select(t => t.AssetId).ToHashSet();
        var fxStateByCurrency = await db.FxPairBackfillStates
            .Where(s => s.Base == ReportingCurrency)
            .ToDictionaryAsync(s => s.Quote, cancellationToken);

        var currenciesInScope = assets
            .Where(a => a.Currency != ReportingCurrency)
            .Select(a => a.Currency)
            .Distinct()
            .ToList();

        var currenciesToFetch = new List<string>();
        var retryPending = new List<string>();

        foreach (var currency in currenciesInScope)
        {
            var assetsForCurrency = assets.Where(a => a.Currency == currency).ToList();
            var requiringMarkets = assetsForCurrency.Select(marketFor).ToHashSet();

            var anyAssetOfThisCurrencyBeingFetched = assetsForCurrency.Any(a => fetchAssetIds.Contains(a.Id));
            if (anyAssetOfThisCurrencyBeingFetched)
            {
                // Hard requirement — never deferred by retry pacing. See the method's own remarks.
                currenciesToFetch.Add(currency);
                foreach (var market in requiringMarkets)
                {
                    marketsInPlay.Add(market);
                }

                continue;
            }

            var maxCapForCurrency = requiringMarkets.Max(m => capByMarket[m]);
            var required = maxCapForCurrency < fxCap ? maxCapForCurrency : fxCap;

            var newestStoredFxDate = await db.FxRates
                .Where(f => f.Base == ReportingCurrency && f.Quote == currency)
                .Select(f => (DateOnly?)f.Date)
                .MaxAsync(cancellationToken);
            var storedCovered = newestStoredFxDate is { } stored && stored >= required;

            var state = fxStateByCurrency.GetValueOrDefault(currency);
            var stateCovered = state is { CoveredTo: { } coveredTo } && coveredTo >= required;

            if (storedCovered || stateCovered)
            {
                continue; // covered — not missing
            }

            if (state is { LastRunSuccess: false, LastAttemptedAt: { } lastAttempted }
                && now - lastAttempted < priceBackfillOptions.Value.FailedRunRetryDelay)
            {
                retryPending.Add($"{ReportingCurrency}/{currency}");
                continue;
            }

            currenciesToFetch.Add(currency);
            foreach (var market in requiringMarkets)
            {
                marketsInPlay.Add(market);
            }
        }

        return (currenciesToFetch, retryPending);
    }

    private async Task<DividendsCatchUpCandidates> PlanDividendsAsync(
        IReadOnlyList<Asset> assets,
        IReadOnlyDictionary<int, DateOnly> earliestTradeDateByAsset,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var toFetch = new List<CatchUpAssetTarget>();
        var retryPending = new List<string>();

        var stateByAsset = await db.AssetDividendStates.ToDictionaryAsync(s => s.AssetId, cancellationToken);

        foreach (var asset in assets)
        {
            var firstTrade = earliestTradeDateByAsset[asset.Id];
            var state = stateByAsset.GetValueOrDefault(asset.Id);

            // Missing when never attempted, the last attempt failed, CoveredFrom is null (every
            // pre-migration row), or a back-dated transaction moved firstTrade earlier than what
            // was last successfully asked for.
            var missing = state is null
                || !state.LastRunSuccess
                || state.CoveredFrom is null
                || state.CoveredFrom > firstTrade;

            if (!missing)
            {
                continue;
            }

            if (state is { LastRunSuccess: false, LastAttemptedAt: { } lastAttempted }
                && now - lastAttempted < dividendBackfillOptions.Value.FailedAssetRetryInterval)
            {
                retryPending.Add(asset.Symbol);
                continue;
            }

            var market = ProviderMarkets.For(asset.QuoteProviderKind)!.Value;
            toFetch.Add(new CatchUpAssetTarget(asset.Id, asset.Symbol, market));
        }

        return new DividendsCatchUpCandidates(toFetch, retryPending);
    }
}
