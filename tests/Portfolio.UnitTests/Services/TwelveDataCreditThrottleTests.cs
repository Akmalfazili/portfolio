using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
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
        int perMinuteLimit = 8,
        int dailyBudget = 800,
        DateTimeOffset? now = null,
        ITwelveDataUsageProvider? usageProvider = null,
        int reconciliationIntervalMinutes = 60)
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<PortfolioDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<IPortfolioDbContext>(sp => sp.GetRequiredService<PortfolioDbContext>());
        if (usageProvider is not null)
        {
            services.AddScoped(_ => usageProvider);
        }

        var provider = services.BuildServiceProvider();

        var time = new FakeTimeProvider(now ?? DateTimeOffset.Parse("2026-08-21T14:00:00Z"));
        var throttle = new TwelveDataCreditThrottle(
            provider.GetRequiredService<IServiceScopeFactory>(),
            time,
            Options.Create(new TwelveDataCreditOptions
            {
                PerMinuteCreditLimit = perMinuteLimit,
                DailyCreditBudget = dailyBudget,
                ReconciliationIntervalMinutes = reconciliationIntervalMinutes,
            }),
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

    /// <summary>
    /// D39's core fix: the pre-fix behaviour started a brand-new UTC day's ledger at zero
    /// regardless of what Twelve Data's own counter already showed — this reproduces that exact
    /// gap (a day already 300 credits deep in real spend, e.g. from a source outside this
    /// process) and proves the first write of the day seeds from the real counter instead.
    /// </summary>
    [Fact]
    public async Task TryAcquireAsync_FirstWriteOfANewDay_SeedsLedgerFromRealUsage_NotZero()
    {
        var usageProvider = Substitute.For<ITwelveDataUsageProvider>();
        usageProvider.GetDailyUsageAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<int?>(300));
        var (sut, _) = CreateSut(usageProvider: usageProvider);

        var granted = await sut.TryAcquireAsync(5, CancellationToken.None);

        granted.Should().BeTrue();
        var status = await sut.GetStatusAsync(CancellationToken.None);
        status.CreditsUsedToday.Should().Be(305, "the day must be seeded from the real 300 already spent, plus this request's 5");
    }

    /// <summary>Confirms the pre-D39 fallback is preserved rather than a hard dependency being
    /// introduced: when no <see cref="ITwelveDataUsageProvider"/> is available at all (as in every
    /// other test in this file), a new day still starts from zero instead of throwing.</summary>
    [Fact]
    public async Task TryAcquireAsync_FirstWriteOfANewDay_WithNoUsageProviderWired_FallsBackToZero()
    {
        var (sut, _) = CreateSut(usageProvider: null);

        (await sut.TryAcquireAsync(5, CancellationToken.None)).Should().BeTrue();

        var status = await sut.GetStatusAsync(CancellationToken.None);
        status.CreditsUsedToday.Should().Be(5);
    }

    /// <summary>Same reproduction, but the real counter is unreachable (transient failure) — the
    /// seed must degrade to zero rather than fail the credit request outright.</summary>
    [Fact]
    public async Task TryAcquireAsync_FirstWriteOfANewDay_WhenRealUsageUnavailable_FallsBackToZero()
    {
        var usageProvider = Substitute.For<ITwelveDataUsageProvider>();
        usageProvider.GetDailyUsageAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<int?>(null));
        var (sut, _) = CreateSut(usageProvider: usageProvider);

        (await sut.TryAcquireAsync(5, CancellationToken.None)).Should().BeTrue();

        var status = await sut.GetStatusAsync(CancellationToken.None);
        status.CreditsUsedToday.Should().Be(5);
    }

    /// <summary>
    /// D39's second half: even after a correct seed, a long-lived process's locally-accumulated
    /// total can still drift from reality (an untracked spend elsewhere, a retry, etc.) — so it
    /// must be corrected periodically. Here the local ledger says 5 after the first call; the real
    /// counter has since moved to 400 by some means this process never recorded. Once the
    /// reconciliation interval has elapsed, the next acquire must pull the ledger back to reality
    /// rather than keep compounding on the stale local total.
    /// </summary>
    [Fact]
    public async Task TryAcquireAsync_AfterReconciliationIntervalElapses_OverwritesLocalTotalFromRealUsage()
    {
        var usageProvider = Substitute.For<ITwelveDataUsageProvider>();
        usageProvider.GetDailyUsageAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<int?>(300), Task.FromResult<int?>(400));
        var (sut, time) = CreateSut(usageProvider: usageProvider, reconciliationIntervalMinutes: 60);

        (await sut.TryAcquireAsync(5, CancellationToken.None)).Should().BeTrue(); // seeds at 300, now 305

        time.Advance(TimeSpan.FromMinutes(61));

        (await sut.TryAcquireAsync(1, CancellationToken.None)).Should().BeTrue(); // reconciles to 400, then +1

        var status = await sut.GetStatusAsync(CancellationToken.None);
        status.CreditsUsedToday.Should().Be(401);
    }

    /// <summary>
    /// The other half of "periodically, not per call": reconciliation must not fire on every
    /// acquire just because a usage provider happens to be wired up — <c>GET /api_usage</c> costs
    /// a real credit, so calling it more often than the configured interval would itself become a
    /// meaningful drain on the budget it exists to protect.
    /// </summary>
    [Fact]
    public async Task TryAcquireAsync_WithinTheReconciliationInterval_DoesNotCallTheUsageProviderAgain()
    {
        var usageProvider = Substitute.For<ITwelveDataUsageProvider>();
        usageProvider.GetDailyUsageAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<int?>(300));
        var (sut, time) = CreateSut(usageProvider: usageProvider, reconciliationIntervalMinutes: 60);

        (await sut.TryAcquireAsync(1, CancellationToken.None)).Should().BeTrue(); // seeds — 1 call
        time.Advance(TimeSpan.FromMinutes(30)); // well within the 60-minute interval
        (await sut.TryAcquireAsync(1, CancellationToken.None)).Should().BeTrue();
        (await sut.TryAcquireAsync(1, CancellationToken.None)).Should().BeTrue();

        await usageProvider.Received(1).GetDailyUsageAsync(Arg.Any<CancellationToken>());

        var status = await sut.GetStatusAsync(CancellationToken.None);
        status.CreditsUsedToday.Should().Be(303, "local accumulation must continue between reconciliations, on top of the 300 seed");
    }

    /// <summary>D39 explicitly forbids reconciling from a read-only status check — <c>GET
    /// /api/prices/status</c> can be polled by the frontend far more often than any sane credit
    /// reconciliation cadence, so it must never itself spend a Twelve Data credit.</summary>
    [Fact]
    public async Task GetStatusAsync_NeverCallsTheUsageProvider()
    {
        var usageProvider = Substitute.For<ITwelveDataUsageProvider>();
        usageProvider.GetDailyUsageAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<int?>(300));
        var (sut, _) = CreateSut(usageProvider: usageProvider);

        await sut.GetStatusAsync(CancellationToken.None);
        await sut.GetStatusAsync(CancellationToken.None);
        await sut.GetStatusAsync(CancellationToken.None);

        await usageProvider.DidNotReceive().GetDailyUsageAsync(Arg.Any<CancellationToken>());
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
