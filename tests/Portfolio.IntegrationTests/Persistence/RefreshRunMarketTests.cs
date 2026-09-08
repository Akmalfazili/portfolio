using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;
using Portfolio.Infrastructure.Persistence;

namespace Portfolio.IntegrationTests.Persistence;

/// <summary>
/// D47: <see cref="RefreshRun.Market"/> is what <c>PriceBackfillService.RunIfDueAsync</c>'s
/// per-market due-ness query keys on — it must round-trip through SQL Server, and a legacy NULL
/// row (every row written before this column existed) must be invisible to that query rather than
/// accidentally matching every market or throwing. The EF Core InMemory provider used by the unit
/// tests does not exercise the actual SQL <c>NULL</c> comparison semantics a nullable enum column
/// depends on here (<c>Market == @p</c> is false, never true, when <c>Market IS NULL</c>) — this
/// is the tripwire for that against a real server.
/// </summary>
public sealed class RefreshRunMarketTests : IAsyncLifetime
{
    // A dedicated database, same dual-connection-string convention as the app and every other
    // integration test class here: the env var wins under container/CI, Windows Auth against
    // local SQLEXPRESS otherwise.
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("ConnectionStrings__PortfolioRefreshRunMarketTests")
        ?? @"Server=localhost\SQLEXPRESS;Database=PortfolioRefreshRunMarketTests;Trusted_Connection=True;TrustServerCertificate=True";

    private PortfolioDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PortfolioDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;

