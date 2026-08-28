using System.Text.Json.Serialization;

namespace Portfolio.Infrastructure.MarketData.Yahoo;

/// <summary>
/// <c>/v8/finance/chart/{symbol}</c> response shapes. Unlike Twelve Data, Yahoo encodes every
/// numeric field as a plain JSON number (verified live: <c>"regularMarketPrice":4.39</c>), so the
/// built-in <see cref="decimal"/>/<see cref="decimal"/>? converters — which read a JSON number
/// token straight into <see cref="decimal"/> with no <see cref="double"/> hop — are sufficient
/// without a custom converter here.
/// </summary>
public sealed class YahooChartResponse
{
    [JsonPropertyName("chart")]
    public YahooChart? Chart { get; set; }
}

public sealed class YahooChart
{
    [JsonPropertyName("result")]
    public List<YahooChartResult>? Result { get; set; }

    [JsonPropertyName("error")]
    public YahooChartError? Error { get; set; }
}

public sealed class YahooChartError
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public sealed class YahooChartResult
{
    [JsonPropertyName("meta")]
    public YahooMeta? Meta { get; set; }

    [JsonPropertyName("timestamp")]
    public List<long>? Timestamp { get; set; }

    [JsonPropertyName("indicators")]
    public YahooIndicators? Indicators { get; set; }

    /// <summary>Only present when the request carries <c>events=div</c>.</summary>
    [JsonPropertyName("events")]
    public YahooChartEvents? Events { get; set; }
}

/// <summary>Dividend/split events, keyed by epoch-seconds string — only <c>dividends</c> is used
/// here; splits are out of scope for this project.</summary>
public sealed class YahooChartEvents
{
    [JsonPropertyName("dividends")]
    public Dictionary<string, YahooDividendEvent>? Dividends { get; set; }
}

public sealed class YahooDividendEvent
{
    /// <summary>Per-share amount, in the response's own <see cref="YahooMeta.Currency"/>.</summary>
    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    /// <summary>Ex-dividend date, epoch seconds.</summary>
    [JsonPropertyName("date")]
    public long Date { get; set; }
}

public sealed class YahooMeta
{
    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("exchangeName")]
    public string? ExchangeName { get; set; }

    [JsonPropertyName("regularMarketPrice")]
    public decimal RegularMarketPrice { get; set; }

    [JsonPropertyName("regularMarketTime")]
    public long? RegularMarketTime { get; set; }
}

public sealed class YahooIndicators
{
    [JsonPropertyName("quote")]
    public List<YahooQuoteIndicator>? Quote { get; set; }
}

public sealed class YahooQuoteIndicator
{
    /// <summary>Null entries mark non-trading days inside the range and must be skipped, not
    /// treated as a zero close.</summary>
    [JsonPropertyName("close")]
    public List<decimal?>? Close { get; set; }
}
