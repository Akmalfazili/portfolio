using Microsoft.AspNetCore.SignalR;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Dtos;

namespace Portfolio.Api.Hubs;

/// <summary>
/// The only place a SignalR type appears outside <c>Portfolio.Api</c>'s wiring — implements the
/// Application-layer <see cref="IPriceUpdateBroadcaster"/> seam so <c>PriceRefreshService</c>
/// never references <see cref="IHubContext{THub}"/> or anything else from
/// <c>Microsoft.AspNetCore.SignalR</c> directly.
/// </summary>
public sealed class SignalRPriceBroadcaster(IHubContext<PricesHub> hubContext) : IPriceUpdateBroadcaster
{
    public Task BroadcastQuoteUpdatedAsync(QuoteUpdateNotification notification, CancellationToken cancellationToken) =>
        hubContext.Clients.All.SendAsync("QuoteUpdated", notification, cancellationToken);

    public Task BroadcastRefreshStatusAsync(PriceRefreshStatus status, CancellationToken cancellationToken) =>
        hubContext.Clients.All.SendAsync("RefreshStatus", status, cancellationToken);
}
