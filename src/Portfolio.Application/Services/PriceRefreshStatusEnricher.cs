using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Dtos;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services;

/// <summary>
/// Fills in <see cref="PriceRefreshStatus"/>'s three derived Twelve Data fields —
/// <see cref="PriceRefreshStatus.EffectiveTwelveDataIntervalSeconds"/>,
/// <see cref="PriceRefreshStatus.CreditsUsedToday"/>, <see cref="PriceRefreshStatus.CreditBudget"/> —
/// on top of the bare snapshot <see cref="PriceRefreshStatusStore.GetSnapshotAsync"/> returns.
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
/// (<see cref="ITwelveDataCreditThrottle.GetStatusAsync"/> — one row lookup keyed on today's date)
/// and counts active Twelve Data assets (a handful of rows in this single-user portfolio) — both
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

        return status with
        {
            EffectiveTwelveDataIntervalSeconds = (int)effectiveInterval.TotalSeconds,
            CreditsUsedToday = creditStatus.CreditsUsedToday,
            CreditBudget = creditStatus.DailyBudget,
        };
    }
}
