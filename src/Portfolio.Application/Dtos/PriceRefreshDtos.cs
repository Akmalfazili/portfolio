using Portfolio.Domain.Enums;

namespace Portfolio.Application.Dtos;

/// <summary>How one call to <see cref="Services.IPriceRefreshService"/> concluded.</summary>
public enum PriceRefreshOutcome
{
    /// <summary>At least one provider group was attempted (fetched or explicitly skipped
    /// because its market is closed) and the cycle's <see cref="Domain.Entities.RefreshRun"/>
    /// was recorded.</summary>
    Completed,

    /// <summary>Nothing was due yet — every source's next scheduled check is still in the future,
    /// or every one of them was gated by a closed market. Not an error. A scheduled cycle writes no
    /// <see cref="Domain.Entities.RefreshRun"/> in this case; a <em>manual</em> one does, so that
    /// <see cref="Services.PriceRefreshOptions.ManualCooldown"/> still has an attempt to measure
    /// from and the endpoint cannot be hammered while everything is gated.</summary>
    NothingDue,

    /// <summary>A manual refresh was requested within <see cref="Services.PriceRefreshOptions.ManualCooldown"/>
    /// of the previous manual refresh. Nothing was fetched.</summary>
    CooldownActive,

    /// <summary>D38: the manual trigger's Twelve Data group would need more than one of the
    /// shared credit throttle's per-minute chunks (see <c>Abstractions.ITwelveDataCreditThrottle</c>),
    /// so the actual fetch was detached onto a background task instead of running inline — this
    /// call returned immediately without waiting for it. Poll <c>GET /api/prices/status</c> for
    /// progress; the detached sweep writes its own <see cref="Domain.Entities.RefreshRun"/> and
    /// broadcasts <c>RefreshStatus</c> over SignalR exactly as a normal <see cref="Completed"/>
    /// cycle would, once it finishes.</summary>
    Queued,
}

/// <summary>
/// Outcome of refreshing a single provider's batch of assets within one cycle.
///
/// D10, corrected 2026-08-07: only ever appears in <see cref="PriceRefreshCycleResult.Sources"/>
/// for a source that was actually fetched — <c>RunCycleAsync</c> records a gated (closed-market)
/// or not-yet-due source straight to the durable <c>SourceRefreshState</c> table and
/// <c>continue</c>s without adding it here, so <see cref="Attempted"/> is always <c>true</c> for
/// every entry this DTO's list ever actually contains; <c>false</c> is reachable only in the
/// persisted status store, not in this per-cycle result. An earlier version of this comment said
/// <see cref="Attempted"/> would be <c>false</c> "when the source's market was closed", which
/// described a shape the API has never actually sent. Do not rely on this list to detect a closed
/// market — <c>GET /api/prices/status</c> exposes <see cref="PriceRefreshStatus.NyseOpen"/> /
/// <see cref="PriceRefreshStatus.SgxOpen"/> for exactly that, and the frontend deliberately reads
/// those instead (see the D10 entry in tracker.md's Decisions/drawback register).
/// </summary>
public sealed record SourceRefreshOutcome(
    QuoteProviderKind Source,
    /// <summary>Always <c>true</c> in practice today — see the type-level remark above.</summary>
    bool Attempted,
    bool Success,
    int SymbolsRefreshed,
    string? Error);

/// <summary>Result of one <see cref="Services.IPriceRefreshService"/> cycle. <see cref="Sources"/>
/// lists only the providers actually fetched this cycle — a provider skipped because its market
/// was closed, or because its interval had not yet elapsed, is never in this list at all (see the
/// D10 remark on <see cref="SourceRefreshOutcome"/>).</summary>
public sealed record PriceRefreshCycleResult(
    PriceRefreshOutcome Outcome,
    /// <summary>Only set when <see cref="Outcome"/> is <see cref="PriceRefreshOutcome.CooldownActive"/>.</summary>
    int? CooldownSecondsRemaining,
    IReadOnlyList<SourceRefreshOutcome> Sources,
    int TotalSymbolsRefreshed);

/// <summary>
/// One asset's freshly fetched quote, broadcast to connected clients over SignalR.
///
/// <para><b>"Freshly fetched" is not the same as "fresh" (D4).</b> The refresh service polls
/// whenever its calendar believes a market is open, and on an unmodelled SGX lunar holiday that
/// belief is wrong: Yahoo answers with the previous session's close and this notification carries
/// it. The client cannot tell from <see cref="Price"/> and <see cref="AsOf"/> alone without
/// reimplementing exchange-session arithmetic in the browser, so <see cref="Source"/> carries the
/// backend's verdict — the same one <c>PortfolioSummaryService</c> puts on <c>HoldingDto</c>, from
/// the same <c>QuoteFreshness</c> rule.</para>
/// </summary>
public sealed record QuoteUpdateNotification(
    int AssetId,
    string Symbol,
    decimal Price,
    string Currency,
    DateTimeOffset AsOf,
    PriceSource Source);

/// <summary>Point-in-time status of one provider, for the "last refreshed" UI indicator.</summary>
public sealed record SourceRefreshStatus(
    QuoteProviderKind Source,
    DateTimeOffset? LastAttemptedAt,
    DateTimeOffset? LastSuccessAt,
    bool LastRunSuccess,
    string? LastError,
    int SymbolsRefreshed,
    DateTimeOffset? NextDueAt);

/// <summary>Snapshot returned by <c>GET /api/prices/status</c> and pushed to newly connected
/// SignalR clients. Held in memory only (see <see cref="Services.PriceRefreshStatusStore"/>) — it
/// resets on app restart, which is acceptable for a "since I last looked" UI indicator; the
/// durable audit trail is <see cref="Domain.Entities.RefreshRun"/>.</summary>
public sealed record PriceRefreshStatus(
    DateTimeOffset? LastRefreshedAt,
    bool NyseOpen,
    bool SgxOpen,
    DateTimeOffset? NextScheduledRunAt,
    IReadOnlyList<SourceRefreshStatus> Sources,
    /// <summary>D38/D37: the Twelve Data quote-sweep interval currently in effect, derived from
    /// the live active-symbol count and today's remaining credit budget (see
    /// <see cref="Services.TwelveDataCadenceCalculator"/>) rather than a cadence hardcoded for one
    /// portfolio size. Null only if this field has not been populated by the endpoint (never the
    /// case for <c>GET /api/prices/status</c> itself — additive field, defaults to null so older
    /// serialized snapshots still deserialize).</summary>
    int? EffectiveTwelveDataIntervalSeconds = null,
    /// <summary>Twelve Data credits spent today (UTC), from the persisted daily ledger — see
    /// <see cref="Abstractions.ITwelveDataCreditThrottle"/>.</summary>
    int? CreditsUsedToday = null,
    /// <summary>Twelve Data's daily credit budget (800 on the free tier).</summary>
    int? CreditBudget = null);
