using Portfolio.Application.Dtos;

namespace Portfolio.Application.Services;

/// <summary>
/// Refreshes live <see cref="Domain.Entities.PriceQuote"/> rows from each configured market-data
/// provider, batched per provider and gated by <see cref="Abstractions.IMarketCalendar"/> for
/// equities. Never writes <see cref="Domain.Entities.PriceHistory"/> — that stays the sole
/// responsibility of <see cref="IPriceBackfillService"/>, and crypto gets no history at all, by
/// the Phase 4 decision recorded in tracker.md.
/// </summary>
public interface IPriceRefreshService
{
    /// <summary>Refreshes every source whose interval has elapsed (or has never run), skipping
    /// the rest. Called by <see cref="PriceRefreshBackgroundService"/> on its poll tick — never
    /// throws for an ordinary provider failure, and never blocked by the manual-refresh cooldown.</summary>
    Task<PriceRefreshCycleResult> RefreshDueAsync(CancellationToken cancellationToken);

    /// <summary>Forces a refresh of every source right now, subject to two things that are never
    /// bypassed: the manual-refresh cooldown, and — for equities — the market calendar. A closed
    /// equity market is still skipped even on a manual trigger, so a "refresh now" click at 3am
    /// cannot spend a Twelve Data credit for data that has not changed since the last close.
    /// Crypto, having no calendar gate, always refreshes on a manual trigger.</summary>
    Task<PriceRefreshCycleResult> RefreshNowAsync(CancellationToken cancellationToken);
}
