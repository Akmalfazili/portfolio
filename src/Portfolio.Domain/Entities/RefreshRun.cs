using Portfolio.Domain.Enums;

namespace Portfolio.Domain.Entities;

/// <summary>
/// Audit record of a single price refresh cycle. Powers the "last refreshed" UI indicator and
/// lets the manual refresh endpoint enforce its cooldown.
/// </summary>
public class RefreshRun
{
    public int Id { get; set; }

    public RefreshTrigger Trigger { get; set; }

    /// <summary>Which asset class this run refreshed. Null when the run covered both.</summary>
    public AssetClass? AssetClass { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public bool Success { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>Number of symbols/coins refreshed in this run, for rate-limit accounting.</summary>
    public int SymbolsRefreshed { get; set; }
}
