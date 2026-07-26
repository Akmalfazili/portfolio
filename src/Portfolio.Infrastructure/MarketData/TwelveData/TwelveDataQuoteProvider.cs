using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Dtos;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;

namespace Portfolio.Infrastructure.MarketData.TwelveData;

/// <summary>
/// US-equity quotes from Twelve Data. Batches every asset into one <c>/quote</c> call — each
/// symbol in the batch costs one credit against the 800/day free-tier budget, so this must never
/// be called once per symbol. Twelve Data flattens a single-symbol response (no wrapper object)
/// but nests each symbol's object under its own key for two-or-more, so both shapes are handled.
/// A per-symbol <c>status: "error"</c> object never fails the sibling symbols in the same batch.
/// </summary>
public sealed class TwelveDataQuoteProvider(
    HttpClient httpClient,
    IOptions<TwelveDataOptions> options,
    TimeProvider timeProvider,
    ILogger<TwelveDataQuoteProvider> logger) : IQuoteProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public QuoteProviderKind Kind => QuoteProviderKind.TwelveData;

    public async Task<IReadOnlyList<QuoteFetchResult>> GetQuotesAsync(
        IReadOnlyCollection<Asset> assets, CancellationToken cancellationToken)
    {
        if (assets.Count == 0)
        {
            return [];
        }

        var bySymbol = new Dictionary<string, Asset>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets)
        {
            if (string.IsNullOrWhiteSpace(asset.ProviderSymbol))
            {
                throw new InvalidOperationException(
                    $"Asset {asset.Id} ({asset.Symbol}) is routed to Twelve Data but has no ProviderSymbol.");
            }

            bySymbol[asset.ProviderSymbol] = asset;
        }

        var symbolList = string.Join(',', bySymbol.Keys);
        var requestUri = $"quote?symbol={Uri.EscapeDataString(symbolList)}&apikey={options.Value.ApiKey}";

        string json;
        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);
            json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Twelve Data /quote batch of {SymbolCount} symbols failed with status {StatusCode}",
                    bySymbol.Count,
                    (int)response.StatusCode);
                return bySymbol.Values
                    .Select(a => new QuoteFetchResult(a.Id, false, null, null, null, $"Twelve Data returned HTTP {(int)response.StatusCode}."))
                    .ToList();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Twelve Data /quote batch of {SymbolCount} symbols threw", bySymbol.Count);
            return bySymbol.Values
                .Select(a => new QuoteFetchResult(a.Id, false, null, null, null, "Twelve Data request failed."))
                .ToList();
        }

        var results = new List<QuoteFetchResult>(bySymbol.Count);

        if (bySymbol.Count == 1)
        {
            var (_, asset) = bySymbol.First();
            var quote = JsonSerializer.Deserialize<TwelveDataQuote>(json, JsonOptions);
            results.Add(ToResult(asset, quote));
            return results;
        }

        using var doc = JsonDocument.Parse(json);
        foreach (var (symbol, asset) in bySymbol)
        {
            if (!doc.RootElement.TryGetProperty(symbol, out var element))
            {
                results.Add(new QuoteFetchResult(asset.Id, false, null, null, null, $"No quote returned for symbol '{symbol}'."));
                continue;
            }

            var quote = element.Deserialize<TwelveDataQuote>(JsonOptions);
            results.Add(ToResult(asset, quote));
        }

        return results;
    }

    public async Task<HistoryFetchResult> GetHistoryAsync(
        Asset asset, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(asset.ProviderSymbol))
        {
            throw new InvalidOperationException(
                $"Asset {asset.Id} ({asset.Symbol}) is routed to Twelve Data but has no ProviderSymbol.");
        }

        var requestUri =
            $"time_series?symbol={Uri.EscapeDataString(asset.ProviderSymbol)}&interval=1day" +
            $"&start_date={from:yyyy-MM-dd}&end_date={to:yyyy-MM-dd}&apikey={options.Value.ApiKey}";

        using var response = await httpClient.GetAsync(requestUri, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Twelve Data /time_series for {Symbol} failed with status {StatusCode}",
                asset.ProviderSymbol,
                (int)response.StatusCode);
            return HistoryFetchResult.Failed(from, $"Twelve Data returned HTTP {(int)response.StatusCode}.");
        }

        var payload = JsonSerializer.Deserialize<TwelveDataTimeSeriesResponse>(json, JsonOptions);

        if (payload is null || payload.IsError)
        {
            logger.LogWarning(
                "Twelve Data /time_series for {Symbol} returned an error: {Message}",
                asset.ProviderSymbol,
                payload?.Message);
            return HistoryFetchResult.Failed(from, payload?.Message ?? "Twelve Data returned an error.");
        }

        var currency = payload.Meta?.Currency ?? asset.Currency;

        // Verified live against a real key: unlike CoinGecko's keyless tier, Twelve Data's free
        // /time_series does not truncate multi-year ranges (2019 data returned for AAPL and
        // USD/SGD alike on a start_date years back), so there is nothing to clamp here.
        var points = (payload.Values ?? [])
            .Where(v => v.Datetime is not null)
            .Select(v => new PriceHistoryPoint(
                DateOnly.ParseExact(v.Datetime!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                v.Close,
                currency))
            .OrderBy(p => p.Date)
            .ToList();

        return HistoryFetchResult.Ok(points, from);
    }

    private QuoteFetchResult ToResult(Asset asset, TwelveDataQuote? quote)
    {
        if (quote is null)
        {
            return new QuoteFetchResult(asset.Id, false, null, null, null, "Empty response from Twelve Data.");
        }

        if (quote.IsError)
        {
            return new QuoteFetchResult(asset.Id, false, null, null, null, quote.Message ?? $"Twelve Data error {quote.Code}.");
        }

        var asOf = quote.Timestamp is { } ts
            ? DateTimeOffset.FromUnixTimeSeconds(ts)
            : timeProvider.GetUtcNow();

        return new QuoteFetchResult(asset.Id, true, quote.Close, quote.Currency ?? asset.Currency, asOf, null);
    }
}
