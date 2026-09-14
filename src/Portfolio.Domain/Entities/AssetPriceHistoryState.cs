namespace Portfolio.Domain.Entities;

/// <summary>
/// The current daily-close backfill coverage of one stock <see cref="Asset"/> — a per-asset mirror
/// of <see cref="AssetDividendState"/>, added for the refresh-catch-up feature. <see cref="PriceHistory"/>
/// itself cannot answer "is this asset's history missing?" on its own: several assets have a
/// PERMANENT gap between their first trade and their first stored close (a weekend/holiday trade
/// date, or a SPAC listed after the trade date — see tracker.md's refresh-catch-up entry for the
/// measured examples), and a rule that only ever looks at stored rows would re-fetch those assets
/// on every single catch-up click, forever. This table records what the provider has already been
/// SUCCESSFULLY ASKED for, independent of what came back, so a permanent gap reads as "covered by
/// attempt" rather than "still missing" — the same "not attempted vs attempted" split
/// <see cref="AssetDividendState"/> already established for dividends, applied here to coverage
/// range rather than a single pass/fail flag.
///
/// <para>Written by <c>PriceBackfillService.RunAsyncCore</c> for EVERY asset fetch attempt,
/// whatever the trigger (scheduled, D51 retry, manual, or catch-up) — not only catch-up runs. A
/// failed attempt updates <see cref="LastAttemptedAt"/>/<see cref="LastRunSuccess"/>/<see cref="LastError"/>
/// but must never touch <see cref="CoveredFrom"/>/<see cref="CoveredTo"/>: a failure must never
/// shrink what is already known to be covered.</para>
/// </summary>
public class AssetPriceHistoryState
{
    /// <summary>Primary key — one row per stock asset, supplied by the app (the owning
    /// <see cref="Asset.Id"/>) rather than generated, same upsert-on-a-known-key shape as
    /// <see cref="AssetDividendState"/>.</summary>
    public int AssetId { get; set; }

    public Asset? Asset { get; set; }

    /// <summary>When this asset's price history was last actually attempted. Null if never
    /// attempted.</summary>
    public DateTimeOffset? LastAttemptedAt { get; set; }

    /// <summary>When this asset's price history was last fetched successfully. Null if never
    /// successful.</summary>
    public DateTimeOffset? LastSuccessAt { get; set; }

    /// <summary>Whether the most recent attempt succeeded. False when nothing has ever been
    /// attempted — callers must check <see cref="LastAttemptedAt"/>, not just this flag, to tell
    /// "never attempted" apart from "attempted and failed".</summary>
    public bool LastRunSuccess { get; set; }

    public string? LastError { get; set; }

    /// <summary>
    /// The earliest date the provider has ever been successfully asked for, for this asset —
    /// i.e. the <c>from</c> a successful <c>GetHistoryAsync</c> call requested, recorded even when
    /// the result came back <c>Truncated</c> (truncation is provider policy; re-asking for the same
    /// <c>from</c> will not return more — see <c>PriceBackfillService</c>'s D53 remarks). Null until
    /// the first successful attempt.
    /// </summary>
    public DateOnly? CoveredFrom { get; set; }

    /// <summary>The latest date (a market's own settled cap — see <c>PriceBackfillOptions.CloseSettleDelay</c>)
    /// the provider has ever been successfully asked for, for this asset. Null until the first
    /// successful attempt.</summary>
    public DateOnly? CoveredTo { get; set; }
}