        return new PortfolioDbContext(options);
    }

    public async Task InitializeAsync()
    {
        await using var context = CreateContext();
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await using var context = CreateContext();
        await context.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task Market_SurvivesRoundTrip_ForEachMarket()
    {
        int nyseRunId;
        int sgxRunId;

        await using (var context = CreateContext())
        {
            var nyseRun = new RefreshRun
            {
                Trigger = RefreshTrigger.BackfillScheduled,
                AssetClass = AssetClass.Stock,
                Market = Market.Nyse,
                StartedAt = new DateTimeOffset(2026, 9, 8, 20, 0, 0, TimeSpan.Zero),
                CompletedAt = new DateTimeOffset(2026, 9, 8, 20, 0, 5, TimeSpan.Zero),
                Success = true,
                SymbolsRefreshed = 21,
            };
            var sgxRun = new RefreshRun
            {
                Trigger = RefreshTrigger.BackfillScheduled,
                AssetClass = AssetClass.Stock,
                Market = Market.Sgx,
                StartedAt = new DateTimeOffset(2026, 9, 8, 9, 0, 0, TimeSpan.Zero),
                CompletedAt = new DateTimeOffset(2026, 9, 8, 9, 0, 2, TimeSpan.Zero),
                Success = true,
                SymbolsRefreshed = 1,
            };
            context.RefreshRuns.AddRange(nyseRun, sgxRun);
            await context.SaveChangesAsync();
            nyseRunId = nyseRun.Id;
            sgxRunId = sgxRun.Id;
        }

        await using var readContext = CreateContext();
        var reloadedNyse = await readContext.RefreshRuns.AsNoTracking().SingleAsync(r => r.Id == nyseRunId);
        var reloadedSgx = await readContext.RefreshRuns.AsNoTracking().SingleAsync(r => r.Id == sgxRunId);

        reloadedNyse.Market.Should().Be(Market.Nyse);
        reloadedSgx.Market.Should().Be(Market.Sgx);
    }

    [Fact]
    public async Task Market_SurvivesRoundTrip_AsNull_ForQuoteRefreshRunsWhichAreNeverMarketScoped()
    {
        // Scheduled/Manual quote-refresh rows (PriceRefreshService) cover both markets at once and
        // were never given a Market — confirms NULL itself round-trips cleanly, not just the two
        // real enum values.
        int runId;
        await using (var context = CreateContext())
        {
            var run = new RefreshRun
            {
                Trigger = RefreshTrigger.Scheduled,
                AssetClass = null,
                Market = null,
                StartedAt = new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero),
                CompletedAt = new DateTimeOffset(2026, 9, 8, 10, 0, 1, TimeSpan.Zero),
                Success = true,
                SymbolsRefreshed = 22,
            };
            context.RefreshRuns.Add(run);
            await context.SaveChangesAsync();
            runId = run.Id;
        }

        await using var readContext = CreateContext();
        var reloaded = await readContext.RefreshRuns.AsNoTracking().SingleAsync(r => r.Id == runId);

        reloaded.Market.Should().BeNull();
    }

    /// <summary>
    /// D47's whole no-audit-backfill-needed argument, proven against real SQL Server: a legacy
    /// NULL-market row must never satisfy <c>r.Market == market</c> for either market — SQL's
    /// three-valued NULL comparison logic (<c>NULL == 'Nyse'</c> is UNKNOWN, not TRUE) is exactly
    /// what makes the per-market due-ness query in <c>PriceBackfillService.RunIfDueAsync</c> treat
    /// a pre-D47 row as if it never existed, for both markets at once, without a migration having
    /// to touch a single existing row.
    /// </summary>
    [Fact]
    public async Task PerMarketQuery_IgnoresLegacyNullMarketRow_ForBothMarkets()
    {
        await using (var context = CreateContext())
        {
            // A legacy row: written before D47, so it predates the Market column entirely.
            context.RefreshRuns.Add(new RefreshRun
            {
                Trigger = RefreshTrigger.BackfillScheduled,
                AssetClass = AssetClass.Stock,
                Market = null,
                StartedAt = new DateTimeOffset(2026, 9, 1, 20, 0, 0, TimeSpan.Zero),
                CompletedAt = new DateTimeOffset(2026, 9, 1, 20, 0, 1, TimeSpan.Zero),
                Success = true,
                SymbolsRefreshed = 22,
            });
            await context.SaveChangesAsync();
        }

        await using var readContext = CreateContext();

        var nyseMatch = await readContext.RefreshRuns
            .Where(r => r.Trigger == RefreshTrigger.BackfillScheduled && r.Market == Market.Nyse)
            .MaxAsync(r => (DateTimeOffset?)r.CompletedAt);
        var sgxMatch = await readContext.RefreshRuns
            .Where(r => r.Trigger == RefreshTrigger.BackfillScheduled && r.Market == Market.Sgx)
            .MaxAsync(r => (DateTimeOffset?)r.CompletedAt);

        // Neither market finds the legacy row — exactly the "each market simply runs once, for
        // free" self-healing behaviour D47 relies on, with no explicit migration data-fix.
        nyseMatch.Should().BeNull();
        sgxMatch.Should().BeNull();
    }

    [Fact]
    public async Task PerMarketQuery_FindsOnlyItsOwnMarketsRow_WhenBothMarketsHaveOne()
    {
        await using (var context = CreateContext())
        {
            context.RefreshRuns.AddRange(
                new RefreshRun
                {
                    Trigger = RefreshTrigger.BackfillScheduled,
                    AssetClass = AssetClass.Stock,
                    Market = Market.Nyse,
                    StartedAt = new DateTimeOffset(2026, 9, 8, 20, 0, 0, TimeSpan.Zero),
                    CompletedAt = new DateTimeOffset(2026, 9, 8, 20, 0, 5, TimeSpan.Zero),
                    Success = true,
                    SymbolsRefreshed = 21,
                },
                new RefreshRun
                {
                    Trigger = RefreshTrigger.BackfillScheduled,
                    AssetClass = AssetClass.Stock,
                    Market = Market.Sgx,
                    StartedAt = new DateTimeOffset(2026, 9, 8, 9, 0, 0, TimeSpan.Zero),
                    CompletedAt = new DateTimeOffset(2026, 9, 8, 9, 0, 2, TimeSpan.Zero),
                    Success = true,
                    SymbolsRefreshed = 1,
                });
            await context.SaveChangesAsync();
        }

        await using var readContext = CreateContext();

        var nyseMatch = await readContext.RefreshRuns
            .Where(r => r.Trigger == RefreshTrigger.BackfillScheduled && r.Market == Market.Nyse)
            .MaxAsync(r => (DateTimeOffset?)r.CompletedAt);
        var sgxMatch = await readContext.RefreshRuns
            .Where(r => r.Trigger == RefreshTrigger.BackfillScheduled && r.Market == Market.Sgx)
            .MaxAsync(r => (DateTimeOffset?)r.CompletedAt);

        nyseMatch.Should().Be(new DateTimeOffset(2026, 9, 8, 20, 0, 5, TimeSpan.Zero));
        sgxMatch.Should().Be(new DateTimeOffset(2026, 9, 8, 9, 0, 2, TimeSpan.Zero));
    }
}
