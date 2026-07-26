using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;
using Portfolio.Infrastructure.Persistence;

namespace Portfolio.IntegrationTests.Persistence;

/// <summary>
/// Proves the decimal precision configured in <see cref="PortfolioDbContext.OnModelCreating"/>
/// actually survives a round trip through SQL Server. The default EF Core SQL Server precision
/// of decimal(18,2) would silently round the ANVL-style price used here to 0.00 and truncate the
/// fractional quantity — this test is the tripwire for that regression.
/// </summary>
public sealed class PrecisionRoundTripTests : IAsyncLifetime
{
    // A dedicated database so this test never collides with the seeded dev database.
    // Honours the same dual-connection-string convention as the app: the
    // ConnectionStrings__Portfolio env var (container / CI) wins when present, otherwise we
    // fall back to Windows Auth against local SQLEXPRESS.
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("ConnectionStrings__PortfolioPrecisionTests")
        ?? "Server=localhost\\SQLEXPRESS;Database=PortfolioPrecisionTests;Trusted_Connection=True;TrustServerCertificate=True";

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
    public async Task FractionalCryptoQuantity_SurvivesRoundTrip()
    {
        const decimal quantity = 0.000123456m;

        int transactionId;
        await using (var context = CreateContext())
        {
            var transaction = new Transaction
            {
                AssetId = 4, // ETH, seeded
                Type = TransactionType.Buy,
                TradeDate = new DateOnly(2026, 1, 15),
                Quantity = quantity,
                PricePerUnit = 2500m,
                Fees = 1.2345m,
                Currency = "USD",
            };
            context.Transactions.Add(transaction);
            await context.SaveChangesAsync();
            transactionId = transaction.Id;
        }

        await using var readContext = CreateContext();
        var reloaded = await readContext.Transactions.AsNoTracking()
            .SingleAsync(t => t.Id == transactionId);

        reloaded.Quantity.Should().Be(quantity);
    }

    [Fact]
    public async Task SubCentUnitPrice_SurvivesRoundTrip_AndMultipliesWithoutLoss()
    {
        // ANVL-style sub-cent price times a bulk quantity — decimal(18,2) would round this to
        // 0.00 and destroy the multiplication below.
        const decimal quantity = 1_000_000m;
        const decimal pricePerUnit = 0.0005326m;
        const decimal expectedTotal = 532.6000m;

        (quantity * pricePerUnit).Should().Be(expectedTotal);

        int transactionId;
        await using (var context = CreateContext())
        {
            var transaction = new Transaction
            {
                AssetId = 6, // ANVL, seeded
                Type = TransactionType.Buy,
                TradeDate = new DateOnly(2026, 2, 1),
                Quantity = quantity,
                PricePerUnit = pricePerUnit,
                Fees = 0m,
                Currency = "USD",
            };
            context.Transactions.Add(transaction);
            await context.SaveChangesAsync();
            transactionId = transaction.Id;
        }

        await using var readContext = CreateContext();
        var reloaded = await readContext.Transactions.AsNoTracking()
            .SingleAsync(t => t.Id == transactionId);

        reloaded.PricePerUnit.Should().Be(pricePerUnit);
        reloaded.Quantity.Should().Be(quantity);
        (reloaded.Quantity * reloaded.PricePerUnit).Should().Be(expectedTotal);
    }

    [Fact]
    public async Task FxRate_SurvivesRoundTrip()
    {
        const decimal rate = 1.34567891m; // 8 decimal places, per decimal(18,8)

        int fxRateId;
        await using (var context = CreateContext())
        {
            var fxRate = new FxRate
            {
                Date = new DateOnly(2026, 3, 1),
                Base = "USD",
                Quote = "SGD",
                Rate = rate,
            };
            context.FxRates.Add(fxRate);
            await context.SaveChangesAsync();
            fxRateId = fxRate.Id;
        }

        await using var readContext = CreateContext();
        var reloaded = await readContext.FxRates.AsNoTracking().SingleAsync(f => f.Id == fxRateId);

        reloaded.Rate.Should().Be(rate);
    }
}
