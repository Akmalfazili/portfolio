namespace Portfolio.Application.Services;

/// <summary>
/// Cadence for <see cref="PriceRefreshBackgroundService"/> and the manual-refresh cooldown, per
/// CLAUDE.md: 5 minutes while the relevant exchange is open, 60 minutes while it is closed, and
/// 2 minutes for crypto (always — it has no market to be "closed"). <see cref="PollInterval"/> is
/// how often the background loop wakes up to check whether anything is due; it is deliberately
/// much shorter than any of the refresh intervals so a due source is never kept waiting long past
/// its scheduled time, not the interval itself.
/// </summary>
public sealed class PriceRefreshOptions
{
    public const string SectionName = "MarketData:Refresh";

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan StockOpenInterval { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan StockClosedInterval { get; set; } = TimeSpan.FromMinutes(60);

    public TimeSpan CryptoInterval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Minimum time between two manually triggered refreshes.</summary>
    public TimeSpan ManualCooldown { get; set; } = TimeSpan.FromSeconds(30);
}
