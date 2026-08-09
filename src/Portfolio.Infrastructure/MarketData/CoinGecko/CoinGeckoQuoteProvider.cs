using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Dtos;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;

namespace Portfolio.Infrastructure.MarketData.CoinGecko;

/// <summary>
/// Crypto quotes from CoinGecko. Fetches ethereum, amp-token and anvil in a single
/// <c>/simple/price</c> call (verified live) — never one call per coin.
///
/// <c>GetHistoryAsync</c> clamps its request window to <see cref="CoinGeckoOptions.KeylessMaxHistoryDays"/>
/// when running keyless (verified live: a 730-day <c>/market_chart/range</c> request 401s with
/// <c>error_code 10012</c>, "limited to querying historical data within the past 365 days" —
/// a ≤365-day range works and parses exactly). Any asset held longer than that window — e.g. ETH
/// bought in 2021 — would otherwise silently backfill zero history for the pre-window period, so
/// the truncation is reported on <see cref="HistoryFetchResult"/> rather than swallowed into an
/// empty list indistinguishable from an outright failure.
/// </summary>
public sealed class CoinGeckoQuoteProvider(
    HttpClient httpClient,
    IOptions<CoinGeckoOptions> options,
    TimeProvider timeProvider,
    ILogger<CoinGeckoQuoteProvider> logger) : IQuoteProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public QuoteProviderKind Kind => QuoteProviderKind.CoinGecko;

    public async Task<IReadOnlyList<QuoteFetchResult>> GetQuotesAsync(
        IReadOnlyCollection<Asset> assets, CancellationToken cancellationToken)
    {
        if (assets.Count == 0)
        {
            return [];
        }

        var byCoinId = new Dictionary<string, Asset>(StringComparer.Ordinal);
        foreach (var asset in assets)
        {
            if (string.IsNullOrWhiteSpace(asset.ProviderCoinId))
            {
                throw new InvalidOperationException(
                    $"Asset {asset.Id} ({asset.Symbol}) is routed to CoinGecko but has no ProviderCoinId.");
            }

            byCoinId[asset.ProviderCoinId] = asset;
        }

        var idsParam = string.Join(',', byCoinId.Keys);
        var requestUri = $"simple/price?ids={Uri.EscapeDataString(idsParam)}&vs_currencies=usd&include_last_updated_at=true";

        string json;
        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);
            json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "CoinGecko /simple/price for {CoinCount} coins failed with status {StatusCode}",
                    byCoinId.Count,
                    (int)response.StatusCode);
                return byCoinId.Values
                    .Select(a => new QuoteFetchResult(a.Id, false, null, null, null, $"CoinGecko returned HTTP {(int)response.StatusCode}."))
                    .ToList();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "CoinGecko /simple/price for {CoinCount} coins threw", byCoinId.Count);
            return byCoinId.Values
                .Select(a => new QuoteFetchResult(a.Id, false, null, null, null, "CoinGecko request failed."))
                .ToList();
        }

        var parsed = JsonSerializer.Deserialize<Dictionary<string, CoinGeckoSimplePriceEntry>>(json, JsonOptions)
            ?? [];

        var results = new List<QuoteFetchResult>(byCoinId.Count);
        foreach (var (coinId, asset) in byCoinId)
        {
            if (!parsed.TryGetValue(coinId, out var entry))
            {
                results.Add(new QuoteFetchResult(asset.Id, false, null, null, null, $"No price returned for coin '{coinId}'."));
                continue;
            }

            var asOf = entry.LastUpdatedAt is { } ts
                ? DateTimeOffset.FromUnixTimeSeconds(ts)
                : timeProvider.GetUtcNow();

            results.Add(new QuoteFetchResult(asset.Id, true, entry.Usd, "USD", asOf, null));
        }

        return results;
    }

    public async Task<HistoryFetchResult> GetHistoryAsync(
        Asset asset, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(asset.ProviderCoinId))
        {
            throw new InvalidOperationException(
                $"Asset {asset.Id} ({asset.Symbol}) is routed to CoinGecko but has no ProviderCoinId.");
        }

        var effectiveFrom = ClampToProviderWindow(from, to);
        var truncated = effectiveFrom > from;

        var fromUnix = new DateTimeOffset(effectiveFrom.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        // Range end is exclusive-ish in practice on CoinGecko; push to end of day to include `to`.
        var toUnix = new DateTimeOffset(to.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero).ToUnixTimeSeconds();

        var requestUri =
            $"coins/{Uri.EscapeDataString(asset.ProviderCoinId)}/market_chart/range" +
            $"?vs_currency=usd&from={fromUnix}&to={toUnix}";

        using var response = await httpClient.GetAsync(requestUri, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var (errorCode, errorMessage) = TryParseError(json);

            if (errorCode == RangeTooWideErrorCode)
            {
                // Should be unreachable in practice — we clamp before sending the request — but
                // this is exactly the failure mode that must never look like a plain HTTP error:
                // if the provider's real limit and our clamp ever disagree, say so explicitly
                // rather than letting it read as a generic transient failure.
                logger.LogWarning(
                    "CoinGecko /market_chart/range for {CoinId} rejected the range {From}..{To} as too wide " +
                    "even after clamping to the {MaxDays}-day keyless window: {Message}",
                    asset.ProviderCoinId,
                    effectiveFrom,
                    to,
                    options.Value.KeylessMaxHistoryDays,
                    errorMessage);
            }
            else
            {
                logger.LogWarning(
                    "CoinGecko /market_chart/range for {CoinId} failed with status {StatusCode}",
                    asset.ProviderCoinId,
                    (int)response.StatusCode);
            }

            return HistoryFetchResult.Failed(from, errorMessage ?? $"CoinGecko returned HTTP {(int)response.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("prices", out var pricesElement) || pricesElement.ValueKind != JsonValueKind.Array)
        {
            return HistoryFetchResult.Ok([], from);
        }

        // Parsed directly off JsonElement (never via a double-typed model) so the price never
        // round-trips through System.Double before it becomes a decimal.
        var points = new List<PriceHistoryPoint>();
        var seenDates = new HashSet<DateOnly>();
        foreach (var pair in pricesElement.EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2)
            {
                continue;
            }

            var unixMillis = pair[0].GetInt64();
            var price = pair[1].GetDecimal();
            var date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(unixMillis).UtcDateTime);

            // CoinGecko returns intraday points for short ranges; keep the last point per day.
            if (seenDates.Add(date))
            {
                points.Add(new PriceHistoryPoint(date, price, "USD"));
            }
            else
            {
                var index = points.FindIndex(p => p.Date == date);
                if (index >= 0)
                {
                    points[index] = new PriceHistoryPoint(date, price, "USD");
                }
            }
        }

        var ordered = points.OrderBy(p => p.Date).ToList();
        return new HistoryFetchResult(ordered, from, effectiveFrom, truncated, true, null);
    }

    /// <summary>Verified live: <c>error_code 10012</c> is what a 401 response carries when a
    /// keyless request's range is too wide.</summary>
    private const int RangeTooWideErrorCode = 10012;

    /// <summary>
    /// A configured <see cref="CoinGeckoOptions.ApiKey"/> lifts CoinGecko's 365-day keyless
    /// history limit, so the clamp only applies when running keyless — never hard-code 365 here.
    /// </summary>
    private DateOnly ClampToProviderWindow(DateOnly from, DateOnly to)
    {
        if (options.Value.HasApiKey)
        {
            return from;
        }

        var earliestAllowed = to.AddDays(-(options.Value.KeylessMaxHistoryDays - 1));
        return from < earliestAllowed ? earliestAllowed : from;
    }

    private static (int? ErrorCode, string? Message) TryParseError(string json)
    {
        try
        {
            var error = JsonSerializer.Deserialize<CoinGeckoErrorResponse>(json, JsonOptions);
            var status = error?.Error?.Status;
            return (status?.ErrorCode, status?.ErrorMessage);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
