using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services;
using Portfolio.Domain.Enums;

namespace Portfolio.UnitTests.Services;

/// <summary>
/// Covers the D12 hosting loop itself, not the gating logic inside <c>PriceBackfillService</c>
/// (see <c>PriceBackfillServiceTests</c>'s <c>RunIfDueAsync_*</c> cases for that). Mirrors
/// <see cref="PriceRefreshBackgroundServiceTests"/> — same resilience concerns apply: a throwing
/// tick must not end the loop, or the app silently stops backfilling until a restart, which is
/// exactly the bug this class exists to close.
/// </summary>
public sealed class PriceBackfillBackgroundServiceTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);

    [Fact]
    public async Task KeepsPolling_AfterACheckThrows()
    {
        var stub = new StubBackfillService
        {
            OnCall = call => call == 1
                ? throw new InvalidOperationException("db unavailable mid-check")
                : Task.CompletedTask,
        };
        var logger = new CapturingLogger<PriceBackfillBackgroundService>();
        var (sut, time) = CreateSut(stub, logger);

        await sut.StartAsync(CancellationToken.None);
        try
        {
            var keptGoing = await AdvanceUntilAsync(time, () => stub.CallCount >= 2);

            keptGoing.Should().BeTrue(
                "the loop must survive a throwing check and try again on the next tick, not exit");
            sut.ExecuteTask.Should().NotBeNull();
            sut.ExecuteTask!.IsFaulted.Should().BeFalse("the exception must be swallowed, not surface as a faulted task");
            sut.ExecuteTask.IsCompleted.Should().BeFalse("the loop should still be running");

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
        var stub = new StubBackfillService();
        var (sut, time, resolutions) = CreateSutTrackingScopes(stub);

        await sut.StartAsync(CancellationToken.None);
        try
        {
            await AdvanceUntilAsync(time, () => stub.CallCount >= 3);

            resolutions().Should().Be(stub.CallCount, "each tick should resolve the backfill service from its own scope");
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StopAsync_EndsTheLoopCleanly_WithoutFaulting()
    {
        var stub = new StubBackfillService();
        var (sut, time) = CreateSut(stub, new CapturingLogger<PriceBackfillBackgroundService>());

        await sut.StartAsync(CancellationToken.None);
        await AdvanceUntilAsync(time, () => stub.CallCount >= 1);

        await sut.StopAsync(CancellationToken.None);

        sut.ExecuteTask.Should().NotBeNull();
        sut.ExecuteTask!.IsCompleted.Should().BeTrue();
        sut.ExecuteTask.IsFaulted.Should().BeFalse("cancellation is a normal shutdown, not a failure");

        var callsAtStop = stub.CallCount;
        time.Advance(PollInterval * 3);
        await Task.Delay(50);
        stub.CallCount.Should().Be(callsAtStop);
    }

    private static (PriceBackfillBackgroundService Sut, FakeTimeProvider Time) CreateSut(
        StubBackfillService stub, ILogger<PriceBackfillBackgroundService> logger)
    {
        var services = new ServiceCollection();
        services.AddScoped<IPriceBackfillService>(_ => stub);
        var provider = services.BuildServiceProvider();

        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-07T21:00:00Z"));
        var sut = new PriceBackfillBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            time,
            Options.Create(new PriceBackfillOptions { SchedulePollInterval = PollInterval }),
            logger);

        return (sut, time);
    }

    private static (PriceBackfillBackgroundService Sut, FakeTimeProvider Time, Func<int> Resolutions) CreateSutTrackingScopes(
        StubBackfillService stub)
    {
        var resolutions = 0;
        var services = new ServiceCollection();

        services.AddScoped<IPriceBackfillService>(_ =>
        {
            Interlocked.Increment(ref resolutions);
            return stub;
        });
        var provider = services.BuildServiceProvider();

        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-07T21:00:00Z"));
        var sut = new PriceBackfillBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            time,
            Options.Create(new PriceBackfillOptions { SchedulePollInterval = PollInterval }),
            new CapturingLogger<PriceBackfillBackgroundService>());

        return (sut, time, () => Volatile.Read(ref resolutions));
    }

    private static async Task<bool> AdvanceUntilAsync(FakeTimeProvider time, Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            time.Advance(PollInterval);
            await Task.Delay(10);
        }

        return condition();
    }

    private sealed class StubBackfillService : IPriceBackfillService
    {
        private int _callCount;

        public Func<int, Task>? OnCall { get; init; }

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<PriceBackfillRunResult> RunIfDueAsync(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (OnCall is not null)
            {
                await OnCall(call);
            }

            // Not due for either market — mirrors a real AlreadyCoveredSinceLastClose tick.
            return new PriceBackfillRunResult(
                [],
                [
                    new PriceBackfillMarketSkip(Market.Nyse, PriceBackfillSkipReason.AlreadyCoveredSinceLastClose),
                    new PriceBackfillMarketSkip(Market.Sgx, PriceBackfillSkipReason.AlreadyCoveredSinceLastClose),
                ],
                null);
        }

        public Task<PriceBackfillSummary> RunAsync(RefreshTrigger trigger, IReadOnlyCollection<Market> markets, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The background loop must only ever call RunIfDueAsync, never RunAsync directly.");
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
