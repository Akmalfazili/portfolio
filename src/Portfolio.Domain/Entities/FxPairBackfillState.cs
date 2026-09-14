namespace Portfolio.Domain.Entities;

/// <summary>
/// The current daily-close backfill coverage of one FX pair (today only USD/SGD) — the FX
/// equivalent of <see cref="AssetPriceHistoryState"/>, added for the refresh-catch-up feature.
///
/// <para><b>Why FX needs this too, not just the asset leg.</b> <c>PriceBackfillService.RunAsyncCore</c>
/// never requests an FX rate beyond <c>fxCap</c> (one UTC calendar day behind "now" — see
/// <see cref="Calculators.PriceBackfillCapCalculator.ComputeFxCap"/> and the D53 remarks on why).
/// A naive planner rule comparing stored <c>FxRate</c> coverage against a MARKET's settled cap
/// (e.g. SGX's) rather than <c>fxCap</c> would read FX as "missing" for hours every day — after SGX
/// settles for day D, SGX's cap is D but <c>fxCap</c> is only D−1, so a market-cap-only rule spends a
/// Twelve Data credit on every catch-up click between SGX's close and the next UTC day rollover,
/// forever, without ever being able to insert anything (the D+0 rate does not exist to fetch yet).
/// Capping the planner's required date at <c>fxCap</c> closes most of that, but Twelve Data does not
/// reliably publish a bar for every calendar day either — measured against the live
/// <c>FxRates</c> table (2025-09-01 onward): 54 Fridays but only 35 Sundays have a stored USD/SGD
/// row. A Monday-evening required date of "the most recent Sunday" then has no stored bar to compare
/// against and reads as missing forever, the exact permanent-gap trap
/// <see cref="AssetPriceHistoryState"/> exists to close for the asset leg — this table closes the
/// same trap for FX, on the same "the provider was successfully asked" principle.</para>
///
/// <para>Keyed by (<see cref="Base"/>, <see cref="Quote"/>), not an <c>Asset</c> id — deliberately
/// hangs off no other table (like <c>ZakatPayment</c>/<c>FxSpotQuote</c>), since an FX pair is not
/// owned by any single asset and must survive that asset being deleted. No cascade, no delete path
/// needed in <c>AssetService.DeleteAsync</c>.</para>
///
/// <para>Written by <c>PriceBackfillService.RunAsyncCore</c>'s FX loop for EVERY attempt, whatever
/// the trigger — mirrors <see cref="AssetPriceHistoryState"/>'s semantics exactly: a failure updates
/// <see cref="LastAttemptedAt"/>/<see cref="LastRunSuccess"/>/<see cref="LastError"/> but must never
/// touch <see cref="CoveredFrom"/>/<see cref="CoveredTo"/>.</para>
/// </summary>
public class FxPairBackfillState
{
    /// <summary>Reporting currency — always "USD" today, but stored rather than assumed, mirroring
    /// <see cref="FxRate.Base"/>.</summary>
    public required string Base { get; set; }

    /// <summary>The non-USD currency, e.g. "SGD".</summary>
    public required string Quote { get; set; }

    public DateTimeOffset? LastAttemptedAt { get; set; }

    public DateTimeOffset? LastSuccessAt { get; set; }

    public bool LastRunSuccess { get; set; }

    public string? LastError { get; set; }

    /// <summary>The earliest date the provider has ever been successfully asked for this pair —
    /// the <c>from</c> a successful fetch requested. Null until the first successful attempt.</summary>
    public DateOnly? CoveredFrom { get; set; }

    /// <summary>The latest date (the requested <c>fxCap</c>, not a market's settled cap — see the
    /// class remarks) the provider has ever been successfully asked for this pair. Null until the
    /// first successful attempt.</summary>
    public DateOnly? CoveredTo { get; set; }
}
