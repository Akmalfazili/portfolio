using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Services;
using Portfolio.Infrastructure.Persistence;

namespace Portfolio.UnitTests.Services;

/// <summary>
/// <see cref="TwelveDataCreditThrottle"/> is the D38 fix's core piece: never more than
/// <see cref="TwelveDataCreditOptions.PerMinuteCreditLimit"/> credits in any rolling 60-second
/// window, and never more than <see cref="TwelveDataCreditOptions.DailyCreditBudget"/> in one UTC
/// day, tracked from a persisted ledger rather than an in-memory assumption reset on restart.
/// Uses <see cref="FakeTimeProvider"/>, not <c>MutableTimeProvider</c>, because the pacing wait
/// goes through <c>Task.Delay(wait, timeProvider, ...)</c> — same reasoning as
/// <c>PriceRefreshBackgroundServiceTests</c>.
/// </summary>
public sealed class TwelveDataCreditThrottleTests
{
    private static (TwelveDataCreditThrottle Throttle, FakeTimeProvider Time) CreateSut(
        int perMinuteLimit = 8, int dailyBudget = 800, DateTimeOffset? now = null)
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<PortfolioDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<IPortfolioDbContext>(sp => sp.GetRequiredService<PortfolioDbContext>());
        var provider = services.BuildServiceProvider();

        var time = new FakeTimeProvider(now ?? DateTimeOffset.Parse("2026-08-21T14:00:00Z"));
        var throttle = new TwelveDataCreditThrottle(
            provider.GetRequiredService<IServiceScopeFactory>(),
            time,
            Options.Create(new TwelveDataCreditOptions { PerMinuteCreditLimit = perMinuteLimit, DailyCreditBudget = dailyBudget }),
            NullLogger<TwelveDataCreditThrottle>.Instance);

        return (throttle, time);
    }

    [Fact]
    public async Task TryAcquireAsync_GrantsImmediately_WhenWellUnderThePerMinuteLimit()
    {
        var (sut, _) = CreateSut();

        var granted = await sut.TryAcquireAsync(8, CancellationToken.None);

        granted.Should().BeTrue();
        var status = await sut.GetStatusAsync(CancellationToken.None);
        status.CreditsUsedToday.Should().Be(8);
    }

    /// <summary>
    /// D38's core reproducing case at the throttle level: a single acquire for more credits than
    /// fit in one per-minute window (21, mirroring the live 21-symbol batch) can never be granted
    /// no matter how long it waits, since 21 will never fit under a limit of 8 — so the throttle
    /// rejects it outright as a caller contract violation instead of hanging or silently
    /// mis-pacing it. This is exactly why <c>TwelveDataQuoteProvider</c> must chunk to at most the
    /// per-minute limit per acquire instead of asking for the whole batch at once (which is what
    /// the pre-D38-fix code did, and what always 429'd against the real API).
    /// </summary>
    [Fact]
    public async Task TryAcquireAsync_ASingleRequestOverThePerMinuteLimit_IsRejected()
    {
        var (sut, _) = CreateSut(perMinuteLimit: 8);

        var act = () => sut.TryAcquireAsync(21, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task TryAcquireAsync_SecondChunkWithinTheSameWindow_WaitsForRoomThenGrants()
    {
        var (sut, time) = CreateSut(perMinuteLimit: 8);

        (await sut.TryAcquireAsync(8, CancellationToken.None)).Should().BeTrue();

        var secondTask = sut.TryAcquireAsync(8, CancellationToken.None);

        // Give the async machinery a moment to register the delay, then prove it really is
        // waiting rather than having already granted instantly.
        await Task.Delay(50);
        secondTask.IsCompleted.Should().BeFalse("8 + 8 exceeds the per-minute limit of 8; it must wait for the window to clear");

        var granted = await AdvanceUntilCompletedAsync(time, secondTask, TimeSpan.FromSeconds(5));

        granted.Should().BeTrue();
        var status = await sut.GetStatusAsync(CancellationToken.None);
        status.CreditsUsedToday.Should().Be(16);
    }

    [Fact]
    public async Task TryAcquireAsync_RequestLargerThanRemainingDailyBudget_IsDenied_AndLedgerUnchanged()
    {
        var (sut, _) = CreateSut(perMinuteLimit: 800, dailyBudget: 10); // huge per-minute limit isolates the daily-budget check

        (await sut.TryAcquireAsync(8, CancellationToken.None)).Should().BeTrue(); // 8 used, 2 left

        var denied = await sut.TryAcquireAsync(5, CancellationToken.None); // would need 13, only 2 left

        denied.Should().BeFalse();
        var status = await sut.GetStatusAsync(CancellationToken.None);
        status.CreditsUsedToday.Should().Be(8, "a denied request must not record any spend");
        status.RemainingToday.Should().Be(2);
    }

    [Fact]
    public async Task GetStatusAsync_ReflectsPersistedSpend_AcrossANewThrottleInstance_OverTheSameDatabase()
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<PortfolioDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<IPortfolioDbContext>(sp => sp.GetRequiredService<PortfolioDbContext>());
        var provider = services.BuildServiceProvider();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-21T14:00:00Z"));

        var first = new TwelveDataCreditThrottle(
            provider.GetRequiredService<IServiceScopeFactory>(),
            time,
            Options.Create(new TwelveDataCreditOptions { PerMinuteCreditLimit = 8, DailyCreditBudget = 800 }),
            NullLogger<TwelveDataCreditThrottle>.Instance);
        await first.TryAcquireAsync(8, CancellationToken.None);

        // A brand new throttle instance (as a fresh process restart would create) reads the SAME
        // persisted ledger, not an assumption reset to zero - this is exactly what makes the daily
        // budget durable across a restart rather than the process quietly re-granting a full 800.
        var second = new TwelveDataCreditThrottle(
            provider.GetRequiredService<IServiceScopeFactory>(),
            time,
            Options.Create(new TwelveDataCreditOptions { PerMinuteCreditLimit = 8, DailyCreditBudget = 800 }),
            NullLogger<TwelveDataCreditThrottle>.Instance);

        var status = await second.GetStatusAsync(CancellationToken.None);
        status.CreditsUsedToday.Should().Be(8);
        status.RemainingToday.Should().Be(792);
    }

    /// <summary>Nudges the fake clock forward in small steps until <paramref name="task"/>
    /// completes, yielding real time in between so the throttle's internal await actually has a
    /// chance to observe each advance — mirrors the equivalent helper in
    /// <c>PriceRefreshBackgroundServiceTests</c>.</summary>
    private static async Task<bool> AdvanceUntilCompletedAsync(FakeTimeProvider time, Task<bool> task, TimeSpan step)
    {
        for (var attempt = 0; attempt < 200 && !task.IsCompleted; attempt++)
        {
            time.Advance(step);
            await Task.Delay(10);
        }

        return await task;
    }
}
