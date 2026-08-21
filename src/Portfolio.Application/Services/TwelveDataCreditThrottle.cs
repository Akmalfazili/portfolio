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
/// would remember nothing between calls), so <see cref="_gate"/> and <see cref="_window"/> are
/// shared, and <see cref="IServiceScopeFactory"/> is used to reach the scoped
/// <see cref="IPortfolioDbContext"/> for the persisted daily ledger from inside a singleton.
///
/// <see cref="_gate"/> serializes every acquire end to end (pacing wait, ledger read, ledger
/// write) so two callers — the quote-refresh loop and the backfill service, say — can never
/// interleave and jointly overspend the per-minute window or race the same day's ledger row.
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
            var usedToday = entry?.CreditsUsed ?? 0;
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

            if (entry is null)
            {
                db.AddTwelveDataCreditLedgerEntry(new TwelveDataCreditLedgerEntry { Date = today, CreditsUsed = credits });
            }
            else
            {
                entry.CreditsUsed += credits;
            }

            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
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
