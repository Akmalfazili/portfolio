namespace Portfolio.Application.Services;

/// <summary>
/// Cache TTL for <see cref="IFxSpotRateService"/>. Overridable so tests never wait on a real
/// clock — the injected <c>TimeProvider</c> drives every freshness check, never
/// <c>DateTimeOffset.UtcNow</c>.
/// </summary>
public sealed class FxSpotRateOptions
{
    public const string SectionName = "MarketData:FxSpot";

    /// <summary>How long a cached spot rate is served without calling the provider again. Measured
    /// from <see cref="Domain.Entities.FxSpotQuote.FetchedAt"/>, never
    /// <see cref="Domain.Entities.FxSpotQuote.AsOf"/> — see that entity's remarks for why a
    /// provider-timestamp TTL would never expire over a weekend and would re-spend a Twelve Data
    /// credit on every single page load. 15 minutes is generous against the zakat report's actual
    /// call volume (a handful of page loads a day, not a poll).</summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(15);
}
