using Portfolio.Application.Abstractions;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services.Calendar;

/// <summary>
/// Which exchange gates each quote provider, or <c>null</c> for a provider with no market at all.
///
/// <para>This mapping had lived privately inside <c>PriceRefreshService</c>, where it answered
/// only "may I spend a credit right now?". <c>PortfolioSummaryService</c> now needs the same
/// mapping to answer a different question — "is this stored quote from the current session?"
/// (D4) — and two copies of a provider-to-market table is exactly the kind of drift D7 was raised
/// about, so there is one table.</para>
/// </summary>
public static class ProviderMarkets
{
    private static readonly IReadOnlyDictionary<QuoteProviderKind, Market?> ByProvider =
        new Dictionary<QuoteProviderKind, Market?>
        {
            [QuoteProviderKind.TwelveData] = Market.Nyse,
            [QuoteProviderKind.Yahoo] = Market.Sgx,

            // Crypto trades 24/7. Null is meaningful here, not "unknown": CoinGecko is never
            // gated by a calendar and never has a session boundary to be stale relative to.
            [QuoteProviderKind.CoinGecko] = null,
        };

    /// <summary>The market gating <paramref name="kind"/>, or null if it has none (crypto).</summary>
    public static Market? For(QuoteProviderKind kind) => ByProvider.GetValueOrDefault(kind);
}
