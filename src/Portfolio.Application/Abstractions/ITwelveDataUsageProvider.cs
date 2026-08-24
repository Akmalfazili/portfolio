namespace Portfolio.Application.Abstractions;

/// <summary>
/// Twelve Data's own authoritative credit counter (<c>GET /api_usage</c>), used only to
/// periodically reconcile the persisted daily ledger (<see cref="ITwelveDataCreditThrottle"/>)
/// against real spend rather than trust the ledger's own bookkeeping forever — see D39.
///
/// <para>This call itself costs 1 credit (measured live 2026-08-21 and 2026-08-24), so it must
/// never be polled in a tight loop. Callers are responsible for bounding how often they call
/// this — see <c>TwelveDataCreditThrottle</c>'s reconciliation cadence.</para>
/// </summary>
public interface ITwelveDataUsageProvider
{
    /// <summary>
    /// Returns today's real credit usage as reported by Twelve Data, or <see langword="null"/>
    /// if the call failed for any reason (never throws, except on cancellation) — reconciliation
    /// is a best-effort correction and must never prevent a credit-gated call from proceeding.
    /// </summary>
    Task<int?> GetDailyUsageAsync(CancellationToken cancellationToken);
}
