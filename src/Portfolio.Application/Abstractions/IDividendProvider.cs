using Portfolio.Application.Dtos;
using Portfolio.Domain.Entities;

namespace Portfolio.Application.Abstractions;

/// <summary>
/// Fetches dividend ex-date history for one stock asset. Unlike <see cref="IQuoteProvider"/>, this
/// is <b>not</b> dispatched per <see cref="Domain.Enums.QuoteProviderKind"/> — Yahoo Finance is the
/// dividend source for every stock, including Twelve Data-routed US equities, because Twelve
/// Data's free tier gates fundamentals data and Yahoo is the only free option. The one
/// implementation (<c>Infrastructure.MarketData.Yahoo.YahooQuoteProvider</c>, which also implements
/// <see cref="IQuoteProvider"/>) resolves the Yahoo-compatible symbol itself — see
/// <c>YahooDividendSymbolResolver</c> for the explicit, documented mapping — rather than relying on
/// <see cref="Asset.QuoteProviderKind"/> to decide anything.
/// </summary>
public interface IDividendProvider
{
    /// <summary>
    /// Fetches dividend ex-dates and per-share amounts over <c>[from, to]</c> inclusive, for
    /// historical backfill. Never throws for an ordinary upstream failure — see
    /// <see cref="DividendHistoryFetchResult"/> for how success and outright failure are
    /// distinguished in the return value. Throws only if <paramref name="asset"/> has no
    /// resolvable Yahoo dividend symbol at all (i.e. is not a stock) — callers are expected to
    /// filter to <see cref="Domain.Enums.AssetClass.Stock"/> before calling this.
    /// </summary>
    Task<DividendHistoryFetchResult> GetDividendHistoryAsync(
        Asset asset, DateOnly from, DateOnly to, CancellationToken cancellationToken);
}
