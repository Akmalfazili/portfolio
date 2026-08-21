namespace Portfolio.Application.Services;

/// <summary>
/// Guards against two manual refreshes racing while a large Twelve Data symbol sweep is being
/// paced across several minutes by <see cref="TwelveDataCreditThrottle"/> in the background (see
/// D38 and the remarks on <c>PriceRefreshService.RefreshNowAsync</c>). Deliberately a bare
/// in-memory flag, not persisted — its only job is to stop a double-click from starting a second,
/// overlapping detached sweep before the first one's own <c>RefreshRun</c> row exists for the
/// persisted cooldown to see. Singleton by design: the flag must be shared across every scoped
/// <c>PriceRefreshService</c> instance, not reset per request.
/// </summary>
public sealed class ManualRefreshInFlightGate
{
    private int _inFlight;

    /// <summary>Attempts to claim the gate. Returns <see langword="false"/> if a detached sweep is
    /// already running.</summary>
    public bool TryEnter() => Interlocked.CompareExchange(ref _inFlight, 1, 0) == 0;

    /// <summary>Releases the gate. Must be called exactly once for every successful
    /// <see cref="TryEnter"/>, including on failure.</summary>
    public void Exit() => Interlocked.Exchange(ref _inFlight, 0);
}
