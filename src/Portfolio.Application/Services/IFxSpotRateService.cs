using Portfolio.Domain.Entities;

namespace Portfolio.Application.Services;

/// <summary>See <see cref="FxSpotRateService"/>.</summary>
public interface IFxSpotRateService
{
    /// <summary>
    /// Returns a fresh-enough cached spot rate for <paramref name="baseCurrency"/>/
    /// <paramref name="quoteCurrency"/>, calling the live provider only when the cached row is
    /// missing or older than <see cref="FxSpotRateOptions.Ttl"/> (measured from
    /// <see cref="FxSpotQuote.FetchedAt"/>). Never throws — a provider failure (throttle denial,
    /// HTTP error, or an unhandled exception from the provider) falls back to the stored row if one
    /// exists, stale but real and correctly stamped, or returns null if there has never been a
    /// successful fetch. This sits on the zakat report's read path and must never turn a provider
    /// hiccup into a 500.
    /// </summary>
    Task<FxSpotQuote?> GetOrRefreshAsync(string baseCurrency, string quoteCurrency, CancellationToken cancellationToken);
}
