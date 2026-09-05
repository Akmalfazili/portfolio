namespace Portfolio.Application.Dtos;

/// <summary>
/// Per-asset outcome of the zakat calculation. Six distinct statuses, never one "skipped" bucket —
/// see zakat.md §6, and the D10/D26/D33/D35/D38/D45 defect family this taxonomy exists to avoid
/// repeating: a report whose skip list cannot distinguish "a correct answer of zero" from "a
/// missing input" from "attempted and failed" is how a silent data-staleness bug hides behind a
/// healthy-looking total.
/// </summary>
public enum ZakatAssetStatus
{
    /// <summary>Valued successfully. Counts toward the total.</summary>
    Included,

    /// <summary>
    /// Quantity held at the fiscal year end (stock) is legitimately zero — a correct answer, not an
    /// error. Counts toward the total, as zero. Never merged with
    /// <see cref="FiscalYearEndNotConfigured"/>: that one is a missing input, this one is a real
    /// result.
    /// </summary>
    NotHeldAtFiscalYearEnd,

    /// <summary>
    /// No <c>FiscalYearEndMonth</c>/<c>FiscalYearEndDay</c> recorded on the asset — calculation was
    /// never attempted. Excluded from the total, and counted in <see cref="ZakatReportDto.ExcludedAssetCount"/>.
    /// </summary>
    FiscalYearEndNotConfigured,

    /// <summary>
    /// No stored <c>PriceHistory</c> row at or before the resolved fiscal year end date. Excluded —
    /// see <c>Services.Calculators.CloseAsOf</c>, which deliberately does not fall back to the
    /// earliest close on file the way <c>Services.Calculators.FxRateResolver</c> does.
    /// </summary>
    NoCloseOnOrBeforeFiscalYearEnd,

    /// <summary>
    /// No USD/SGD <c>FxRate</c> rows exist at all — not even one to carry forward or fall back to.
    /// Excluded. See zakat.md §7.4: every USD/SGD row in this database exists only because Z74 is an
    /// SGD asset, so this failure is structurally possible for the whole report, not only for one
    /// asset, if Z74 is ever sold out of entirely.
    /// </summary>
    NoFxRateForCloseDate,

    /// <summary>
    /// Crypto with no live or stale <c>PriceQuote</c> at all. Crypto keeps no <c>PriceHistory</c> by
    /// design, so a missing quote is a hard exclusion, not a fallback. Excluded.
    /// </summary>
    NoQuote,
}

