using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;

namespace Portfolio.Infrastructure.MarketData.TwelveData;

/// <summary>
/// US-equity quotes from Twelve Data. Each symbol in a <c>/quote</c> request costs one credit
/// against the 800/day free-tier budget, and — this is D38 — Twelve Data's per-minute limit is
/// itself credit-denominated, not request-denominated: a single request carrying more than
/// <see cref="TwelveDataCreditPolicy.PerMinuteCreditLimit"/> symbols 429s immediately even though
/// it is the only request in its minute. <see cref="GetQuotesAsync"/> therefore chunks the batch
/// into groups no larger than that limit and pushes every chunk through the shared
/// <see cref="ITwelveDataCreditThrottle"/>, which paces successive chunks to respect the rolling
/// 60-second window — this can take tens of seconds per extra chunk, and the caller must never be
/// something that has to respond quickly (see the remarks on
/// <c>Portfolio.Application.Services.PriceRefreshService.RefreshNowAsync</c>).
///
/// Twelve Data flattens a single-symbol response (no wrapper object) but nests each symbol's
/// object under its own key for two-or-more, so both shapes are handled per chunk. A per-symbol
/// <c>status: "error"</c> object never fails the sibling symbols in the same chunk.
/// </summary>
public sealed class TwelveDataQuoteProvider(
    HttpClient httpClient,
    IOptions<TwelveDataOptions> options,
    ITwelveDataCreditThrottle creditThrottle,
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

        var results = new List<QuoteFetchResult>(bySymbol.Count);

        foreach (var chunk in bySymbol.Chunk(TwelveDataCreditPolicy.PerMinuteCreditLimit))
        {
            var granted = await creditThrottle.TryAcquireAsync(chunk.Length, cancellationToken);
            if (!granted)
            {
                logger.LogWarning(
                    "Twelve Data /quote chunk of {SymbolCount} symbols skipped: daily credit budget exhausted",
                    chunk.Length);
                results.AddRange(chunk.Select(kv =>
                    new QuoteFetchResult(kv.Value.Id, false, null, null, null, "Twelve Data daily credit budget exhausted.")));
                continue;
            }

            results.AddRange(await FetchChunkAsync(chunk, cancellationToken));
        }

        return results;
    }

    private async Task<IReadOnlyList<QuoteFetchResult>> FetchChunkAsync(
        KeyValuePair<string, Asset>[] chunk, CancellationToken cancellationToken)
    {
        var symbolList = string.Join(',', chunk.Select(kv => kv.Key));
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
                    chunk.Length,
                    (int)response.StatusCode);
                return chunk
                    .Select(kv => new QuoteFetchResult(kv.Value.Id, false, null, null, null, $"Twelve Data returned HTTP {(int)response.StatusCode}."))
                    .ToList();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Twelve Data /quote batch of {SymbolCount} symbols threw", chunk.Length);
            return chunk
                .Select(kv => new QuoteFetchResult(kv.Value.Id, false, null, null, null, "Twelve Data request failed."))
                .ToList();
        }

        var results = new List<QuoteFetchResult>(chunk.Length);

        if (chunk.Length == 1)
        {
            var asset = chunk[0].Value;
            var quote = JsonSerializer.Deserialize<TwelveDataQuote>(json, JsonOptions);
            results.Add(ToResult(asset, quote));
            return results;
        }

        using var doc = JsonDocument.Parse(json);
        foreach (var (symbol, asset) in chunk)
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

        // A /time_series call costs exactly 1 credit regardless of the date range — same shared
        // throttle the quote path uses (D38), so a backfill run and a quote sweep can never
        // jointly overspend the per-minute window or the daily budget.
        var granted = await creditThrottle.TryAcquireAsync(1, cancellationToken);
        if (!granted)
        {
            logger.LogWarning(
                "Twelve Data /time_series for {Symbol} skipped: daily credit budget exhausted",
                asset.ProviderSymbol);
            return HistoryFetchResult.Failed(from, "Twelve Data daily credit budget exhausted.");
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
