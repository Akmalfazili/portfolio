using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;
using Portfolio.Infrastructure.Persistence;

namespace Portfolio.IntegrationTests.Persistence;

/// <summary>
/// Runs <see cref="PriceRefreshStatusEnricher.EnrichAsync"/>'s per-market close-status query (the
/// private <c>BuildMarketCloseStatusesAsync</c>) against real SQL Server. The unit-test
/// suite (<c>Portfolio.UnitTests.Services.PriceRefreshStatusEnricherTests</c>) proves the
/// enrichment's selection/aggregation rules against the EF Core InMemory provider, but InMemory
/// cannot prove the LINQ actually translates: <c>BackfillTriggers.Contains(r.Trigger)</c> (an array
/// field captured in a query), the correlated <c>PriceHistories.Max(...)</c> subquery per asset,
/// and the nullable <c>r.Market!.Value</c> projection once the query has already filtered
/// <c>Market != null</c>. This class is that tripwire.
///
/// <para><c>EnsureCreatedAsync</c> applies <see cref="Portfolio.Infrastructure.Persistence.Seed.SeedData"/>'s
/// fixed-id <c>HasData</c> rows (AAPL, MSFT, Z74, ETH, AMP, ANVL) the same way the real migrations
/// do, so tests here use the already-seeded Z74 (fetched by symbol, not assumed by id) rather than
/// inserting a second asset with the same <c>Symbol</c> — that collides with <c>IX_Assets_Symbol</c>,
/// same trap <c>AssetDeleteCascadeTests</c> avoids with its own non-colliding <c>TSTZKT</c> symbol.
/// The seeded AAPL/MSFT (Nyse) carry no transaction, so they never qualify as "held" and leave NYSE's
/// close status at "never attempted" for the tests below that need that baseline.</para>
/// </summary>
public sealed class PriceRefreshStatusEnricherTests : IAsyncLifetime
{
    // A dedicated database, same dual-connection-string convention as every other integration test
    // class here: the env var wins under container/CI, Windows Auth against local SQLEXPRESS otherwise.
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("ConnectionStrings__PortfolioRefreshStatusEnricherTests")
        ?? @"Server=localhost\SQLEXPRESS;Database=PortfolioRefreshStatusEnricherTests;Trusted_Connection=True;TrustServerCertificate=True";

