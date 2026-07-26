using System.Text.Json.Serialization;
using Portfolio.Infrastructure.MarketData.Json;

namespace Portfolio.Infrastructure.MarketData.CoinGecko;

/// <summary>
/// One coin's entry in a <c>/simple/price</c> response, e.g.
/// <c>{"usd":0.00042282,"last_updated_at":1785060840}</c>. <see cref="Usd"/> arrives as a bare
/// JSON number with sub-cent magnitude — <see cref="FlexibleDecimalJsonConverter"/> reads it
/// straight into <see cref="decimal"/> via <c>JsonElement.GetDecimal()</c>, never through
/// <see cref="double"/>, so ANVL's ~$0.00042282 survives exactly.
/// </summary>
public sealed class CoinGeckoSimplePriceEntry
{
    [JsonPropertyName("usd")]
    [JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public decimal Usd { get; set; }

    [JsonPropertyName("last_updated_at")]
    public long? LastUpdatedAt { get; set; }
}

// The /coins/{id}/market_chart/range response (each "prices" entry a [unixMillis, price] pair,
// e.g. [1784631600000, 0.0004059826574851183] — verified live) is parsed directly off
// JsonDocument in CoinGeckoQuoteProvider rather than through a typed List<List<double>> model —
// a double-typed array element would round-trip the price through double before we ever see it,
// which is exactly the precision loss this codebase exists to avoid.

/// <summary>
/// The error body a keyless <c>/market_chart/range</c> request returns when the requested range
/// is older than CoinGecko's 365-day public-API window. Verified live:
/// <c>{"error":{"status":{"error_code":10012,"error_message":"Your request exceeds the allowed
/// time range. Public API users are limited to querying historical data within the past 365
/// days. Upgrade to a paid plan…"}}}</c>, returned as HTTP 401. <see cref="ErrorCode"/> 10012
/// specifically means "range too wide", not an auth failure — <c>CoinGeckoQuoteProvider</c>
/// clamps the request before sending it, so this is a defensive fallback for whenever the clamp
/// and the provider's actual limit disagree, not the primary mitigation.
/// </summary>
public sealed class CoinGeckoErrorResponse
{
    [JsonPropertyName("error")]
    public CoinGeckoErrorDetail? Error { get; set; }
}

public sealed class CoinGeckoErrorDetail
{
    [JsonPropertyName("status")]
    public CoinGeckoErrorStatus? Status { get; set; }
}

public sealed class CoinGeckoErrorStatus
{
    [JsonPropertyName("error_code")]
    public int? ErrorCode { get; set; }

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; set; }
}