/// <summary>
/// One stock asset's contribution to the zakat report, valued at ITS OWN last fiscal year end —
/// see zakat.md §2.2/§2.7. Every stock asset appears here, including ones holding zero units today
/// (zakat.md §2.8) — this list is never filtered to current holdings.
/// </summary>
public sealed record ZakatStockLineDto(
    int AssetId,
    string Symbol,
    string Name,

    /// <summary>Native trading currency — "USD" or "SGD" for every asset in this portfolio today.</summary>
    string Currency,
    ZakatAssetStatus Status,

    /// <summary>
    /// The resolved fiscal year end date used for this valuation — null only when
    /// <see cref="Status"/> is <see cref="ZakatAssetStatus.FiscalYearEndNotConfigured"/>. Per
    /// zakat.md §2.7, this date differs asset to asset; the report is a sum across different dates,
    /// not one point in time, and this field is what keeps that visible rather than implied.
    /// </summary>
    DateOnly? FiscalYearEndDate,

    /// <summary>
    /// Units held on <see cref="FiscalYearEndDate"/> — null only when that date itself is null
    /// (<see cref="ZakatAssetStatus.FiscalYearEndNotConfigured"/>). Zero is a real, reported value
    /// (<see cref="ZakatAssetStatus.NotHeldAtFiscalYearEnd"/>), never conflated with "unknown".
    /// </summary>
    decimal? QuantityHeld,

    /// <summary>Closing price in <see cref="Currency"/> on <see cref="CloseDateUsed"/>. Null unless
    /// <see cref="Status"/> is <see cref="ZakatAssetStatus.Included"/>.</summary>
    decimal? CloseNative,

    /// <summary>
    /// The date of the <c>PriceHistory</c> row actually used — the greatest date at or before
    /// <see cref="FiscalYearEndDate"/>. May differ from <see cref="FiscalYearEndDate"/> itself; see
    /// <see cref="CloseDateExact"/>.
    /// </summary>
    DateOnly? CloseDateUsed,

    /// <summary>
    /// True when <see cref="CloseDateUsed"/> equals <see cref="FiscalYearEndDate"/> exactly; false
    /// when it was carried forward from an earlier trading day (a weekend, a holiday, or — per
    /// zakat.md §4.1's wrinkle — a fiscal year end that lands on today, which never has a close of
    /// its own yet). Null unless <see cref="Status"/> is <see cref="ZakatAssetStatus.Included"/>.
    /// </summary>
    bool? CloseDateExact,

    /// <summary>
    /// The date of the USD/SGD <c>FxRate</c> row actually used to convert <see cref="CloseNative"/>
    /// to SGD. Null for an SGD-native asset (no FX conversion at all — see zakat.md §4.2) and null
    /// unless <see cref="Status"/> is <see cref="ZakatAssetStatus.Included"/>.
    /// </summary>
    DateOnly? FxDateUsed,

    /// <summary>
    /// True when <see cref="FxDateUsed"/> is AFTER <see cref="CloseDateUsed"/> — meaning no FX rate
    /// existed at or before the close date at all, and the resolver fell back to the earliest rate
    /// on file rather than genuinely carrying one forward (see
    /// <c>Services.Calculators.FxRateResolution.CarriedBack</c>). False for a normal forward
    /// carry-forward (a weekend). Null for an SGD-native asset or when not <see cref="ZakatAssetStatus.Included"/>.
    /// </summary>
    bool? FxCarriedBack,

    /// <summary>
    /// The USD/SGD rate actually used to convert <see cref="CloseNative"/> to SGD — SGD per USD, so
    /// converting multiplies (zakat.md §4.3). Null for an SGD-native asset (Z74) — that path does no
    /// FX conversion at all, so there is no rate to report; never <c>1.0</c> there, since a real rate
    /// of one and "no conversion happened" are exactly the not-attempted-vs-attempted distinction
    /// this file's <see cref="ZakatAssetStatus"/> doc comments exist to protect. Null for every other
    /// non-<see cref="ZakatAssetStatus.Included"/> status.
    /// </summary>
    decimal? FxRateUsed,

    /// <summary>
    /// This asset's contribution to the zakat total, in SGD. Zero when
    /// <see cref="ZakatAssetStatus.NotHeldAtFiscalYearEnd"/> (a real zero, counted). Null for every
    /// other non-<see cref="ZakatAssetStatus.Included"/> status — excluded from the total entirely,
    /// never presented as zero.
    /// </summary>
    decimal? ValueSgd);

/// <summary>
/// One crypto asset's contribution to the zakat report. Crypto follows no MUIS ruling — see
/// zakat.md §2.3 — and is valued at TODAY's price (or the <paramref name="asOf"/> query date), never
/// a fiscal year end, since crypto has none and keeps no price history to look one up in.
/// </summary>
public sealed record ZakatCryptoLineDto(
    int AssetId,
    string Symbol,
    string Name,
    string Currency,
    ZakatAssetStatus Status,

    /// <summary>Units held as of the report's valuation date (today, or <c>?asOf=</c>). Always
    /// computed, regardless of <see cref="Status"/> — unlike the stock line, nothing about
    /// resolving this quantity can fail.</summary>
    decimal QuantityHeld,

    /// <summary>Price in USD. Null only when <see cref="Status"/> is <see cref="ZakatAssetStatus.NoQuote"/>.</summary>
    decimal? PriceUsd,

    /// <summary>
    /// Whether <see cref="PriceUsd"/> came from a live quote or a stale one — carried through
    /// rather than flattened, per zakat.md §4.4. Reuses the same <see cref="PriceSource"/>
    /// classification the portfolio summary uses; note that today it evaluates to
    /// <see cref="Dtos.PriceSource.Live"/> for every crypto quote regardless of age, because CoinGecko
    /// has no market session for <c>QuoteFreshness</c> to compare against — see that type's own
    /// remarks. Null only when <see cref="Status"/> is <see cref="ZakatAssetStatus.NoQuote"/>.
    /// </summary>
    PriceSource? PriceSource,

    /// <summary>UTC instant the quote was captured. Null only when <see cref="Status"/> is
    /// <see cref="ZakatAssetStatus.NoQuote"/>.</summary>
    DateTimeOffset? PriceAsOf,

    /// <summary>The USD/SGD <c>FxRate</c> date actually used. Null unless <see cref="Status"/> is
    /// <see cref="ZakatAssetStatus.Included"/>.</summary>
    DateOnly? FxDateUsed,

    /// <summary>Same meaning as <see cref="ZakatStockLineDto.FxCarriedBack"/>.</summary>
    bool? FxCarriedBack,

    /// <summary>Same meaning as <see cref="ZakatStockLineDto.FxRateUsed"/>. Crypto always converts
    /// through this rate — there is no SGD-native crypto asset — so it is null only for a
    /// non-<see cref="ZakatAssetStatus.Included"/> status.</summary>
    decimal? FxRateUsed,

    /// <summary>This asset's contribution to the zakat total, in SGD. Null unless
    /// <see cref="Status"/> is <see cref="ZakatAssetStatus.Included"/>.</summary>
    decimal? ValueSgd);

