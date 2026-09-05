namespace Portfolio.Domain.Entities;

/// <summary>
/// The latest live spot rate for a currency pair, fetched on demand (currently only for the zakat
/// report's crypto lines — see <c>Application.Services.IFxSpotRateService</c>). One row per pair,
/// overwritten on refresh — the same shape and the same "overwritten, not appended" idiom as
/// <see cref="PriceQuote"/>.
///
/// <para><b><see cref="AsOf"/> and <see cref="FetchedAt"/> are both required and must never be
/// collapsed into one field.</b> <see cref="AsOf"/> is the provider's own timestamp for the rate;
/// <see cref="FetchedAt"/> is when THIS process called the provider. On a weekend, Twelve Data's
/// <c>/exchange_rate</c> keeps returning Friday's close timestamp indefinitely — <see cref="AsOf"/>
/// stops advancing. A cache TTL keyed off <see cref="AsOf"/> would therefore never be satisfied on
/// a weekend and would re-spend a Twelve Data credit on every single page load, forever. The TTL
/// in <c>IFxSpotRateService</c> is keyed off <see cref="FetchedAt"/> for exactly this reason —
/// "how long ago did WE last ask", never "how fresh does the provider claim its number is".</para>
///
/// <para><b>This table deliberately hangs off nothing.</b> It has no foreign key to
/// <see cref="Asset"/> or anything else — a currency pair is not owned by any one asset — so
/// <c>AssetDeleteCascadeTests</c> must not grow a case for it. Same reasoning as
/// <see cref="ZakatPayment"/>'s own remarks.</para>
///
/// <para><b>Never a daily close.</b> This is the mirror of "a close is never written into
/// <see cref="PriceQuote"/>": an intraday spot must never be written into <see cref="FxRate"/>
/// either. <c>PriceBackfillService</c> skips any date it already holds a row for, so an intraday
/// spot stored as today's <see cref="FxRate"/> row would freeze in permanently and become the
/// "close" every historical report reads from that day forward.</para>
/// </summary>
public class FxSpotQuote
{
    public int Id { get; set; }

    /// <summary>ISO 4217 base currency, e.g. "USD".</summary>
    public required string Base { get; set; }

    /// <summary>ISO 4217 quote currency, e.g. "SGD".</summary>
    public required string Quote { get; set; }

    /// <summary>Units of <see cref="Quote"/> per one unit of <see cref="Base"/> — same convention
    /// as <see cref="FxRate.Rate"/>.</summary>
    public decimal Rate { get; set; }

    /// <summary>The provider's own timestamp for this rate. Never used for cache-freshness
    /// decisions — see the class remarks.</summary>
    public DateTimeOffset AsOf { get; set; }

    /// <summary>When THIS process called the provider and got this rate. The TTL clock runs off
    /// this field, never <see cref="AsOf"/> — see the class remarks.</summary>
    public DateTimeOffset FetchedAt { get; set; }
}
