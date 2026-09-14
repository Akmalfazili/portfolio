using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services.Calendar;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services;

/// <summary>
/// Fills in <see cref="PriceRefreshStatus"/>'s derived fields — the three Twelve Data ones
/// (<see cref="PriceRefreshStatus.EffectiveTwelveDataIntervalSeconds"/>,
/// <see cref="PriceRefreshStatus.CreditsUsedToday"/>, <see cref="PriceRefreshStatus.CreditBudget"/>)
/// and <see cref="PriceRefreshStatus.Closes"/> (per-market daily-close backfill status) — on top of
/// the bare snapshot <see cref="PriceRefreshStatusStore.GetSnapshotAsync"/> returns.
///
/// <para>This exists so the three places that ever hand a <see cref="PriceRefreshStatus"/> to a
/// client enrich it identically: <c>GET /api/prices/status</c>
/// (<c>Portfolio.Api.Endpoints.PricesEndpoints</c>), a newly connected SignalR client
/// (<c>Portfolio.Api.Hubs.PricesHub.OnConnectedAsync</c>), and every SignalR broadcast
/// (<see cref="PriceRefreshService"/>, via <see cref="IPriceUpdateBroadcaster"/>). Before this type
/// existed the enrichment was written once, inline, inside the status endpoint's handler; the other
/// two producers sent the bare store snapshot straight over the wire, so these three fields were
/// always null on both SignalR paths and only ever populated over HTTP — a duplicated-code-path bug,
/// not a serializer one, but the same shape as the documented SignalR/HTTP divergences elsewhere in
/// this codebase. See the tracker entry this fixed.</para>
///
/// <para><b>Cost:</b> reads only the persisted Twelve Data credit ledger
/// (<see cref="ITwelveDataCreditThrottle.GetStatusAsync"/> — one row lookup keyed on today's date),
/// counts active Twelve Data assets (a handful of rows in this single-user portfolio), and — for
/// <see cref="PriceRefreshStatus.Closes"/> — two further DB-only queries (one grouped read of
/// per-asset max <c>PriceHistory</c> dates, one read of the relevant <c>RefreshRun</c> rows) — all
/// cheap local SQL Server reads. It never calls <see cref="ITwelveDataCreditThrottle.TryAcquireAsync"/>
/// and never reaches <c>ITwelveDataUsageProvider</c>/Twelve Data's own <c>GET /api_usage</c>, so
/// enriching — however often it runs, including once per crypto broadcast every 2 minutes — can
/// never spend a Twelve Data credit or a per-minute request slot. That is the same guarantee
/// <c>GET /api/prices/status</c> already made before this type existed, and it must keep holding on
/// every path that calls this.</para>
/// </summary>
public sealed class PriceRefreshStatusEnricher(
    IPortfolioDbContext db,
    ITwelveDataCreditThrottle creditThrottle,
    IOptions<PriceRefreshOptions> refreshOptions)
{
    /// <summary>The <see cref="RefreshTrigger"/> values that record a daily-close backfill run —
    /// never the live-quote (<see cref="RefreshTrigger.Scheduled"/>/<see cref="RefreshTrigger.Manual"/>)
    /// triggers <see cref="SourceRefreshStatus"/> already covers. Includes
    /// <see cref="RefreshTrigger.BackfillCatchUp"/> alongside the two pre-existing triggers — the
    /// refresh-catch-up feature's price-history leg is still a real backfill attempt against a real
    /// market, and a catch-up that fails must surface in this panel exactly like a failed scheduled
    /// or manual run would, not vanish into an audit trail nothing reads.</summary>
    private static readonly RefreshTrigger[] BackfillTriggers =
        [RefreshTrigger.BackfillScheduled, RefreshTrigger.BackfillManual, RefreshTrigger.BackfillCatchUp];

    public async Task<PriceRefreshStatus> EnrichAsync(PriceRefreshStatus status, CancellationToken cancellationToken)
    {
        var creditStatus = await creditThrottle.GetStatusAsync(cancellationToken);
        var activeTwelveDataCount = await db.Assets.CountAsync(
            a => a.IsActive && a.QuoteProviderKind == QuoteProviderKind.TwelveData, cancellationToken);
        var effectiveInterval = TwelveDataCadenceCalculator.DeriveStockOpenInterval(
            activeTwelveDataCount,
            creditStatus.RemainingToday,
            TimeSpan.FromMinutes(TwelveDataCreditPolicy.NyseSessionMinutes),
            refreshOptions.Value.StockOpenInterval);

        var closes = await BuildMarketCloseStatusesAsync(cancellationToken);

        return status with
        {
            EffectiveTwelveDataIntervalSeconds = (int)effectiveInterval.TotalSeconds,
            CreditsUsedToday = creditStatus.CreditsUsedToday,
            CreditBudget = creditStatus.DailyBudget,
            Closes = closes,
        };
    }

    private async Task<IReadOnlyList<MarketCloseStatus>> BuildMarketCloseStatusesAsync(
        CancellationToken cancellationToken)
    {
        // One grouped query: every active Stock asset with at least one transaction, alongside the
        // newest PriceHistory date it has on file (null if it has none at all). AssetClass.Crypto is
        // excluded by construction — crypto keeps no price history and has no Market.
        var assetRows = await db.Assets
            .Where(a => a.AssetClass == AssetClass.Stock && a.IsActive)
            .Where(a => db.Transactions.Any(t => t.AssetId == a.Id))
            .Select(a => new
            {
                a.QuoteProviderKind,
                LatestClose = db.PriceHistories
                    .Where(p => p.AssetId == a.Id)
                    .Max(p => (DateOnly?)p.Date),
            })
            .ToListAsync(cancellationToken);

        // One query for every completed backfill run, newest first, so each market's most recent
        // attempt is simply the first entry in its group — and its most recent success is the first
        // entry in that same group with Success == true. Market == null rows (pre-D47) are excluded
        // by the WHERE, not filtered after the fact, so they can never be mistaken for "attempted".
        var runsByMarket = (await db.RefreshRuns
                .Where(r => r.Market != null
                    && r.CompletedAt != null
                    && BackfillTriggers.Contains(r.Trigger))
                .OrderByDescending(r => r.CompletedAt)
                .Select(r => new { Market = r.Market!.Value, CompletedAt = r.CompletedAt!.Value, r.Success, r.ErrorMessage })
                .ToListAsync(cancellationToken))
            .GroupBy(r => r.Market)
            .ToDictionary(g => g.Key, g => g.ToList());

        var closes = new List<MarketCloseStatus>(ProviderMarkets.All.Count);
        foreach (var market in ProviderMarkets.All)
        {
            var assetsForMarket = assetRows.Where(a => ProviderMarkets.For(a.QuoteProviderKind) == market).ToList();

            // Min-of-max, and null wherever no honest floor exists yet: either this market has no
            // qualifying asset at all, or at least one of them has never received a single
            // PriceHistory row. Using the max instead would claim "closes are current through
            // Friday" when only one asset actually got Friday's close.
            DateOnly? latestCloseDate = assetsForMarket.Count > 0 && assetsForMarket.All(a => a.LatestClose is not null)
                ? assetsForMarket.Min(a => a.LatestClose!.Value)
                : null;

            runsByMarket.TryGetValue(market, out var runs);
            var lastRun = runs?.FirstOrDefault();
            var lastSuccessfulRun = runs?.FirstOrDefault(r => r.Success);

            closes.Add(new MarketCloseStatus(
                Market: market,
                LatestCloseDate: latestCloseDate,
                LastAttemptedAt: lastRun?.CompletedAt,
                LastSuccessAt: lastSuccessfulRun?.CompletedAt,
                LastRunSuccess: lastRun?.Success,
                LastError: lastRun?.ErrorMessage));
        }

        return closes;
    }
}
