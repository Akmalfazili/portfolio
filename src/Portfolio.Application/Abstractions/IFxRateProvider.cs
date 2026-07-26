using Portfolio.Application.Dtos;

namespace Portfolio.Application.Abstractions;

/// <summary>
/// Fetches spot and historical FX rates. Only one implementation exists today (Twelve Data,
/// USD/SGD), but the interface stays currency-pair generic rather than hard-coding USD/SGD.
/// </summary>
public interface IFxRateProvider
{
    /// <summary>Current spot rate: units of <paramref name="quoteCurrency"/> per one unit of
    /// <paramref name="baseCurrency"/>. Null if the provider could not resolve the pair.</summary>
    Task<FxSpotResult?> GetSpotRateAsync(
        string baseCurrency, string quoteCurrency, CancellationToken cancellationToken);

    /// <summary>Daily rates over <c>[from, to]</c> inclusive, ascending by date, for historical
    /// backfill. Empty (not throwing) when the provider has no data for the range.</summary>
    Task<IReadOnlyList<FxRatePoint>> GetHistoryAsync(
        string baseCurrency, string quoteCurrency, DateOnly from, DateOnly to, CancellationToken cancellationToken);
}
