using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services;

namespace Portfolio.UnitTests.Services;

/// <summary>
/// Covers the hosting loop itself rather than the refresh logic it drives — the part that has to
/// keep running for the life of the process. <see cref="FakeTimeProvider"/> is used instead of
/// <c>MutableTimeProvider</c> because the loop waits with <c>Task.Delay(interval, timeProvider,
/// ...)</c>, which goes through <see cref="TimeProvider.CreateTimer"/>: a provider that only
/// overrides <see cref="TimeProvider.GetUtcNow"/> would still sleep for a real 30 seconds.
///
/// The behaviour that matters here is resilience. If a tick throws and the loop exits, the app
/// silently stops refreshing prices until someone restarts it — quotes just quietly go stale with
/// nothing in the UI to say so.
/// </summary>
public sealed class PriceRefreshBackgroundServiceTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task KeepsPolling_AfterARefreshCycleThrows()
    {
        var stub = new StubRefreshService
        {
            OnCall = call => call == 1
                ? throw new InvalidOperationException("provider blew up mid-cycle")
                : Task.CompletedTask,
        };
        var logger = new CapturingLogger<PriceRefreshBackgroundService>();
        var (sut, time) = CreateSut(stub, logger);

        await sut.StartAsync(CancellationToken.None);
        try
        {
            var keptGoing = await AdvanceUntilAsync(time, () => stub.CallCount >= 2);

            keptGoing.Should().BeTrue(
                "the loop must survive a throwing cycle and try again on the next tick, not exit");
            sut.ExecuteTask.Should().NotBeNull();
            sut.ExecuteTask!.IsFaulted.Should().BeFalse("the exception must be swallowed, not surface as a faulted task");
            sut.ExecuteTask.IsCompleted.Should().BeFalse("the loop should still be running");

            // The failure must be visible to an operator, not silently discarded.
            logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task CreatesAFreshScopePerTick()
    {
        // IPortfolioDbContext is scoped, so reusing one scope for the process lifetime would leak
        // a single DbContext across every refresh for days and accumulate tracked entities.
        var stub = new StubRefreshService();
        var (sut, time, resolutions) = CreateSutTrackingScopes(stub);

        await sut.StartAsync(CancellationToken.None);
        try
        {
            await AdvanceUntilAsync(time, () => stub.CallCount >= 3);

            resolutions().Should().Be(
                stub.CallCount,
                "each tick should resolve the refresh service from its own scope");
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StopAsync_EndsTheLoopCleanly_WithoutFaulting()
    {
        var stub = new StubRefreshService();
        var (sut, time) = CreateSut(stub, new CapturingLogger<PriceRefreshBackgroundService>());

        await sut.StartAsync(CancellationToken.None);
        await AdvanceUntilAsync(time, () => stub.CallCount >= 1);

        await sut.StopAsync(CancellationToken.None);

        sut.ExecuteTask.Should().NotBeNull();
        sut.ExecuteTask!.IsCompleted.Should().BeTrue();
        sut.ExecuteTask.IsFaulted.Should().BeFalse("cancellation is a normal shutdown, not a failure");

        // Time moving on after shutdown must not wake it up again.
        var callsAtStop = stub.CallCount;
        time.Advance(PollInterval * 3);
        await Task.Delay(50);
        stub.CallCount.Should().Be(callsAtStop);
    }

    private static (PriceRefreshBackgroundService Sut, FakeTimeProvider Time) CreateSut(
        StubRefreshService stub, ILogger<PriceRefreshBackgroundService> logger)
    {
        var services = new ServiceCollection();
        services.AddScoped<IPriceRefreshService>(_ => stub);
        var provider = services.BuildServiceProvider();

        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-31T04:00:00Z"));
        var sut = new PriceRefreshBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            time,
            Options.Create(new PriceRefreshOptions { PollInterval = PollInterval }),
            logger);

        return (sut, time);
    }

    private static (PriceRefreshBackgroundService Sut, FakeTimeProvider Time, Func<int> Resolutions) CreateSutTrackingScopes(
        StubRefreshService stub)
    {
        var resolutions = 0;
        var services = new ServiceCollection();

        // Scoped registrations are cached per scope, so the factory running once per tick is
        // exactly what proves a new scope was created for that tick.
        services.AddScoped<IPriceRefreshService>(_ =>
        {
            Interlocked.Increment(ref resolutions);
            return stub;
        });
        var provider = services.BuildServiceProvider();

        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-31T04:00:00Z"));
        var sut = new PriceRefreshBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            time,
            Options.Create(new PriceRefreshOptions { PollInterval = PollInterval }),
            new CapturingLogger<PriceRefreshBackgroundService>());

        return (sut, time, () => Volatile.Read(ref resolutions));
    }

    /// <summary>
    /// Nudges the fake clock until <paramref name="condition"/> holds. The loop registers its
    /// timer only after a tick's work completes, so a single <c>Advance</c> issued too early would
    /// fire nothing and then hang forever — advancing repeatedly, yielding real time in between,
    /// avoids that race without depending on the loop's internal scheduling.
    /// </summary>
    private static async Task<bool> AdvanceUntilAsync(FakeTimeProvider time, Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            time.Advance(PollInterval);
            await Task.Delay(10);
        }

        return condition();
    }

    private sealed class StubRefreshService : IPriceRefreshService
    {
        private int _callCount;

        /// <summary>Invoked with the 1-based call number, so a specific tick can be made to throw.</summary>
        public Func<int, Task>? OnCall { get; init; }

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<PriceRefreshCycleResult> RefreshDueAsync(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (OnCall is not null)
            {
                await OnCall(call);
            }

            return new PriceRefreshCycleResult(PriceRefreshOutcome.NothingDue, null, [], 0);
        }

        public Task<PriceRefreshCycleResult> RefreshNowAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("The background loop must never trigger a manual refresh.");
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, Exception? Exception)> _entries = [];

        public IReadOnlyList<(LogLevel Level, Exception? Exception)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, exception));
            }
        }
    }
}
