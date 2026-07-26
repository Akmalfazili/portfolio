using Portfolio.Application.Dtos;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Abstractions;

/// <summary>
/// Fetches quotes and daily history for assets from a single upstream market-data source.
/// One implementation per <see cref="QuoteProviderKind"/> lives in <c>Portfolio.Infrastructure</c>
/// — this interface (and the DTOs it returns) is all <c>Portfolio.Application</c> ever sees, so
/// HTTP-client and provider-SDK types never leak into the service layer.
/// </summary>
public interface IQuoteProvider
{
    /// <summary>Which provider this implementation talks to.</summary>
    QuoteProviderKind Kind { get; }

    /// <summary>
    /// Fetches the latest quote for every asset in one batched upstream call where the provider
    /// supports it. Returns one <see cref="QuoteFetchResult"/> per input asset, in no particular
    /// order — a failure for one asset never suppresses the others' results.
    /// </summary>
    Task<IReadOnlyList<QuoteFetchResult>> GetQuotesAsync(
        IReadOnlyCollection<Asset> assets, CancellationToken cancellationToken);

    /// <summary>
    /// Fetches daily closes for a single asset over <c>[from, to]</c> inclusive, for historical
    /// backfill. Points are ordered ascending by date. Never throws for an ordinary upstream
    /// failure — see <see cref="HistoryFetchResult"/> for how success, provider-window
    /// truncation, and outright failure are distinguished in the return value.
    /// </summary>
    Task<HistoryFetchResult> GetHistoryAsync(
        Asset asset, DateOnly from, DateOnly to, CancellationToken cancellationToken);
}
