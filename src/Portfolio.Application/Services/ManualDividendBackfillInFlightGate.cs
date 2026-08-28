namespace Portfolio.Application.Services;

/// <summary>
/// Guards against two manual <c>POST /api/dividends/backfill</c> requests racing while a run is
/// already in progress in the background. Mirrors <see cref="ManualBackfillInFlightGate"/>'s own
/// reasoning exactly (detached onto a background task so an aborted HTTP connection's own
/// <c>CancellationToken</c> can never misreport a still-running fetch as a provider failure) — kept
/// as a separate gate, rather than sharing <see cref="ManualBackfillInFlightGate"/>, because a
/// manual price backfill and a manual dividend backfill are independent operations that must be
/// able to run concurrently without tripping each other's in-flight flag.
/// </summary>
public sealed class ManualDividendBackfillInFlightGate
{
    private int _inFlight;

    public bool TryEnter() => Interlocked.CompareExchange(ref _inFlight, 1, 0) == 0;

    public void Exit() => Interlocked.Exchange(ref _inFlight, 0);
}