    private static readonly PriceRefreshStatus BareStatus = new(
        LastRefreshedAt: new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero),
        NyseOpen: true,
        SgxOpen: false,
        NextScheduledRunAt: null,
        Sources: []);

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

    private static ITwelveDataCreditThrottle FullBudgetThrottle()
    {
        var throttle = Substitute.For<ITwelveDataCreditThrottle>();
        throttle.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(new TwelveDataCreditStatus(0, 800, 800));
        return throttle;
    }

    /// <summary>
    /// Reproduces the 2026-09-13 live shape from the D53 follow-up this ships alongside: a live-quote
    /// Yahoo failure that the closed market can never clear, superseded by a genuinely successful
    /// close backfill that ran afterwards. Exercises every rule at once against real SQL Server:
    /// the trigger filter, the nullable Market projection, the min-of-max PriceHistory subquery, and
    /// LastSuccessAt surviving nothing (there is no later failure here — the opposite shape, a later
    /// failure, is covered by the InMemory unit test since it does not depend on SQL translation).
    /// </summary>
    [Fact]
    public async Task EnrichAsync_Closes_Sgx_ReflectsTheSuccessfulBackfillDespiteAnEarlierLiveQuoteFailure()
    {
        int z74Id;
        await using (var context = CreateContext())
        {
            // Z74 is already seeded (HasData) — give it a transaction so it qualifies as "held",
            // rather than inserting a second asset with the same Symbol (IX_Assets_Symbol is unique).
            z74Id = await context.Assets.Where(a => a.Symbol == "Z74").Select(a => a.Id).SingleAsync();

            context.Transactions.Add(new Transaction
            {
                AssetId = z74Id,
                Type = TransactionType.Buy,
                TradeDate = new DateOnly(2026, 1, 2),
                Quantity = 100,
                PricePerUnit = 4m,
                Fees = 0,
                Currency = "SGD",
            });
            context.PriceHistories.Add(new PriceHistory
            {
                AssetId = z74Id,
                Date = new DateOnly(2026, 9, 11),
                Close = 4.50m,
                Currency = "SGD",
            });

            // The live-quote Yahoo failure — a closed market never live-polls, so this row can
            // never clear itself and must be irrelevant to Closes entirely.
            context.RefreshRuns.Add(new RefreshRun
            {
                Trigger = RefreshTrigger.Scheduled,
                AssetClass = AssetClass.Stock,
                Market = null, // live-quote rows are never market-scoped
                StartedAt = new DateTimeOffset(2026, 9, 11, 8, 56, 0, TimeSpan.Zero),
                CompletedAt = new DateTimeOffset(2026, 9, 11, 8, 56, 3, TimeSpan.Zero),
                Success = false,
                ErrorMessage = "Yahoo request failed",
                SymbolsRefreshed = 0,
            });

            // The two genuinely successful close-backfill runs that followed.
            context.RefreshRuns.Add(new RefreshRun
            {
                Trigger = RefreshTrigger.BackfillScheduled,
                AssetClass = AssetClass.Stock,
                Market = Market.Sgx,
                StartedAt = new DateTimeOffset(2026, 9, 12, 14, 48, 0, TimeSpan.Zero),
                CompletedAt = new DateTimeOffset(2026, 9, 12, 14, 48, 5, TimeSpan.Zero),
                Success = true,
                SymbolsRefreshed = 1,
            });
            context.RefreshRuns.Add(new RefreshRun
            {
                Trigger = RefreshTrigger.BackfillManual,
                AssetClass = AssetClass.Stock,
                Market = Market.Sgx,
                StartedAt = new DateTimeOffset(2026, 9, 12, 17, 1, 0, TimeSpan.Zero),
                CompletedAt = new DateTimeOffset(2026, 9, 12, 17, 1, 2, TimeSpan.Zero),
                Success = true,
                SymbolsRefreshed = 1,
            });

            await context.SaveChangesAsync();
        }

        await using var readContext = CreateContext();
        var enricher = new PriceRefreshStatusEnricher(readContext, FullBudgetThrottle(), Options.Create(new PriceRefreshOptions()));

        var result = await enricher.EnrichAsync(BareStatus, CancellationToken.None);

        result.Closes.Should().NotBeNull();
        result.Closes!.Select(c => c.Market).Should().BeEquivalentTo([Market.Nyse, Market.Sgx]);

        var sgx = result.Closes!.Single(c => c.Market == Market.Sgx);
        sgx.LatestCloseDate.Should().Be(new DateOnly(2026, 9, 11));
        // The most recent completed backfill run, not the earlier live-quote failure.
        sgx.LastAttemptedAt.Should().Be(new DateTimeOffset(2026, 9, 12, 17, 1, 2, TimeSpan.Zero));
        sgx.LastSuccessAt.Should().Be(new DateTimeOffset(2026, 9, 12, 17, 1, 2, TimeSpan.Zero));
        sgx.LastRunSuccess.Should().BeTrue();
        sgx.LastError.Should().BeNull();

        // NYSE has no assets, no runs at all: never attempted, every field null, never false/zero.
        var nyse = result.Closes!.Single(c => c.Market == Market.Nyse);
        nyse.LatestCloseDate.Should().BeNull();
        nyse.LastAttemptedAt.Should().BeNull();
        nyse.LastSuccessAt.Should().BeNull();
        nyse.LastRunSuccess.Should().BeNull();
        nyse.LastError.Should().BeNull();
    }

    /// <summary>
    /// The live D53-follow-up shape this ships to fix: ARVLF's provider has permanently stopped
    /// publishing daily closes, so its own max PriceHistory date lags a genuinely up-to-date sibling.
    /// Declaring <see cref="Asset.ExcludeFromCloseCoverage"/> on it removes it from the min-of-max
    /// entirely, against real SQL Server's translation of the added <c>!a.ExcludeFromCloseCoverage</c>
    /// predicate — not just LINQ-to-Objects.
    /// </summary>
    [Fact]
    public async Task EnrichAsync_Closes_Nyse_ExcludedAssetsStaleCloseDoesNotPinTheFloor_AgainstSqlServer()
    {
        await using (var context = CreateContext())
        {
            // AAPL is already seeded (HasData) — give it a transaction and an up-to-date close.
            var aaplId = await context.Assets.Where(a => a.Symbol == "AAPL").Select(a => a.Id).SingleAsync();
            context.Transactions.Add(new Transaction
            {
                AssetId = aaplId,
                Type = TransactionType.Buy,
                TradeDate = new DateOnly(2026, 1, 2),
                Quantity = 10,
                PricePerUnit = 190m,
                Fees = 0,
                Currency = "USD",
            });
            context.PriceHistories.Add(new PriceHistory
            {
                AssetId = aaplId,
                Date = new DateOnly(2026, 9, 15),
                Close = 191m,
                Currency = "USD",
            });

            var arvlf = new Asset
            {
                Symbol = "ARVLF",
                Name = "Arrival SA",
                AssetClass = AssetClass.Stock,
                Currency = "USD",
                QuoteProviderKind = QuoteProviderKind.TwelveData,
                ProviderSymbol = "ARVLF",
                IsActive = true,
                ExcludeFromCloseCoverage = true,
                CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            };
            context.Assets.Add(arvlf);
            await context.SaveChangesAsync();

            context.Transactions.Add(new Transaction
            {
                AssetId = arvlf.Id,
                Type = TransactionType.Buy,
                TradeDate = new DateOnly(2026, 1, 2),
                Quantity = 4,
                PricePerUnit = 0.0001m,
                Fees = 0,
                Currency = "USD",
            });
            context.PriceHistories.Add(new PriceHistory
            {
                AssetId = arvlf.Id,
                Date = new DateOnly(2026, 9, 14), // stale — its provider has stopped publishing closes
                Close = 0.0001m,
                Currency = "USD",
            });
            await context.SaveChangesAsync();
        }

        await using var readContext = CreateContext();
        var enricher = new PriceRefreshStatusEnricher(readContext, FullBudgetThrottle(), Options.Create(new PriceRefreshOptions()));

        var result = await enricher.EnrichAsync(BareStatus, CancellationToken.None);

        // ARVLF's stale 9/14 close must not drag the floor down from AAPL's genuine 9/15.
        result.Closes!.Single(c => c.Market == Market.Nyse).LatestCloseDate.Should().Be(new DateOnly(2026, 9, 15));
    }

    /// <summary>
    /// The specific translation the InMemory suite cannot prove: a legacy pre-D47 row (<c>Market ==
    /// null</c>) must vanish under SQL's three-valued NULL logic, not merely under LINQ-to-Objects'
    /// C# <c>!= null</c> semantics — the two agree here, but only running against a real server rules
    /// out an EF Core SQL-translation quirk turning the nullable-enum comparison into something that
    /// spuriously matches (or throws on) a NULL column.
    /// </summary>
    [Fact]
    public async Task EnrichAsync_Closes_IgnoresALegacyPreD47NullMarketRow_AgainstSqlServer()
    {
        await using (var context = CreateContext())
        {
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
        var enricher = new PriceRefreshStatusEnricher(readContext, FullBudgetThrottle(), Options.Create(new PriceRefreshOptions()));

        var result = await enricher.EnrichAsync(BareStatus, CancellationToken.None);

        result.Closes!.Should().OnlyContain(c =>
            c.LastAttemptedAt == null && c.LastSuccessAt == null && c.LastRunSuccess == null);
    }

    /// <summary>
    /// Confirms the enricher never spends a Twelve Data credit or a per-minute request slot: it must
    /// only ever call <see cref="ITwelveDataCreditThrottle.GetStatusAsync"/>, never
    /// <see cref="ITwelveDataCreditThrottle.TryAcquireAsync"/>. <c>GET /api/prices/status</c>'s
    /// documented "can never cost a credit" guarantee depends on this holding even with real data
    /// and a real SQL round trip in the mix.
    /// </summary>
    [Fact]
    public async Task EnrichAsync_NeverCallsTryAcquireOnTheCreditThrottle()
    {
        // AAPL/MSFT (active, Twelve Data) are already seeded — no extra asset needed.
        await using var readContext = CreateContext();
        var throttle = FullBudgetThrottle();
        var enricher = new PriceRefreshStatusEnricher(readContext, throttle, Options.Create(new PriceRefreshOptions()));

        await enricher.EnrichAsync(BareStatus, CancellationToken.None);

        await throttle.Received(1).GetStatusAsync(Arg.Any<CancellationToken>());
        await throttle.DidNotReceive().TryAcquireAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
