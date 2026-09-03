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

    /// <summary>
    /// Same reproduction, but the real counter is unreachable (transient failure) — the seed must
    /// degrade rather than fail the credit request outright. D45 changed what it degrades *to*: the
    /// probe still went over the wire and Twelve Data still billed it, so the day starts at that
    /// one credit rather than at zero. Contrast
    /// <see cref="TryAcquireAsync_FirstWriteOfANewDay_WithNoUsageProviderWired_FallsBackToZero"/>,
    /// where no request is made at all and zero is the honest answer.
    /// </summary>
    [Fact]
    public async Task TryAcquireAsync_FirstWriteOfANewDay_WhenRealUsageUnavailable_StillCountsTheProbesOwnCredit()
    {
        var usageProvider = Substitute.For<ITwelveDataUsageProvider>();
        usageProvider.GetDailyUsageAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<int?>(null));
        var (sut, _) = CreateSut(usageProvider: usageProvider);

        (await sut.TryAcquireAsync(5, CancellationToken.None)).Should().BeTrue();

        var status = await sut.GetStatusAsync(CancellationToken.None);
        status.CreditsUsedToday.Should().Be(6, "the failed /api_usage probe was billed too — 1 for it, 5 for this request");
    }

    /// <summary>
    /// D45's core reproduction, at the throttle level. <c>GET /api_usage</c> is a real Twelve Data
    /// request that spends a real credit, but the pre-fix code issued it from inside
    /// <c>SeedOrReconcileAsync</c> and never recorded it in the rolling per-minute window — that
    /// window was written from <c>TryAcquireAsync</c> alone. So the window read one short of what
    /// had actually left the process, and the throttle would grant a further full 8 credits on top
    /// of the probe: 9 requests into an 8-request minute, and Twelve Data 429s the 9th.
    ///
    /// Measured live 2026-09-03 against the running stack: the scheduled backfill's 8th Twelve Data
    /// asset 429'd on every run whose day-seed landed in the same minute (FSLY on 2026-08-25/27/29
    /// and 09-03, ERIC on 08-26 when the stale-first ordering shuffled FSLY to the front), holding
    /// that asset's price history days behind every other holding.
    ///
    /// Pre-fix this test fails by completing instantly — the point is that it must not.
    /// </summary>
    [Fact]
    public async Task TryAcquireAsync_TheUsageProbe_ConsumesAPerMinuteSlotOfItsOwn()
    {
        var usageProvider = Substitute.For<ITwelveDataUsageProvider>();
        usageProvider.GetDailyUsageAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<int?>(1));
        var (sut, time) = CreateSut(perMinuteLimit: 8, usageProvider: usageProvider);

        // Seeds the day, which fires the probe: 1 slot for the probe + 1 for this grant = 2 of 8.
        (await sut.TryAcquireAsync(1, CancellationToken.None)).Should().BeTrue();

        // Only 6 slots are genuinely left, so a 7-credit request cannot be served this minute.
        var next = sut.TryAcquireAsync(7, CancellationToken.None);

        await Task.Delay(50);
        next.IsCompleted.Should().BeFalse(
            "the /api_usage probe occupied one of the eight per-minute slots; granting 7 more would put 9 requests into one minute");

        (await AdvanceUntilCompletedAsync(time, next, TimeSpan.FromSeconds(5))).Should().BeTrue();
    }

    /// <summary>
    /// The failure-mode half of D45. A probe that throws or 429s tells the throttle nothing, and the
    /// pre-fix <c>_lastReconciledAt</c> was only stamped on success — so a persistently unhappy
    /// <c>/api_usage</c> would be re-probed on *every* acquire, each attempt billed in full and (once
    /// the probe claims a slot) each one eating the per-minute capacity the caller needs. Backing off
    /// for the whole interval on any attempt is what keeps a bad hour from compounding.
    /// </summary>
    [Fact]
    public async Task TryAcquireAsync_AfterAFailedProbe_DoesNotProbeAgainUntilTheIntervalElapses()
    {
        var usageProvider = Substitute.For<ITwelveDataUsageProvider>();
        usageProvider.GetDailyUsageAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<int?>(null));
        var (sut, time) = CreateSut(usageProvider: usageProvider, reconciliationIntervalMinutes: 60);

        (await sut.TryAcquireAsync(1, CancellationToken.None)).Should().BeTrue(); // seed attempt — 1 probe
        time.Advance(TimeSpan.FromMinutes(30));
        (await sut.TryAcquireAsync(1, CancellationToken.None)).Should().BeTrue();
        (await sut.TryAcquireAsync(1, CancellationToken.None)).Should().BeTrue();

        await usageProvider.Received(1).GetDailyUsageAsync(Arg.Any<CancellationToken>());

        time.Advance(TimeSpan.FromMinutes(31)); // now past the interval

        (await sut.TryAcquireAsync(1, CancellationToken.None)).Should().BeTrue();

        await usageProvider.Received(2).GetDailyUsageAsync(Arg.Any<CancellationToken>());

        var status = await sut.GetStatusAsync(CancellationToken.None);
        status.CreditsUsedToday.Should().Be(
            6,
            "2 failed probes were billed alongside the 4 granted credits, and neither probe may go unrecorded");
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
