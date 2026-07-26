using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Dtos;

namespace Portfolio.Infrastructure.MarketData.TwelveData;

/// <summary>
/// USD/SGD spot and historical rates from Twelve Data. <c>/exchange_rate</c> returns <c>rate</c>
/// as a bare JSON number (unlike <c>/quote</c>'s JSON-string numerics) — both still decode
/// through <see cref="Json.FlexibleDecimalJsonConverter"/> so a future provider quirk in either
/// direction is tolerated without another code change.
/// </summary>
public sealed class TwelveDataFxProvider(
    HttpClient httpClient,
    IOptions<TwelveDataOptions> options,
    TimeProvider timeProvider,
    ILogger<TwelveDataFxProvider> logger) : IFxRateProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<FxSpotResult?> GetSpotRateAsync(
        string baseCurrency, string quoteCurrency, CancellationToken cancellationToken)
    {
        var pair = $"{baseCurrency}/{quoteCurrency}";
        var requestUri = $"exchange_rate?symbol={Uri.EscapeDataString(pair)}&apikey={options.Value.ApiKey}";

        using var response = await httpClient.GetAsync(requestUri, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Twelve Data /exchange_rate for {Pair} failed with status {StatusCode}",
                pair,
                (int)response.StatusCode);
            return null;
        }

        var payload = JsonSerializer.Deserialize<TwelveDataExchangeRate>(json, JsonOptions);

        if (payload is null || payload.IsError)
        {
            logger.LogWarning(
                "Twelve Data /exchange_rate for {Pair} returned an error: {Message}",
                pair,
                payload?.Message);
            return null;
        }

        var asOf = payload.Timestamp is { } ts
            ? DateTimeOffset.FromUnixTimeSeconds(ts)
            : timeProvider.GetUtcNow();

        return new FxSpotResult(payload.Rate, asOf);
    }

    public async Task<IReadOnlyList<FxRatePoint>> GetHistoryAsync(
        string baseCurrency, string quoteCurrency, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var pair = $"{baseCurrency}/{quoteCurrency}";
        var requestUri =
            $"time_series?symbol={Uri.EscapeDataString(pair)}&interval=1day" +
            $"&start_date={from:yyyy-MM-dd}&end_date={to:yyyy-MM-dd}&apikey={options.Value.ApiKey}";

        using var response = await httpClient.GetAsync(requestUri, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Twelve Data /time_series (FX) for {Pair} failed with status {StatusCode}",
                pair,
                (int)response.StatusCode);
            return [];
        }

        var payload = JsonSerializer.Deserialize<TwelveDataTimeSeriesResponse>(json, JsonOptions);

        if (payload is null || payload.IsError)
        {
            logger.LogWarning(
                "Twelve Data /time_series (FX) for {Pair} returned an error: {Message}",
                pair,
                payload?.Message);
            return [];
        }

        return (payload.Values ?? [])
            .Where(v => v.Datetime is not null)
            .Select(v => new FxRatePoint(
                DateOnly.ParseExact(v.Datetime!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                v.Close))
            .OrderBy(p => p.Date)
            .ToList();
    }
}
