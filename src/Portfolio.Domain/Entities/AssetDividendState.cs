namespace Portfolio.Domain.Entities;

/// <summary>
/// The current dividend-backfill state of one stock <see cref="Asset"/> — one row per asset,
/// unlike <see cref="SourceRefreshState"/>'s one row per provider. Yahoo's dividend endpoint has
/// no batch form and is called once per asset, so a failure for one symbol must never be lost
/// inside an aggregate "the run succeeded" flag (the D26/D33 family of mistake this project keeps
/// re-learning) — this is what lets <c>DividendService</c> tell a caller whether a stock's
/// dividend figures are a real computation, have simply never been attempted yet, or are stale
/// because the last attempt failed.
///
/// Mirrors <see cref="SourceRefreshState"/>'s field shape deliberately: <see cref="LastRunSuccess"/>
/// reflects only the most recent attempt, so a stock that fails today after months of successful
/// quarterly fetches is flagged again rather than quietly trusted forever.
/// </summary>
public class AssetDividendState
{
    /// <summary>Primary key — one row per stock asset, supplied by the app (the owning
    /// <see cref="Asset.Id"/>) rather than generated.</summary>
    public int AssetId { get; set; }

    public Asset? Asset { get; set; }

    /// <summary>When this asset's dividend history was last actually fetched. Null if never
    /// attempted — the "NotYetFetched" case a caller must not confuse with a real zero.</summary>
    public DateTimeOffset? LastAttemptedAt { get; set; }

    /// <summary>When this asset's dividend history was last fetched successfully (even if zero
    /// events came back — a real non-paying stock is a valid, successful result). Null if never
    /// successful.</summary>
    public DateTimeOffset? LastSuccessAt { get; set; }

    /// <summary>Whether the most recent attempt succeeded. False when nothing has been attempted
    /// yet — callers must check <see cref="LastAttemptedAt"/>, not just this flag, to tell
    /// "never attempted" apart from "attempted and failed".</summary>
    public bool LastRunSuccess { get; set; }

    public string? LastError { get; set; }
}
