using Portfolio.Application.Dtos;

namespace Portfolio.Application.Abstractions;

/// <summary>
/// Publishes live refresh events to connected browser clients. Implemented with SignalR in
/// <c>Portfolio.Api</c> — this seam is what keeps every SignalR type out of
/// <c>Portfolio.Application</c> entirely, matching the same inward-only dependency rule that
/// keeps HTTP-client types out of it via <see cref="IQuoteProvider"/>.
/// </summary>
public interface IPriceUpdateBroadcaster
{
    /// <summary>One asset's quote changed. Fired per asset, as results come back from a
    /// provider batch, not once per whole cycle.</summary>
    Task BroadcastQuoteUpdatedAsync(QuoteUpdateNotification notification, CancellationToken cancellationToken);

    /// <summary>The refresh status snapshot changed — fired once per completed cycle.</summary>
    Task BroadcastRefreshStatusAsync(PriceRefreshStatus status, CancellationToken cancellationToken);
}
