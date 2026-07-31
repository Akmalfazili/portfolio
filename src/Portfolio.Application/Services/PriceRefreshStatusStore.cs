using Portfolio.Application.Dtos;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services;

/// <summary>
/// In-memory, thread-safe holder of the latest known status per provider, read by
/// <c>GET /api/prices/status</c> and by newly connected SignalR clients, written by
/// <see cref="IPriceRefreshService"/> at the end of every attempted source within a cycle. This is
/// deliberately not persisted: it is a "since I last looked" snapshot for the UI, not an audit
/// trail — the audit trail is <see cref="Domain.Entities.RefreshRun"/>, written separately. A
/// process restart resets this store, which is an accepted trade for Phase 5's scope; the
/// alternative (a per-provider column set on <c>RefreshRun</c>) was not pursued because nothing
/// outside the process needs it to survive a restart, and it would have meant a schema change for
/// state that is inherently transient.
/// </summary>
public sealed class PriceRefreshStatusStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<QuoteProviderKind, SourceRefreshStatus> _sources = [];
    private DateTimeOffset? _lastRefreshedAt;

    /// <summary>Records the outcome of attempting (or explicitly skipping) one source within a
    /// cycle, and the instant it should next be considered due.</summary>
    public void RecordOutcome(SourceRefreshOutcome outcome, DateTimeOffset at, DateTimeOffset nextDueAt)
    {
        lock (_lock)
        {
            _sources.TryGetValue(outcome.Source, out var previous);

            if (!outcome.Attempted)
            {
                // "Not attempted" (market closed, or not yet due) must never overwrite what
                // actually happened the last time the provider was called — only the next check
                // time moves.
                _sources[outcome.Source] = previous is null
                    ? new SourceRefreshStatus(outcome.Source, null, null, false, null, 0, nextDueAt)
                    : previous with { NextDueAt = nextDueAt };
                return;
            }

            var lastSuccessAt = outcome.Success ? at : previous?.LastSuccessAt;

            _sources[outcome.Source] = new SourceRefreshStatus(
                outcome.Source,
                LastAttemptedAt: at,
                LastSuccessAt: lastSuccessAt,
                LastRunSuccess: outcome.Success,
                LastError: outcome.Error,
                SymbolsRefreshed: outcome.SymbolsRefreshed,
                NextDueAt: nextDueAt);

            if (outcome.Success)
            {
                _lastRefreshedAt = at;
            }
        }
    }

    /// <summary>The instant a source is next due, or null if it has never been recorded (i.e.
    /// treated as due immediately).</summary>
    public DateTimeOffset? GetNextDueAt(QuoteProviderKind source)
    {
        lock (_lock)
        {
            return _sources.TryGetValue(source, out var status) ? status.NextDueAt : null;
        }
    }

    public PriceRefreshStatus GetSnapshot(bool nyseOpen, bool sgxOpen)
    {
        lock (_lock)
        {
            var sources = _sources.Values.OrderBy(s => s.Source).ToList();
            var nextRun = sources.Count == 0 ? null : sources.Min(s => s.NextDueAt);

            return new PriceRefreshStatus(_lastRefreshedAt, nyseOpen, sgxOpen, nextRun, sources);
        }
    }
}
