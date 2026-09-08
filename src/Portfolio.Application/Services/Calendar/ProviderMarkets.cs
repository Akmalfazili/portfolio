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

    /// <summary>Every market this app polls a calendar for. Used wherever "cover every market" is
    /// the correct scope — the manual backfill endpoint, and <c>PriceBackfillService.RunIfDueAsync</c>'s
    /// per-market due-ness scan (D47) — so both stay in sync with <see cref="Market"/>'s own enum
    /// members rather than each hand-rolling the list.</summary>
    public static readonly IReadOnlyList<Market> All = [Market.Nyse, Market.Sgx];
}
