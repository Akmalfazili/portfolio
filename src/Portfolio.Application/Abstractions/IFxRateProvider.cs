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

    /// <summary>Daily rates over <c>[from, to]</c> — <b><paramref name="to"/> is INCLUSIVE</b> —
    /// ascending by date, for historical backfill. Like <see cref="IQuoteProvider"/>'s
    /// equivalent method, this is a contract on the RETURNED points, not a promise about the
    /// upstream query string: see <c>TwelveDataFxProvider</c>'s remarks (D53) for why its own
    /// end-date parameter cannot be trusted to behave this way on the wire. Distinguishes "provider
    /// call failed" (<see cref="FxHistoryFetchResult.Success"/>
    /// false, <see cref="FxHistoryFetchResult.Error"/> set) from "provider succeeded but has no
    /// rates in the range" (<see cref="FxHistoryFetchResult.Success"/> true, empty
    /// <see cref="FxHistoryFetchResult.Points"/>) — a bare empty list for both, as this used to
    /// return, made a 429 indistinguishable from a genuinely empty range and let the FX backfill
    /// report a false success.</summary>
    Task<FxHistoryFetchResult> GetHistoryAsync(
        string baseCurrency, string quoteCurrency, DateOnly from, DateOnly to, CancellationToken cancellationToken);
}
