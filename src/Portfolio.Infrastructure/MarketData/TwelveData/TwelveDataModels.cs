using System.Text.Json.Serialization;
using Portfolio.Infrastructure.MarketData.Json;

namespace Portfolio.Infrastructure.MarketData.TwelveData;

/// <summary>
/// One entry of a <c>/quote</c> response. Twelve Data encodes every numeric field as a JSON
/// *string* on this endpoint (verified live: <c>"close":"333.019989"</c>) — <see cref="Close"/>
/// and <see cref="Timestamp"/> use <see cref="FlexibleDecimalJsonConverter"/> so a string or a
/// bare number both parse straight to <see cref="decimal"/>, never through <see cref="double"/>.
/// </summary>
public sealed class TwelveDataQuote
{
    [JsonPropertyName("symbol")]
    public string? Symbol { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("close")]
    [JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public decimal Close { get; set; }

    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; set; }

    /// <summary>Present and equal to "error" on a per-symbol failure nested inside an otherwise
    /// successful batch response. Sibling symbols in the same call can still succeed.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("code")]
    public int? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    public bool IsError => string.Equals(Status, "error", StringComparison.OrdinalIgnoreCase);
}

/// <summary><c>/exchange_rate</c> response. Unlike <c>/quote</c>, <see cref="Rate"/> arrives as a
/// bare JSON number (verified live: <c>"rate":1.29073</c>) — Twelve Data is inconsistent between
/// its own endpoints, so this still needs <see cref="FlexibleDecimalJsonConverter"/> to tolerate
/// either shape defensively.</summary>
public sealed class TwelveDataExchangeRate
{
    [JsonPropertyName("symbol")]
    public string? Symbol { get; set; }

    [JsonPropertyName("rate")]
    [JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public decimal Rate { get; set; }

    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("code")]
    public int? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    public bool IsError => string.Equals(Status, "error", StringComparison.OrdinalIgnoreCase);
}

/// <summary><c>/api_usage</c> response — Twelve Data's own authoritative credit counter, snake_case
/// unlike most of its other endpoints (verified live: <c>"daily_usage":323</c>). Used only for D39
/// reconciliation (<see cref="TwelveDataUsageProvider"/>), never for gating a data call.</summary>
public sealed class TwelveDataUsageResponse
{
    [JsonPropertyName("current_usage")]
    public int CurrentUsage { get; set; }

    [JsonPropertyName("plan_limit")]
    public int PlanLimit { get; set; }

    [JsonPropertyName("daily_usage")]
    public int DailyUsage { get; set; }

    [JsonPropertyName("plan_daily_limit")]
    public int PlanDailyLimit { get; set; }
}

/// <summary>One row of a <c>/time_series</c> response.</summary>
public sealed class TwelveDataTimeSeriesValue
{
    [JsonPropertyName("datetime")]
    public string? Datetime { get; set; }

    [JsonPropertyName("close")]
    [JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public decimal Close { get; set; }
}

public sealed class TwelveDataTimeSeriesMeta
{
    [JsonPropertyName("symbol")]
    public string? Symbol { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }
}

/// <summary><c>/time_series</c> response for a single symbol. On failure Twelve Data returns the
/// same flat error shape as the other endpoints (<c>status: "error"</c>) instead of a
/// <c>values</c> array.</summary>
public sealed class TwelveDataTimeSeriesResponse
{
    [JsonPropertyName("meta")]
    public TwelveDataTimeSeriesMeta? Meta { get; set; }

    [JsonPropertyName("values")]
    public List<TwelveDataTimeSeriesValue>? Values { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("code")]
    public int? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    public bool IsError => string.Equals(Status, "error", StringComparison.OrdinalIgnoreCase);
}
