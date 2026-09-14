using Portfolio.Domain.Enums;

namespace Portfolio.Application.Dtos;

/// <summary>How one leg (price history or dividends) of a manual refresh's catch-up resolved this
/// click. See tracker.md's refresh-catch-up entry and <c>Services.IRefreshCatchUpService</c>.</summary>
public enum CatchUpState
{
    /// <summary>Nothing was missing — every in-scope asset (and FX pair, for the price-history leg)
    /// is already covered by stored data or a prior successful attempt on file (see
    /// <c>AssetPriceHistoryState</c>/<c>AssetDividendState.CoveredFrom</c>). No in-flight gate was
    /// taken, no background task started, no <c>Domain.Entities.RefreshRun</c> written.</summary>
    NothingToFetch,

    /// <summary>Something was missing and this click's leg claimed the in-flight gate — detached
    /// onto a background task exactly like <c>POST /api/prices/backfill</c>/<c>POST
    /// /api/dividends/backfill</c>. Poll <c>GET /api/prices/status</c> or listen for the SignalR
    /// <c>CatchUpCompleted</c> event for the outcome.</summary>
    Queued,

    /// <summary>Something was missing, but the leg's in-flight gate was already held — by a
    /// concurrently running manual full backfill of the same kind, or an earlier catch-up click
    /// still in flight. Nothing new was started this click; <see cref="CatchUpLeg.FetchSymbols"/>
    /// still lists what WOULD have been queued.</summary>
    AlreadyRunning,
}

/// <summary>Which backfill a <see cref="CatchUpLeg"/> or <see cref="CatchUpCompletedNotification"/>
/// covers.</summary>
public enum CatchUpKind
{
    PriceHistory,
    Dividends,
}

/// <summary>One leg of <see cref="RefreshCatchUpPlan"/> — see <c>Services.IRefreshCatchUpService</c>
/// for exactly how each list is decided.</summary>
public sealed record CatchUpLeg(
    CatchUpState State,
    /// <summary>Missing and eligible: queued this click (<see cref="CatchUpState.Queued"/>), or
    /// would have been had the gate not already been held (<see cref="CatchUpState.AlreadyRunning"/>).
    /// Empty for <see cref="CatchUpState.NothingToFetch"/>.</summary>
    IReadOnlyList<string> FetchSymbols,
    /// <summary>e.g. <c>"USD/SGD"</c>. Price-history leg only — always empty for dividends, which
    /// has no FX leg of its own.</summary>
    IReadOnlyList<string> FxPairs,
    /// <summary>Missing, but the last attempt failed too recently to retry — see
    /// <c>PriceBackfillOptions.FailedRunRetryDelay</c> / <c>DividendBackfillOptions.FailedAssetRetryInterval</c>.
    /// Not attempted this click, regardless of <see cref="State"/>.</summary>
    IReadOnlyList<string> RetryPendingSymbols,
    /// <summary>Price history only: the asset's first trade date is after the latest settled close
    /// (<c>PriceBackfillOptions.CloseSettleDelay</c>), so no close exists yet to fetch — not
    /// attempted, and not "missing" in any actionable sense. Always empty for dividends.</summary>
    IReadOnlyList<string> NotYetAvailableSymbols,
    /// <summary>
    /// The id of this leg's detached run — minted fresh (<c>Guid.NewGuid()</c>) when the leg is
    /// actually started, non-null ONLY when <see cref="State"/> is <see cref="CatchUpState.Queued"/>
    /// (null for <see cref="CatchUpState.NothingToFetch"/>/<see cref="CatchUpState.AlreadyRunning"/>,
    /// neither of which started anything). Echoed back on <see cref="CatchUpCompletedNotification.RunId"/>
    /// so the frontend can match a completion push to the click that queued it without comparing
    /// clocks — the detached task starts before this response is even sent, so a fast leg (a single
    /// Yahoo dividend call, ~300ms) can push <c>CatchUpCompleted</c> before the browser has finished
    /// processing the HTTP response that reported <see cref="CatchUpState.Queued"/>.
    /// </summary>
    Guid? RunId = null);

/// <summary>What one manual refresh's catch-up decided for each leg. Carried on
/// <see cref="PriceRefreshCycleResult.CatchUp"/> — null on every scheduled cycle and whenever the
/// manual refresh itself did not run (<see cref="PriceRefreshOutcome.CooldownActive"/>, which is a
/// 429 <c>ProblemDetails</c> anyway, from either branch that outcome can come from).</summary>
public sealed record RefreshCatchUpPlan(CatchUpLeg PriceHistory, CatchUpLeg Dividends);

/// <summary>One symbol — or, for the price-history leg, an FX pair spelled <c>"FX:USD/SGD"</c>,
/// matching <see cref="AssetBackfillFailure"/>'s own convention — that a catch-up leg attempted and
/// did not succeed.</summary>
public sealed record CatchUpFailure(string Symbol, string Error);

/// <summary>Broadcast over SignalR (event name <c>"CatchUpCompleted"</c>) when a detached catch-up
/// leg finishes, success or failure, so the refresh panel never waits forever on a leg that died.
/// See <c>Abstractions.IPriceUpdateBroadcaster.BroadcastCatchUpCompletedAsync</c>.</summary>
public sealed record CatchUpCompletedNotification(
    CatchUpKind Kind,
    IReadOnlyList<string> SucceededSymbols,
    IReadOnlyList<CatchUpFailure> Failed,
    /// <summary>Price history only — a catch-up's asset set is small by construction (only what the
    /// planner found missing), so this is never expected to bite in practice, but it is wired
    /// through rather than assumed always empty. Always empty for dividends unless a portfolio
    /// somehow exceeds <c>DividendBackfillOptions.MaxAssetsPerRun</c> (default 500) in one catch-up.</summary>
    IReadOnlyList<string> SkippedForBudgetSymbols,
    /// <summary><c>PriceHistory</c>+<c>FxRates</c> rows inserted for the price-history leg;
    /// <c>DividendEvents</c> rows inserted for the dividends leg.</summary>
    int RowsInserted,
    DateTimeOffset CompletedAt,
    /// <summary>The same id <see cref="CatchUpLeg.RunId"/> reported when this run was queued —
    /// echoed on every completion, including the "the run itself threw" path, so the frontend never
    /// has to fall back to comparing clocks to tell which click a push belongs to.</summary>
    Guid RunId);
