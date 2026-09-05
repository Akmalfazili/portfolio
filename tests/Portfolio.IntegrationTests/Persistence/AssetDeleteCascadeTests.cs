using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Portfolio.Application.Services;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;
using Portfolio.Infrastructure.Persistence;

namespace Portfolio.IntegrationTests.Persistence;

/// <summary>
/// Proves <see cref="AssetService.DeleteAsync"/>'s cascade actually runs against real SQL Server.
/// The unit tests cannot: the EF Core InMemory provider has no foreign keys at all, so it would
/// happily accept a delete order that SQL Server rejects. The Transaction FK is
/// <c>DeleteBehavior.Restrict</c> on purpose, which means the asset row can only go once its
/// transactions are already gone — this is the tripwire for that ordering, and for a future
/// child table being added without being included in the cascade.
/// </summary>
public sealed class AssetDeleteCascadeTests : IAsyncLifetime
{
    // A dedicated database, same dual-connection-string convention as the app and as
    // PrecisionRoundTripTests: the env var wins under container/CI, Windows Auth against local
    // SQLEXPRESS otherwise.
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("ConnectionStrings__PortfolioDeleteCascadeTests")
        ?? @"Server=localhost\SQLEXPRESS;Database=PortfolioDeleteCascadeTests;Trusted_Connection=True;TrustServerCertificate=True";

    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

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

    /// <summary>
    /// zakat.md §3.2: <see cref="ZakatPayment"/> deliberately hangs off nothing — no foreign key to
    /// <see cref="Asset"/> at all. Deleting every asset in the database must leave the payment
    /// ledger completely untouched, unlike every other child table this test class covers.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_LeavesTheZakatPaymentLedgerCompletelyUntouched_AgainstSqlServer()
    {
        int assetId;
        int paymentId;

        await using (var seed = CreateContext())
        {
            var asset = new Asset
            {
                Symbol = "TSTZKT",
                Name = "Zakat Delete Test",
                AssetClass = AssetClass.Stock,
                Exchange = "NASDAQ",
                Currency = "USD",
                QuoteProviderKind = QuoteProviderKind.TwelveData,
                ProviderSymbol = "TSTZKT",
                IsActive = true,
                CreatedAt = Now,
            };
            seed.Assets.Add(asset);

            var payment = new ZakatPayment { PaidOn = new DateOnly(2026, 3, 1), AmountSgd = 250.75m };
            seed.ZakatPayments.Add(payment);

            await seed.SaveChangesAsync();
            assetId = asset.Id;
            paymentId = payment.Id;
        }

        await using (var act = CreateContext())
        {
            var service = new AssetService(act, TimeProvider.System);
            var deleted = await service.DeleteAsync(assetId, CancellationToken.None);
            deleted.Should().BeTrue();
        }

        await using var verify = CreateContext();
        (await verify.Assets.AnyAsync(a => a.Id == assetId)).Should().BeFalse();
        // Deleting the asset must not touch the payment ledger — no relationship exists to cascade
        // through, and nothing in AssetService.DeleteAsync references ZakatPayment at all.
        (await verify.ZakatPayments.AnyAsync(p => p.Id == paymentId)).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheAssetAndEveryChildRow_AgainstSqlServer()
    {
        int assetId;

        await using (var seed = CreateContext())
        {
            var asset = new Asset
            {
                Symbol = "TSTDEL",
                Name = "Delete Cascade Test",
                AssetClass = AssetClass.Stock,
                Exchange = "NASDAQ",
                Currency = "USD",
                QuoteProviderKind = QuoteProviderKind.TwelveData,
                ProviderSymbol = "TSTDEL",
                IsActive = true,
                CreatedAt = Now,
            };
            seed.Assets.Add(asset);
            await seed.SaveChangesAsync();
            assetId = asset.Id;

            seed.Transactions.Add(new Transaction
            {
                AssetId = assetId,
                Type = TransactionType.Buy,
                TradeDate = new DateOnly(2026, 1, 5),
                Quantity = 1.5m,
                PricePerUnit = 190.25m,
                Fees = 1.25m,
                Currency = "USD",
            });
            seed.PriceHistories.Add(new PriceHistory
            {
                AssetId = assetId,
                Date = new DateOnly(2026, 1, 5),
                Close = 191m,
                Currency = "USD",
            });
            seed.PriceQuotes.Add(new PriceQuote
            {
                AssetId = assetId,
                Price = 192m,
                Currency = "USD",
                AsOf = Now,
            });
            seed.DividendEvents.Add(new DividendEvent
            {
                AssetId = assetId,
                ExDate = new DateOnly(2026, 3, 1),
                AmountPerShare = 0.25m,
                Currency = "USD",
            });
            seed.AssetDividendStates.Add(new AssetDividendState
            {
                AssetId = assetId,
                LastAttemptedAt = Now,
                LastSuccessAt = Now,
                LastRunSuccess = true,
            });
            await seed.SaveChangesAsync();
        }

        await using (var act = CreateContext())
        {
            var service = new AssetService(act, TimeProvider.System);
            var deleted = await service.DeleteAsync(assetId, CancellationToken.None);
            deleted.Should().BeTrue();
        }

        await using var verify = CreateContext();
        (await verify.Assets.AnyAsync(a => a.Id == assetId)).Should().BeFalse();
        (await verify.Transactions.AnyAsync(t => t.AssetId == assetId)).Should().BeFalse();
        (await verify.PriceHistories.AnyAsync(p => p.AssetId == assetId)).Should().BeFalse();
        (await verify.PriceQuotes.AnyAsync(q => q.AssetId == assetId)).Should().BeFalse();
        (await verify.DividendEvents.AnyAsync(d => d.AssetId == assetId)).Should().BeFalse();
        (await verify.AssetDividendStates.AnyAsync(s => s.AssetId == assetId)).Should().BeFalse();
    }
}
