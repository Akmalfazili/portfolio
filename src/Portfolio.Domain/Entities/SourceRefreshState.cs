using Portfolio.Domain.Enums;

namespace Portfolio.Domain.Entities;

/// <summary>
/// The current refresh state of one quote provider — when it was last called, whether that call
/// worked, and when it is next due. Exactly one row per <see cref="QuoteProviderKind"/>, upserted
/// in place, so this table never grows beyond the number of providers.
///
/// This is deliberately *not* an audit trail; <see cref="RefreshRun"/> is. It exists so the
/// cadence survives a process restart: without it, every provider's "next due" time is unknown on
/// startup and therefore treated as due immediately, so restarting the API re-hits every provider
/// and spends rate-limited credits that were already spent minutes earlier.
/// </summary>
public class SourceRefreshState
{
    /// <summary>Primary key — one row per provider, never generated.</summary>
    public QuoteProviderKind Source { get; set; }

    /// <summary>When the provider was last actually called. Null if it never has been.</summary>
    public DateTimeOffset? LastAttemptedAt { get; set; }

    /// <summary>When the provider last returned usable quotes. Null if it never has.</summary>
    public DateTimeOffset? LastSuccessAt { get; set; }

    /// <summary>Whether the most recent attempt succeeded. False when nothing has been attempted.</summary>
    public bool LastRunSuccess { get; set; }

    public string? LastError { get; set; }

    /// <summary>Symbols refreshed by the most recent attempt, for rate-limit accounting.</summary>
    public int SymbolsRefreshed { get; set; }

    /// <summary>When this provider should next be considered due. Null means "due now".</summary>
    public DateTimeOffset? NextDueAt { get; set; }
}
