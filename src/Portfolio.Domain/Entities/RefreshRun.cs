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

    /// <summary>
    /// Which exchange this row's due-ness gate applies to (D47). <c>PriceBackfillService.RunAsync</c>
    /// writes one <see cref="RefreshRun"/> row PER MARKET it covers — never one row for a run that
    /// spans both — so <c>RunIfDueAsync</c>'s per-market due-ness query
    /// (<c>Trigger == BackfillScheduled &amp;&amp; Market == market</c>) has something to read for
    /// each exchange independently. NYSE being open must never gate whether SGX's close backfill
    /// ran, and vice versa; a single shared latch was exactly D47's bug.
    ///
    /// <para><b>NULL means legacy, not "unknown" or "both."</b> Every row written before this
    /// column existed — every <see cref="RefreshTrigger.Scheduled"/>/<see cref="RefreshTrigger.Manual"/>
    /// quote-refresh row (never market-scoped to begin with) and every pre-D47
    /// <see cref="RefreshTrigger.BackfillScheduled"/>/<see cref="RefreshTrigger.BackfillManual"/> row
    /// — is NULL. A NULL row never matches <c>Market == market</c> in the per-market query, so it is
    /// silently invisible to the new gate rather than corrupting it. The practical effect: each
    /// market simply runs once, for free, on the first due-ness check after this ships — no audit
    /// backfill needed, and nothing to migrate.</para>
    /// </summary>
    public Market? Market { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public bool Success { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>Number of symbols/coins refreshed in this run, for rate-limit accounting.</summary>
    public int SymbolsRefreshed { get; set; }
}
