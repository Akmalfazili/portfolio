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
    int TotalSymbolsRefreshed,
    /// <summary>
    /// Refresh-catch-up feature: what <c>POST /api/prices/refresh</c>'s planner (<c>IRefreshCatchUpService</c>)
    /// found missing for price history/dividends and, when something was missing, whether this
    /// click queued it. Null on every SCHEDULED cycle (<c>PriceRefreshBackgroundService</c> never
    /// runs the catch-up planner — this is a manual-click-only feature) and whenever the manual
    /// refresh itself did not run (<see cref="PriceRefreshOutcome.CooldownActive"/>, from either
    /// branch that can produce it). Populated by <c>PricesEndpoints</c>, not by
    /// <c>PriceRefreshService</c> itself — the planner and the two catch-up backfills are DB/provider
    /// concerns one layer removed from live-quote refreshing, kept out of this type's own producer
    /// so a unit test against <c>PriceRefreshService</c> alone never has to stand up the catch-up
    /// machinery just to construct this record.
    /// </summary>
    RefreshCatchUpPlan? CatchUp = null);

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

/// <summary>
/// Per-market status of the daily-close backfill (<c>PriceBackfillService</c>, <c>PriceHistory</c>)
/// — the counterpart to <see cref="SourceRefreshStatus"/>, which only ever reports the live-quote
/// path (<c>PriceQuote</c>). Added so the refresh panel can stop reading "last updated" purely off
/// the live-quote source: outside market hours no live quote is even attempted, so
/// <see cref="SourceRefreshStatus"/> alone can report a stale-looking failure from days ago while a
/// same-day close backfill succeeded minutes earlier — see the tracker entry this shipped with.
///
/// <para>One entry per <see cref="Market"/> in <c>Services.Calendar.ProviderMarkets.All</c>, always
/// both, even when a market has never run at all — a market that has never been attempted reports
/// every field <c>null</c>, never <c>false</c>/zero, so "not attempted" can never be misread as
/// "attempted and failed" (the project's most repeated defect family: D10/D26/D33/D35/D38/D45).</para>
/// </summary>
public sealed record MarketCloseStatus(
    Market Market,
    /// <summary>
    /// The newest date through which <b>every</b> active <see cref="AssetClass.Stock"/> asset of
    /// this market that has at least one transaction has a <c>PriceHistory</c> row — i.e. the MIN
    /// over those assets of each asset's own MAX(Date). Deliberately the min, not the max: the max
    /// would read as "closes are current through Friday" when only one asset actually got Friday's
    /// close and another is still lagging, which is exactly the kind of healthy-looking-but-wrong
    /// report this project keeps tripping over.
    ///
    /// <para>Null both when the market has no such assets at all, and when at least one of them has
    /// never received a single <c>PriceHistory</c> row — both are "no honest floor exists yet" in
    /// the same way, and distinguishing them would need a second field nobody asked for.</para>
    /// </summary>
    DateOnly? LatestCloseDate,
    /// <summary>
    /// <c>CompletedAt</c> of the most recent completed <see cref="Domain.Entities.RefreshRun"/> with
    /// this <see cref="Market"/> and a <c>Trigger</c> of <c>BackfillScheduled</c>/<c>BackfillManual</c>
    /// — pre-D47 runs have <c>Market == null</c> and are invisible here by design, same as the
    /// due-ness gate they predate. Null means this market's close backfill has never completed, not
    /// zero and not a failure.
    /// </summary>
    DateTimeOffset? LastAttemptedAt,
    /// <summary><c>CompletedAt</c> of the most recent such run with <c>Success == true</c>, or null
    /// if none has ever succeeded. Stays at the earlier success when a later run failed — a failed
    /// run must never blank out the last time it genuinely worked.</summary>
    DateTimeOffset? LastSuccessAt,
    /// <summary><c>Success</c> of the most recent completed run (see <see cref="LastAttemptedAt"/>).
    /// Null only when there has never been one — never <c>false</c> for "not attempted".</summary>
    bool? LastRunSuccess,
    /// <summary>That same most recent run's <c>ErrorMessage</c>. The frontend truncates for
    /// display.</summary>
    string? LastError);

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
    /// portfolio size.
    ///
    /// <para>Populated identically, via <see cref="Services.PriceRefreshStatusEnricher"/>, by every
    /// producer of this DTO: <c>GET /api/prices/status</c>, the snapshot a newly connected SignalR
    /// client receives, and every SignalR <c>RefreshStatus</c> broadcast. (Previously the enrichment
    /// lived only inline in the status endpoint's handler, so this field and the two below it were
    /// always null over SignalR and only ever populated over HTTP — a duplicated-code-path bug fixed
    /// alongside this doc comment; see the tracker entry.) Null has no remaining meaning today: the
    /// underlying computation always resolves to a concrete number — zero credits spent and zero
    /// active symbols are valid inputs that still derive a real interval, not an "unknown" state.
    /// It stays a nullable <c>int</c> only so a snapshot serialized before these fields existed still
    /// deserializes.</para></summary>
    int? EffectiveTwelveDataIntervalSeconds = null,
    /// <summary>Twelve Data credits spent today (UTC), from the persisted daily ledger — see
    /// <see cref="Abstractions.ITwelveDataCreditThrottle"/>. Same "populated identically everywhere,
    /// never legitimately null after enrichment" rule as <see cref="EffectiveTwelveDataIntervalSeconds"/>
    /// — see that field's remarks.</summary>
    int? CreditsUsedToday = null,
    /// <summary>Twelve Data's daily credit budget (800 on the free tier). Same "populated identically
    /// everywhere, never legitimately null after enrichment" rule as
    /// <see cref="EffectiveTwelveDataIntervalSeconds"/>.</summary>
    int? CreditBudget = null,
    /// <summary>Per-market daily-close backfill status — see <see cref="MarketCloseStatus"/>. Follows
    /// the same "populated identically everywhere, via <see cref="Services.PriceRefreshStatusEnricher"/>"
    /// rule as the three Twelve Data fields above, and the same trailing-nullable-parameter shape
    /// purely for back-compat deserialization of a snapshot serialized before this field existed —
    /// once enriched it always holds one entry per <c>Services.Calendar.ProviderMarkets.All</c>,
    /// never null and never an empty list.</summary>
    IReadOnlyList<MarketCloseStatus>? Closes = null);
