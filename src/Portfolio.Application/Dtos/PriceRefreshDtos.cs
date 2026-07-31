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
}

/// <summary>Outcome of refreshing a single provider's batch of assets within one cycle.</summary>
public sealed record SourceRefreshOutcome(
    QuoteProviderKind Source,
    /// <summary>False when the source's market was closed or its interval had not yet elapsed —
    /// distinguishes "we chose not to call the provider" from "we called it and it failed".</summary>
    bool Attempted,
    bool Success,
    int SymbolsRefreshed,
    string? Error);

/// <summary>Result of one <see cref="Services.IPriceRefreshService"/> cycle.</summary>
public sealed record PriceRefreshCycleResult(
    PriceRefreshOutcome Outcome,
    /// <summary>Only set when <see cref="Outcome"/> is <see cref="PriceRefreshOutcome.CooldownActive"/>.</summary>
    int? CooldownSecondsRemaining,
    IReadOnlyList<SourceRefreshOutcome> Sources,
    int TotalSymbolsRefreshed);

/// <summary>One asset's freshly fetched quote, broadcast to connected clients over SignalR.</summary>
public sealed record QuoteUpdateNotification(
    int AssetId,
    string Symbol,
    decimal Price,
    string Currency,
    DateTimeOffset AsOf);

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
    IReadOnlyList<SourceRefreshStatus> Sources);
