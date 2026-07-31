using Microsoft.AspNetCore.SignalR;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Services;

namespace Portfolio.Api.Hubs;

/// <summary>
/// Push-only hub — clients never call a method on it, they just listen for
/// <c>QuoteUpdated</c> and <c>RefreshStatus</c> events broadcast by <see cref="SignalRPriceBroadcaster"/>.
/// On connect, a client is sent the current status snapshot immediately rather than waiting for
/// the next refresh cycle, so a freshly opened browser tab does not show a blank "last refreshed"
/// indicator until the next scheduled tick.
/// </summary>
public sealed class PricesHub(
    PriceRefreshStatusStore statusStore,
    IMarketCalendar calendar,
    TimeProvider timeProvider) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var now = timeProvider.GetUtcNow();
        var status = statusStore.GetSnapshot(
            calendar.IsOpen(Market.Nyse, now),
            calendar.IsOpen(Market.Sgx, now));

        await Clients.Caller.SendAsync("RefreshStatus", status);
        await base.OnConnectedAsync();
    }
}
