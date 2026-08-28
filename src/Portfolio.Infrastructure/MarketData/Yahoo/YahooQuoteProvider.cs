using System.Text.Json;
using Microsoft.Extensions.Logging;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Dtos;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;

namespace Portfolio.Infrastructure.MarketData.Yahoo;

/// <summary>
/// Z74 (SGX) quotes and history from Yahoo Finance's unofficial chart endpoint. This is the
/// user's chosen source because Twelve Data's free tier cannot serve Z74 at all (verified live:
/// <c>symbol=Z74&amp;exchange=SGX</c> 404s with "available starting with the Pro or Venture
/// plan"). Yahoo has no key, no SLA, and no documented stability guarantee, so every call here is
/// treated as fallible: a failure returns an empty/failed result rather than throwing, so a
/// refresh covering multiple assets is never taken down by Z74 alone — the caller is expected to
/// fall back to the last stored quote.
/// </summary>
public sealed class YahooQuoteProvider(
    HttpClient httpClient,
    TimeProvider timeProvider,
    ILogger<YahooQuoteProvider> logger) : IQuoteProvider, IDividendProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public QuoteProviderKind Kind => QuoteProviderKind.Yahoo;

    public async Task<IReadOnlyList<QuoteFetchResult>> GetQuotesAsync(
        IReadOnlyCollection<Asset> assets, CancellationToken cancellationToken)
    {
        // Yahoo's chart endpoint is per-symbol (no batch form), but today only Z74 is routed
        // here, so the lack of batching does not cost anything against a shared credit budget
        // the way it would for Twelve Data.
        var results = new List<QuoteFetchResult>(assets.Count);

        foreach (var asset in assets)
        {
            results.Add(await GetOneQuoteAsync(asset, cancellationToken));
        }

        return results;
    }

    private async Task<QuoteFetchResult> GetOneQuoteAsync(Asset asset, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(asset.ProviderSymbol))
        {
            throw new InvalidOperationException(
                $"Asset {asset.Id} ({asset.Symbol}) is routed to Yahoo but has no ProviderSymbol.");
        }

        try
        {
            var requestUri = $"v8/finance/chart/{Uri.EscapeDataString(asset.ProviderSymbol)}?range=5d&interval=1d";
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Yahoo chart quote for {Symbol} failed with status {StatusCode}",
                    asset.ProviderSymbol,
                    (int)response.StatusCode);
                return new QuoteFetchResult(asset.Id, false, null, null, null, $"Yahoo returned HTTP {(int)response.StatusCode}.");
            }

            var payload = JsonSerializer.Deserialize<YahooChartResponse>(json, JsonOptions);
            var result = payload?.Chart?.Result?.FirstOrDefault();

            if (payload?.Chart?.Error is { } error)
            {
                return new QuoteFetchResult(asset.Id, false, null, null, null, error.Description ?? error.Code ?? "Yahoo chart error.");
            }

            if (result?.Meta is not { } meta)
            {
                return new QuoteFetchResult(asset.Id, false, null, null, null, "Yahoo chart response had no meta.");
            }

            var asOf = meta.RegularMarketTime is { } ts
                ? DateTimeOffset.FromUnixTimeSeconds(ts)
                : timeProvider.GetUtcNow();

            return new QuoteFetchResult(asset.Id, true, meta.RegularMarketPrice, meta.Currency ?? asset.Currency, asOf, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Yahoo chart quote for {Symbol} threw", asset.ProviderSymbol);
            return new QuoteFetchResult(asset.Id, false, null, null, null, "Yahoo request failed.");
        }
    }

    public async Task<HistoryFetchResult> GetHistoryAsync(
        Asset asset, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(asset.ProviderSymbol))
        {
            throw new InvalidOperationException(
                $"Asset {asset.Id} ({asset.Symbol}) is routed to Yahoo but has no ProviderSymbol.");
        }

        // period1/period2 (unix seconds) give exact control over the range, unlike the
        // relative "range=5y" form — needed so backfill can request precisely [from, to].
        var period1 = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        var period2 = new DateTimeOffset(to.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero).ToUnixTimeSeconds();

        try
        {
            var requestUri =
                $"v8/finance/chart/{Uri.EscapeDataString(asset.ProviderSymbol)}" +
                $"?period1={period1}&period2={period2}&interval=1d";
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Yahoo chart history for {Symbol} failed with status {StatusCode}",
                    asset.ProviderSymbol,
                    (int)response.StatusCode);
                return HistoryFetchResult.Failed(from, $"Yahoo returned HTTP {(int)response.StatusCode}.");
            }

            var payload = JsonSerializer.Deserialize<YahooChartResponse>(json, JsonOptions);
            var result = payload?.Chart?.Result?.FirstOrDefault();

            if (payload?.Chart?.Error is not null || result is null)
            {
                logger.LogWarning("Yahoo chart history for {Symbol} returned no result", asset.ProviderSymbol);
                return HistoryFetchResult.Failed(from, "Yahoo chart response had no result.");
            }

            var timestamps = result.Timestamp ?? [];
            var closes = result.Indicators?.Quote?.FirstOrDefault()?.Close ?? [];
            var currency = result.Meta?.Currency ?? asset.Currency;

            var points = new List<PriceHistoryPoint>(timestamps.Count);
            for (var i = 0; i < timestamps.Count && i < closes.Count; i++)
            {
                if (closes[i] is not { } close)
                {
                    // Null close marks a non-trading day inside the range — skip, don't zero-fill.
                    continue;
                }

                var date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(timestamps[i]).UtcDateTime);

                // Verified live: Yahoo's chart "close" series carries float32/float64
                // round-trip noise in the JSON text itself (e.g. "4.440000057220459" instead of
                // "4.44") — this is upstream data noise, not something our parsing introduces
                // (GetDecimal() reads the literal text exactly). Rounding to 6dp discards that
                // noise while keeping far more precision than an SGX equity (cent increments)
                // ever needs.
                points.Add(new PriceHistoryPoint(date, Math.Round(close, 6, MidpointRounding.AwayFromZero), currency));
            }

            return HistoryFetchResult.Ok(points.OrderBy(p => p.Date).ToList(), from);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Yahoo chart history for {Symbol} threw", asset.ProviderSymbol);
            return HistoryFetchResult.Failed(from, "Yahoo request failed.");
        }
    }

    /// <summary>
    /// See <see cref="IDividendProvider"/> — this is called for <b>every</b> stock, not just the
    /// assets this class is routed to as an <see cref="IQuoteProvider"/>, so the symbol it uses
    /// comes from <see cref="YahooDividendSymbolResolver"/> rather than the routing-gated
    /// <see cref="Asset.ProviderSymbol"/> check the quote/history methods above use. Reuses this
    /// same <c>HttpClient</c> registration (browser User-Agent, <c>RedactingLoggingHandler</c>) —
    /// deliberately not a separate typed client.
    /// </summary>
    public async Task<DividendHistoryFetchResult> GetDividendHistoryAsync(
        Asset asset, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var yahooSymbol = YahooDividendSymbolResolver.Resolve(asset);
        if (yahooSymbol is null)
        {
            throw new InvalidOperationException(
                $"Asset {asset.Id} ({asset.Symbol}) has no Yahoo dividend symbol — only Stock assets pay dividends.");
        }

        var period1 = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        var period2 = new DateTimeOffset(to.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero).ToUnixTimeSeconds();

        try
        {
            var requestUri =
                $"v8/finance/chart/{Uri.EscapeDataString(yahooSymbol)}" +
                $"?period1={period1}&period2={period2}&interval=1d&events=div";
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Yahoo chart dividends for {Symbol} failed with status {StatusCode}",
                    yahooSymbol,
                    (int)response.StatusCode);
                return DividendHistoryFetchResult.Failed($"Yahoo returned HTTP {(int)response.StatusCode}.");
            }

            var payload = JsonSerializer.Deserialize<YahooChartResponse>(json, JsonOptions);
            var result = payload?.Chart?.Result?.FirstOrDefault();

            if (payload?.Chart?.Error is not null || result is null)
            {
                logger.LogWarning("Yahoo chart dividends for {Symbol} returned no result", yahooSymbol);
                return DividendHistoryFetchResult.Failed("Yahoo chart response had no result.");
            }

            var currency = result.Meta?.Currency ?? asset.Currency;
            var events = result.Events?.Dividends?.Values is { } dividendValues
                ? (IEnumerable<YahooDividendEvent>)dividendValues
                : [];

            var points = events
                .Select(e => new DividendPoint(
                    DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(e.Date).UtcDateTime),
                    e.Amount,
                    currency))
                .OrderBy(p => p.ExDate)
                .ToList();

            return DividendHistoryFetchResult.Ok(points);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Yahoo chart dividends for {Symbol} threw", yahooSymbol);
            return DividendHistoryFetchResult.Failed("Yahoo request failed.");
        }
    }
}
