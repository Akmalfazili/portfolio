using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Portfolio.Application.Abstractions;
using Portfolio.Domain.Entities;

namespace Portfolio.Application.Services;

/// <summary>
/// See <see cref="ITwelveDataCreditThrottle"/>. A process-wide singleton: the in-memory rolling
/// 60-second window it paces against would be meaningless per-scope (a new one on every request
/// would remember nothing between calls), so <see cref="_gate"/>, <see cref="_window"/> and
/// <see cref="_lastReconciledAt"/> are shared, and <see cref="IServiceScopeFactory"/> is used to
/// reach the scoped <see cref="IPortfolioDbContext"/> (and <see cref="ITwelveDataUsageProvider"/>)
/// from inside a singleton.
///
/// <see cref="_gate"/> serializes every acquire end to end (pacing wait, ledger read, ledger
/// write) so two callers — the quote-refresh loop and the backfill service, say — can never
/// interleave and jointly overspend the per-minute window or race the same day's ledger row.
///
/// <para><b>D39:</b> the ledger this throttle persists is only ever built from credits *this
/// process* granted through <see cref="TryAcquireAsync"/>. Measured 2026-08-21: it read
/// <c>creditsUsedToday: 123</c> while Twelve Data's own <c>GET /api_usage</c> read
/// <c>daily_usage: 323</c> at the same instant — partly because the ledger table was created
/// mid-day and started at zero, and partly from spend this process never recorded (e.g. calls
/// made outside the throttle, or a resilience retry that hit the real API a second time without a
/// second <see cref="TryAcquireAsync"/> call to record it). A ledger that under-counts will
/// authorise calls the real budget cannot afford, and a rejected call is still billed in full
/// (D38). Fixed two ways, both driven by <see cref="ITwelveDataUsageProvider"/> — resolved
/// per-scope, not injected directly, exactly like <see cref="IPortfolioDbContext"/>, and looked up
/// with <c>GetService</c> rather than <c>GetRequiredService</c> so a caller that hasn't wired one
/// up (existing unit tests) degrades to the pre-D39 zero-seeded behaviour instead of throwing:
/// <list type="bullet">
/// <item>On the first write of a new UTC day, the ledger is seeded from Twelve Data's real
/// counter instead of zero.</item>
/// <item>Periodically (<see cref="TwelveDataCreditOptions.ReconciliationIntervalMinutes"/>, default
/// 60 — <b>never per call</b>, since <c>GET /api_usage</c> itself costs 1 credit, measured
/// 2026-08-21 and 2026-08-24), the ledger is overwritten from the real counter again rather than
/// trusting its own running total indefinitely.</item>
/// </list>
/// Both are best-effort: if the usage call fails or isn't wired up, the ledger is left exactly as
/// it was under the pre-D39 behaviour — reconciliation must never block or fail a credit-gated
/// call.</para>
/// </summary>
public sealed class TwelveDataCreditThrottle(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<TwelveDataCreditOptions> options,
    ILogger<TwelveDataCreditThrottle> logger) : ITwelveDataCreditThrottle
{
    private static readonly TimeSpan WindowLength = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<(DateTimeOffset At, int Credits)> _window = [];

    /// <summary>Process-lifetime only, like <see cref="_window"/> — a restart re-reconciling
    /// sooner than the interval is harmless; the persisted <see cref="TwelveDataCreditLedgerEntry.CreditsUsed"/>
    /// is the thing that must survive a restart, not this cadence bookkeeping.</summary>
    private DateTimeOffset? _lastReconciledAt;

    public async Task<bool> TryAcquireAsync(int credits, CancellationToken cancellationToken)
    {
        if (credits <= 0)
        {
            return true;
        }

        if (credits > options.Value.PerMinuteCreditLimit)
        {
            // A single request for more credits than the per-minute limit allows can NEVER be
            // granted, no matter how long it waits — this is exactly the shape of the pre-D38-fix
            // bug (one /quote call carrying all 21 symbols at once). Callers must chunk to at most
            // the per-minute limit per call; this is a caller contract violation, not a transient
            // condition to pace through.
            throw new ArgumentOutOfRangeException(
                nameof(credits),
                credits,
                $"A single Twelve Data request must never ask for more than the per-minute credit limit ({options.Value.PerMinuteCreditLimit}); chunk the request instead.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IPortfolioDbContext>();

            var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
            var entry = await db.TwelveDataCreditLedgerEntries.FirstOrDefaultAsync(e => e.Date == today, cancellationToken);
            entry = await SeedOrReconcileAsync(scope, db, entry, today, cancellationToken);

            var usedToday = entry.CreditsUsed;
            var dailyBudget = options.Value.DailyCreditBudget;

            if (usedToday + credits > dailyBudget)
            {
                logger.LogWarning(
                    "Twelve Data daily credit budget would be exceeded: {UsedToday} used, {Requested} requested, {Budget} budget",
                    usedToday,
                    credits,
                    dailyBudget);
                return false;
            }

            await WaitForPerMinuteRoomAsync(credits, cancellationToken);

            _window.Add((timeProvider.GetUtcNow(), credits));

            entry.CreditsUsed += credits;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>D39. Must be called while holding <see cref="_gate"/> — mutates
    /// <see cref="_lastReconciledAt"/> and the ledger row without any lock of its own.</summary>
    private async Task<TwelveDataCreditLedgerEntry> SeedOrReconcileAsync(
        AsyncServiceScope scope,
        IPortfolioDbContext db,
        TwelveDataCreditLedgerEntry? entry,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        if (entry is null)
        {
            // First write of a new UTC day: seed from Twelve Data's real counter instead of zero.
            // A day that starts mid-way through already-spent credits (or simply drifted overnight)
            // must not be treated as a fresh 800-credit allowance.
            var real = await TryFetchRealUsageAsync(scope, cancellationToken);
            entry = new TwelveDataCreditLedgerEntry { Date = today, CreditsUsed = real ?? 0 };
            db.AddTwelveDataCreditLedgerEntry(entry);
            await db.SaveChangesAsync(cancellationToken);

            if (real is { } seeded)
            {
                _lastReconciledAt = now;
                logger.LogInformation(
                    "Seeded Twelve Data credit ledger for {Date} from real usage: {Credits}",
                    today,
                    seeded);
            }
            else
            {
                logger.LogWarning(
                    "Could not seed Twelve Data credit ledger for {Date} from real usage; starting from zero",
                    today);
            }

            return entry;
        }

        var reconciliationInterval = TimeSpan.FromMinutes(options.Value.ReconciliationIntervalMinutes);
        var dueForReconciliation = _lastReconciledAt is null || now - _lastReconciledAt.Value >= reconciliationInterval;

        if (dueForReconciliation)
        {
            var real = await TryFetchRealUsageAsync(scope, cancellationToken);
            if (real is { } reconciled)
            {
                logger.LogInformation(
                    "Reconciled Twelve Data credit ledger for {Date}: local {Local} -> real {Real}",
                    today,
                    entry.CreditsUsed,
                    reconciled);
                entry.CreditsUsed = reconciled;
                await db.SaveChangesAsync(cancellationToken);
                _lastReconciledAt = now;
            }
            // else: real usage unavailable this time — leave the ledger as-is and try again once
            // the interval next elapses, rather than blocking or failing this credit request.
        }

        return entry;
    }

    /// <summary>Never throws except on cancellation — a failed or missing reconciliation source
    /// must degrade to the pre-D39 behaviour, not break credit gating.</summary>
    private static async Task<int?> TryFetchRealUsageAsync(AsyncServiceScope scope, CancellationToken cancellationToken)
    {
        var usageProvider = scope.ServiceProvider.GetService<ITwelveDataUsageProvider>();
        return usageProvider is null ? null : await usageProvider.GetDailyUsageAsync(cancellationToken);
    }

    public async Task<TwelveDataCreditStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IPortfolioDbContext>();

        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var usedToday = await db.TwelveDataCreditLedgerEntries
            .Where(e => e.Date == today)
            .Select(e => (int?)e.CreditsUsed)
            .FirstOrDefaultAsync(cancellationToken) ?? 0;

        var budget = options.Value.DailyCreditBudget;
        return new TwelveDataCreditStatus(usedToday, budget, Math.Max(0, budget - usedToday));
    }

    /// <summary>Blocks (never a real <c>Thread.Sleep</c> — always <c>Task.Delay</c> through the
    /// injected <see cref="TimeProvider"/>, so tests can control it) until the rolling window has
    /// room for <paramref name="credits"/> more. Must be called only while holding
    /// <see cref="_gate"/>.</summary>
    private async Task WaitForPerMinuteRoomAsync(int credits, CancellationToken cancellationToken)
    {
        var perMinuteLimit = options.Value.PerMinuteCreditLimit;

        while (true)
        {
            var now = timeProvider.GetUtcNow();
            var windowStart = now - WindowLength;
            _window.RemoveAll(e => e.At < windowStart);

            var usedThisWindow = _window.Sum(e => e.Credits);
            if (usedThisWindow + credits <= perMinuteLimit)
            {
                return;
            }

            var oldestAt = _window.Min(e => e.At);
            var wait = oldestAt + WindowLength - now;
            if (wait <= TimeSpan.Zero)
            {
                // Nothing to wait for (the entry aged out between the sum and this check) — loop
                // straight back around and re-evaluate rather than delaying for a non-positive span.
                continue;
            }

            logger.LogInformation(
                "Twelve Data per-minute credit window full ({UsedThisWindow}/{PerMinuteLimit}); pacing {Requested} credit(s) for {WaitSeconds:F0}s",
                usedThisWindow,
                perMinuteLimit,
                credits,
                wait.TotalSeconds);

            await Task.Delay(wait, timeProvider, cancellationToken);
        }
    }
}
