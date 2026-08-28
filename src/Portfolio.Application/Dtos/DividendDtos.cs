namespace Portfolio.Application.Dtos;

/// <summary>
/// Whether a stock's dividend figures on <see cref="HoldingDto"/> / <see cref="AssetDividendHistoryDto"/>
/// reflect a real computation or something else entirely. A bare <c>$0.00</c> cannot distinguish
/// "this stock genuinely pays no dividend" from "dividends have never been fetched for it" from
/// "the last fetch failed" — exactly the reporting-layer ambiguity behind this project's most
/// repeated defect family (D10/D26/D33/D35/D38: a healthy-looking report hiding a real failure).
/// Backed by <see cref="Domain.Entities.AssetDividendState"/>.
/// </summary>
public enum DividendCoverageStatus
{
    /// <summary>At least one dividend backfill attempt has succeeded for this asset — even if it
    /// found zero events, which is a real, valid answer for a stock that pays no dividend. The
    /// figures shown are a genuine, current computation.</summary>
    Covered,

    /// <summary>No dividend backfill has ever attempted this asset yet (e.g. just created, or the
    /// scheduled backfill has not ticked since). Not a failure — figures are null, never a
    /// misleading zero, and the next scheduled run will pick it up.</summary>
    NotYetFetched,

    /// <summary>The most recent dividend backfill attempt for this asset failed. Figures reflect
    /// whatever was successfully computed as of the last successful attempt (if any) and may be
    /// stale — a caller should flag this rather than trust it silently.</summary>
    FetchFailed,
}

/// <summary>
/// One dividend ex-date payment, as it applied to the holding's actual entitlement — not simply
/// the declared per-share amount. See <c>IDividendIncomeCalculator</c> for the ex-date entitlement
/// rule. These figures are <b>estimated from ex-date holdings</b>, not recorded cash actually
/// received: no withholding tax, no DRIP/scrip reinvestment, no brokerage payment-date modelling.
/// </summary>
public sealed record DividendPaymentDto(
    DateOnly ExDate,
    decimal AmountPerShareNative,
    string Currency,
    decimal UnitsHeldAtExDate,
    decimal IncomeUsd);

/// <summary>
/// Full dividend payment history for one stock asset, powering the asset detail page. Stocks
/// only — requesting this for a crypto asset id is rejected by the service before this DTO is
/// ever built (see <c>IDividendService</c>), the same shape as <see cref="AssetPerformanceDto"/>.
/// </summary>
public sealed record AssetDividendHistoryDto(
    int AssetId,
    string Symbol,
    string Name,
    string Currency,

    /// <summary>Null exactly when <see cref="CoverageStatus"/> is <see cref="DividendCoverageStatus.NotYetFetched"/>
    /// — never a bare zero standing in for "unknown".</summary>
    decimal? Trailing12MonthIncomeUsd,

    /// <summary>Null exactly when <see cref="CoverageStatus"/> is <see cref="DividendCoverageStatus.NotYetFetched"/>.</summary>
    decimal? AllTimeIncomeUsd,
    DividendCoverageStatus CoverageStatus,

    /// <summary>Newest ex-date first.</summary>
    IReadOnlyList<DividendPaymentDto> Payments);
