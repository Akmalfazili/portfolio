using Microsoft.EntityFrameworkCore;
using Portfolio.Domain.Entities;
using Portfolio.Infrastructure.Persistence.Seed;

namespace Portfolio.Infrastructure.Persistence;

/// <summary>
/// EF Core context for the portfolio tracker. Every decimal column is configured with explicit
/// precision in <see cref="OnModelCreating"/> — SQL Server's <c>decimal(18,2)</c> default would
/// silently destroy fractional crypto units and sub-cent unit prices. Never let a decimal
/// property fall through to that default.
/// </summary>
public class PortfolioDbContext(DbContextOptions<PortfolioDbContext> options) : DbContext(options)
{
    public DbSet<Asset> Assets => Set<Asset>();

    public DbSet<Transaction> Transactions => Set<Transaction>();

    public DbSet<PriceQuote> PriceQuotes => Set<PriceQuote>();

    public DbSet<PriceHistory> PriceHistories => Set<PriceHistory>();

    public DbSet<FxRate> FxRates => Set<FxRate>();

    public DbSet<RefreshRun> RefreshRuns => Set<RefreshRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PortfolioDbContext).Assembly);

        modelBuilder.Entity<Asset>().HasData(SeedData.Assets);
    }
}