/// <summary>
/// The full zakat-on-shares report — see zakat.md end to end. This is the one sanctioned exception
/// to asset-class segregation: <see cref="Stocks"/> and <see cref="Crypto"/> both appear here,
/// because MUIS requires a single grand total, but they stay in separate lists with separate
/// subtotals so nothing aggregates implicitly — <see cref="TotalZakatableSgd"/> is the only place
/// the two classes actually meet. Nothing here is persisted; it is computed fresh on every read.
/// </summary>
public sealed record ZakatReportDto(
    /// <summary>The valuation reference date this report was computed against — today unless the
    /// caller passed <c>?asOf=</c>. Used as "today" for crypto valuation and as the reference point
    /// each stock's fiscal year end is resolved backward from.</summary>
    DateOnly AsOf,
    IReadOnlyList<ZakatStockLineDto> Stocks,
    IReadOnlyList<ZakatCryptoLineDto> Crypto,

    /// <summary>Sum of every stock line's <see cref="ZakatStockLineDto.ValueSgd"/> (treating null as
    /// excluded, not zero).</summary>
    decimal StockZakatableSgd,

    /// <summary>
    /// Sum of every crypto line's <see cref="ZakatCryptoLineDto.ValueSgd"/>. Labelled separately
    /// from <see cref="StockZakatableSgd"/> on purpose — zakat.md §2.3 is explicit that the crypto
    /// valuation basis (today's price) is a user convention, not a MUIS ruling, and must never be
    /// presented with the same authority as the share calculation.
    /// </summary>
    decimal CryptoZakatableSgd,
    decimal TotalZakatableSgd,

    /// <summary>2.5% of <see cref="TotalZakatableSgd"/> — the nisab comparison itself is
    /// deliberately NOT made here; see zakat.md §2.4. This is what would be owed if the total is
    /// above whatever nisab figure the user checks separately on zakat.sg.</summary>
    decimal ZakatPayableSgd,

    /// <summary>
    /// Count of every asset (stock + crypto) whose <see cref="ZakatAssetStatus"/> is anything other
    /// than <see cref="ZakatAssetStatus.Included"/> or <see cref="ZakatAssetStatus.NotHeldAtFiscalYearEnd"/>
    /// — i.e. excluded from <see cref="TotalZakatableSgd"/> entirely. A non-zero count means the
    /// total is knowingly incomplete; the caller must caveat it rather than presenting it as final,
    /// the same principle as <see cref="PortfolioSummaryDto.UnpricedHoldingsCount"/>.
    /// </summary>
    int ExcludedAssetCount);

/// <summary>One entry in the zakat payment ledger — see <c>Domain.Entities.ZakatPayment</c> for why
/// this is deliberately unlinked to the calculation above.</summary>
public sealed record ZakatPaymentDto(int Id, DateOnly PaidOn, decimal AmountSgd);

public sealed record CreateZakatPaymentRequest(DateOnly PaidOn, decimal AmountSgd);

public sealed record UpdateZakatPaymentRequest(DateOnly PaidOn, decimal AmountSgd);
