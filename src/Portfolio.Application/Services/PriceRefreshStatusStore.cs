using Microsoft.EntityFrameworkCore;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Dtos;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services;

/// <summary>
/// The latest known status per provider, read by <c>GET /api/prices/status</c> and by newly
/// connected SignalR clients, written by <see cref="IPriceRefreshService"/> for every source it
/// attempts or explicitly skips within a cycle.
///
/// Backed by the <see cref="SourceRefreshState"/> table rather than process memory. It holds one
/// row per provider and is upserted in place, so it stays at three rows however long the app runs.
/// Persisting it matters for more than the UI indicator: <see cref="GetNextDueAtAsync"/> is what
/// gates the 5/60/2-minute cadence, and an in-memory version reset on every restart, making every
/// provider due immediately and re-spending rate-limited credits that had just been spent. The
/// durable audit trail of individual cycles remains <see cref="RefreshRun"/>, which is separate
/// and append-only.
/// </summary>
public sealed class PriceRefreshStatusStore(IPortfolioDbContext db)
{
    private const int MaxErrorLength = 2000;

    /// <summary>Records the outcome of attempting (or explicitly skipping) one source within a
    /// cycle, and the instant it should next be considered due. Saves immediately: the caller may
    /// return without another save — a cycle where every source was gated writes nothing else at
    /// all — and losing the next-due time would silently disable the closed-market backoff.</summary>
    public async Task RecordOutcomeAsync(
        SourceRefreshOutcome outcome, DateTimeOffset at, DateTimeOffset nextDueAt, CancellationToken cancellationToken)
    {
        var state = await db.SourceRefreshStates
            .FirstOrDefaultAsync(s => s.Source == outcome.Source, cancellationToken);

        if (state is null)
        {
            state = new SourceRefreshState { Source = outcome.Source };
            db.AddSourceRefreshState(state);
        }

        if (!outcome.Attempted)
        {
            // "Not attempted" (market closed, or not yet due) must never overwrite what actually
            // happened the last time the provider was called — only the next check time moves.
            state.NextDueAt = nextDueAt;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        state.LastAttemptedAt = at;
        state.LastRunSuccess = outcome.Success;
        state.LastError = Truncate(outcome.Error);
        state.SymbolsRefreshed = outcome.SymbolsRefreshed;
        state.NextDueAt = nextDueAt;

        if (outcome.Success)
        {
            state.LastSuccessAt = at;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The instant a source is next due, or null if it has never been recorded (i.e.
    /// treated as due immediately).</summary>
    public async Task<DateTimeOffset?> GetNextDueAtAsync(QuoteProviderKind source, CancellationToken cancellationToken) =>
        await db.SourceRefreshStates
            .Where(s => s.Source == source)
            .Select(s => s.NextDueAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<PriceRefreshStatus> GetSnapshotAsync(
        bool nyseOpen, bool sgxOpen, CancellationToken cancellationToken)
    {
        var states = await db.SourceRefreshStates
            .OrderBy(s => s.Source)
            .ToListAsync(cancellationToken);

        var sources = states
            .Select(s => new SourceRefreshStatus(
                s.Source,
                s.LastAttemptedAt,
                s.LastSuccessAt,
                s.LastRunSuccess,
                s.LastError,
                s.SymbolsRefreshed,
                s.NextDueAt))
            .ToList();

        // "Last refreshed" is the most recent success across all providers, derived rather than
        // stored so it can never drift out of step with the per-source rows it summarises.
        var lastRefreshedAt = states.Count == 0 ? null : states.Max(s => s.LastSuccessAt);
        var nextRun = states.Count == 0 ? null : states.Min(s => s.NextDueAt);

        return new PriceRefreshStatus(lastRefreshedAt, nyseOpen, sgxOpen, nextRun, sources);
    }

    /// <summary>Provider errors are free text and can be long; the column stops at 2000, so trim
    /// here rather than letting a verbose exception message fail the whole save.</summary>
    private static string? Truncate(string? error) =>
        error is { Length: > MaxErrorLength } ? error[..MaxErrorLength] : error;
}
