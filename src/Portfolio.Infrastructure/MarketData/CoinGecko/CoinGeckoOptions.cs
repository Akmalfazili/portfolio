namespace Portfolio.Infrastructure.MarketData.CoinGecko;

/// <summary>
/// CoinGecko needs no key at all on the keyless public API (verified live:
/// <c>/simple/price</c> for ethereum/anvil/amp-token works with no auth header, ~10-30 req/min
/// IP-based, comfortably inside our one-call-per-2-minutes cadence). <see cref="ApiKey"/> is
/// therefore optional — if set later via <c>dotnet user-secrets</c> / <c>CoinGecko__ApiKey</c>,
/// the client switches to the pro-api base URL and sends it as <c>x-cg-pro-api-key</c>; the
/// keyless base URL must never receive that header (CoinGecko ignores it there, but sending an
/// unused credential is still worth avoiding).
/// </summary>
public sealed class CoinGeckoOptions
{
    public const string SectionName = "CoinGecko";

    public string? ApiKey { get; set; }

    public string KeylessBaseUrl { get; set; } = "https://api.coingecko.com/api/v3";

    public string ProBaseUrl { get; set; } = "https://pro-api.coingecko.com/api/v3";

    /// <summary>
    /// Verified live: a keyless <c>/market_chart/range</c> request older than 365 days is
    /// rejected outright with HTTP 401 and <c>error_code 10012</c> ("Public API users are
    /// limited to querying historical data within the past 365 days"). A configured
    /// <see cref="ApiKey"/> lifts this limit, so <c>CoinGeckoQuoteProvider</c> only clamps to
    /// this window when <see cref="ApiKey"/> is null — never hard-code 365 in the provider body.
    /// </summary>
    public int KeylessMaxHistoryDays { get; set; } = 365;
}
