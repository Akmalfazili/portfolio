namespace Portfolio.Application.Services;

/// <summary>
/// Guards against two manual <c>POST /api/prices/backfill</c> requests racing while a paced
/// multi-asset backfill runs in the background. Found live: with D37/D38's shared credit throttle
/// now pacing every Twelve Data call, a full backfill pass (22 calls at 8 credits/minute) takes
/// over two minutes — long enough that nginx's default proxy read timeout (60s) 504s the request
/// while the API keeps running it, and the ASP.NET request's own <c>CancellationToken</c> being
/// tied to that aborted connection then cancels every remaining provider call mid-run, each one
/// misreported as a genuine provider failure rather than what it actually is. The endpoint detaches
/// the work exactly like the manual quote refresh does (see <see cref="ManualRefreshInFlightGate"/>
/// and the remarks on <c>PriceRefreshService.RefreshNowAsync</c>) so it is never bound to a
/// request's lifetime; this gate stops a second click from starting an overlapping run before the
/// first one's own audit row exists.
/// </summary>
public sealed class ManualBackfillInFlightGate
{
    private int _inFlight;

    public bool TryEnter() => Interlocked.CompareExchange(ref _inFlight, 1, 0) == 0;

    public void Exit() => Interlocked.Exchange(ref _inFlight, 0);
}
