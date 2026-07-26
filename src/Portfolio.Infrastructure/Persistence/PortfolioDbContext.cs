using Microsoft.EntityFrameworkCore;
using Portfolio.Application.Abstractions;
using Portfolio.Domain.Entities;
using Portfolio.Infrastructure.Persistence.Seed;

namespace Portfolio.Infrastructure.Persistence;

/// <summary>
/// EF Core context for the portfolio tracker. Every decimal column is configured with explicit
/// precision in <see cref="OnModelCreating"/> — SQL Server's <c>decimal(18,2)</c> default would
/// silently destroy fractional crypto units and sub-cent unit prices. Never let a decimal
/// property fall through to that default.
///
/// Implements <see cref="IPortfolioDbContext"/> directly so Portfolio.Application's services can
/// depend on the interface (IQueryable-based) without referencing a specific EF Core provider.
/// </summary>
public class PortfolioDbContext(DbContextOptions<PortfolioDbContext> options) : DbContext(options), IPortfolioDbContext
{
    public DbSet<Asset> Assets => Set<Asset>();

    public DbSet<Transaction> Transactions => Set<Transaction>();

    public DbSet<PriceQuote> PriceQuotes => Set<PriceQuote>();

    public DbSet<PriceHistory> PriceHistories => Set<PriceHistory>();

    public DbSet<FxRate> FxRates => Set<FxRate>();

    public DbSet<RefreshRun> RefreshRuns => Set<RefreshRun>();

    IQueryable<Asset> IPortfolioDbContext.Assets => Assets;

    IQueryable<Transaction> IPortfolioDbContext.Transactions => Transactions;

    public void AddAsset(Asset asset) => Assets.Add(asset);

    public void AddTransaction(Transaction transaction) => Transactions.Add(transaction);

    public void RemoveTransaction(Transaction transaction) => Transactions.Remove(transaction);

    public ValueTask<Asset?> FindAssetAsync(int id, CancellationToken cancellationToken) =>
        Assets.FindAsync([id], cancellationToken);

    public ValueTask<Transaction?> FindTransactionAsync(int id, CancellationToken cancellationToken) =>
        Transactions.FindAsync([id], cancellationToken);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PortfolioDbContext).Assembly);

        modelBuilder.Entity<Asset>().HasData(SeedData.Assets);
    }
}
