using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Dtos;
using Portfolio.Domain.Entities;

namespace Portfolio.Application.Services;

/// <summary>
/// Caches a live FX spot rate (currently only USD/SGD, for the zakat report's crypto lines) behind
/// a TTL, so a page load does not spend a Twelve Data credit on every request. See
/// <see cref="Domain.Entities.FxSpotQuote"/>'s remarks for why the TTL is measured from
/// <see cref="FxSpotQuote.FetchedAt"/> rather than the provider's own <see cref="FxSpotQuote.AsOf"/>
/// timestamp.
///
/// <para><b>Never writes to <see cref="Domain.Entities.FxRate"/>.</b> That table is daily closes
/// only; an intraday spot stored there would be picked up by <c>PriceBackfillService</c>'s
/// already-have-this-date skip and frozen in permanently as "the close" for that day.</para>
///
/// <para><b>Never throws.</b> This sits on the zakat report's read path. A provider failure —
/// <see cref="IFxRateProvider.GetSpotRateAsync"/> returning null (throttle denial, non-success HTTP
/// status, provider error body) or throwing outright (network failure, deserialization) — falls
/// back to the stored row if one exists, or null if there has never been a successful fetch. Same
/// treat-every-call-as-fallible posture as the Yahoo provider.</para>
/// </summary>
public sealed class FxSpotRateService(
    IPortfolioDbContext db,
    IFxRateProvider fxRateProvider,
    TimeProvider timeProvider,
    IOptions<FxSpotRateOptions> options,
    ILogger<FxSpotRateService> logger) : IFxSpotRateService
{
    public async Task<FxSpotQuote?> GetOrRefreshAsync(
        string baseCurrency, string quoteCurrency, CancellationToken cancellationToken)
    {
        var stored = await db.FxSpotQuotes
            .FirstOrDefaultAsync(q => q.Base == baseCurrency && q.Quote == quoteCurrency, cancellationToken);

        var now = timeProvider.GetUtcNow();

        // TTL keyed off FetchedAt, never AsOf — see FxSpotQuote's remarks. A weekend, where the
        // provider keeps returning Friday's AsOf indefinitely, must still expire on schedule.
        if (stored is not null && now - stored.FetchedAt < options.Value.Ttl)
        {
            return stored;
        }

        FxSpotResult? result;
        try
        {
            result = await fxRateProvider.GetSpotRateAsync(baseCurrency, quoteCurrency, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Live {Base}/{Quote} spot rate fetch threw; falling back to any stored rate",
                baseCurrency,
                quoteCurrency);
            result = null;
        }

        if (result is null)
        {
            if (stored is not null)
            {
                logger.LogWarning(
                    "Live {Base}/{Quote} spot rate unavailable; serving stored rate fetched at {FetchedAt}",
                    baseCurrency,
                    quoteCurrency,
                    stored.FetchedAt);
            }

            // Stale-but-real if we have one, null if we have never had a successful fetch — never
            // an exception either way.
            return stored;
        }

        if (stored is not null)
        {
            stored.Rate = result.Rate;
            stored.AsOf = result.AsOf;
            stored.FetchedAt = now;
        }
        else
        {
            stored = new FxSpotQuote
            {
                Base = baseCurrency,
                Quote = quoteCurrency,
                Rate = result.Rate,
                AsOf = result.AsOf,
                FetchedAt = now,
            };
            db.AddFxSpotQuote(stored);
        }

        await db.SaveChangesAsync(cancellationToken);
        return stored;
    }
}
