namespace Portfolio.Application.Abstractions;

/// <summary>Snapshot of today's (UTC) Twelve Data credit spend, from the persisted ledger.</summary>
public sealed record TwelveDataCreditStatus(int CreditsUsedToday, int DailyBudget, int RemainingToday);

/// <summary>
/// D38: gates every outbound Twelve Data call — one instance shared by the quote path
/// (<c>TwelveDataQuoteProvider</c>) and the historical backfill path (<c>TwelveDataQuoteProvider
/// .GetHistoryAsync</c>/<c>TwelveDataFxProvider</c>), because they draw on the exact same
/// credit-denominated per-minute limit and the same daily budget. See <c>Services
/// .TwelveDataCreditPolicy</c> for the numbers this enforces and why the limit is credits, not
/// requests.
/// </summary>
public interface ITwelveDataCreditThrottle
{
    /// <summary>
    /// Waits (via <see cref="TimeProvider"/>-based delay, never a blocking sleep) until
    /// <paramref name="credits"/> can be spent without exceeding the rolling 60-second per-minute
    /// window, then records the spend against both the in-memory window and the persisted daily
    /// ledger, and returns <see langword="true"/>.
    ///
    /// <para>Returns <see langword="false"/> immediately — without waiting or recording anything —
    /// if today's persisted daily ledger shows too little budget left to grant this request at
    /// all. Callers must treat that as a normal failure for the affected symbol(s)/call, exactly
    /// like an HTTP 429, not retry it in a loop (today's budget will not replenish until the next
    /// UTC day).</para>
    ///
    /// <para>This can legitimately take tens of seconds, or (chained across several chunks in one
    /// caller-level loop) minutes, once <paramref name="credits"/> plus what has already been
    /// spent in the last 60 seconds would exceed the per-minute limit. Never call this from a path
    /// that must respond quickly — see the remarks on <c>PriceRefreshService.RefreshNowAsync</c>
    /// for how the manual refresh endpoint avoids blocking on it.</para>
    /// </summary>
    Task<bool> TryAcquireAsync(int credits, CancellationToken cancellationToken);

    /// <summary>Today's (UTC) credit spend from the persisted ledger, for cadence derivation and
    /// for surfacing on <c>GET /api/prices/status</c>.</summary>
    Task<TwelveDataCreditStatus> GetStatusAsync(CancellationToken cancellationToken);
}
