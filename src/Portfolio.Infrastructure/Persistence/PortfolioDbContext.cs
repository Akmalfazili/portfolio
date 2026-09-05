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

    public DbSet<SourceRefreshState> SourceRefreshStates => Set<SourceRefreshState>();

    public DbSet<TwelveDataCreditLedgerEntry> TwelveDataCreditLedgerEntries => Set<TwelveDataCreditLedgerEntry>();

    public DbSet<DividendEvent> DividendEvents => Set<DividendEvent>();

    public DbSet<AssetDividendState> AssetDividendStates => Set<AssetDividendState>();

    public DbSet<ZakatPayment> ZakatPayments => Set<ZakatPayment>();

    IQueryable<Asset> IPortfolioDbContext.Assets => Assets;

    IQueryable<Transaction> IPortfolioDbContext.Transactions => Transactions;

    IQueryable<PriceHistory> IPortfolioDbContext.PriceHistories => PriceHistories;

    IQueryable<FxRate> IPortfolioDbContext.FxRates => FxRates;

    IQueryable<PriceQuote> IPortfolioDbContext.PriceQuotes => PriceQuotes;

    IQueryable<RefreshRun> IPortfolioDbContext.RefreshRuns => RefreshRuns;

    IQueryable<SourceRefreshState> IPortfolioDbContext.SourceRefreshStates => SourceRefreshStates;

    IQueryable<TwelveDataCreditLedgerEntry> IPortfolioDbContext.TwelveDataCreditLedgerEntries => TwelveDataCreditLedgerEntries;

    IQueryable<DividendEvent> IPortfolioDbContext.DividendEvents => DividendEvents;

    IQueryable<AssetDividendState> IPortfolioDbContext.AssetDividendStates => AssetDividendStates;

    IQueryable<ZakatPayment> IPortfolioDbContext.ZakatPayments => ZakatPayments;

    public void AddAsset(Asset asset) => Assets.Add(asset);

    public void RemoveAsset(Asset asset) => Assets.Remove(asset);

    public void AddTransaction(Transaction transaction) => Transactions.Add(transaction);

    public void RemoveTransaction(Transaction transaction) => Transactions.Remove(transaction);

    public void RemoveTransactions(IEnumerable<Transaction> transactions) => Transactions.RemoveRange(transactions);

    public void RemovePriceHistories(IEnumerable<PriceHistory> priceHistories) => PriceHistories.RemoveRange(priceHistories);

    public void RemovePriceQuotes(IEnumerable<PriceQuote> priceQuotes) => PriceQuotes.RemoveRange(priceQuotes);

    public void RemoveDividendEvents(IEnumerable<DividendEvent> dividendEvents) => DividendEvents.RemoveRange(dividendEvents);

    public void RemoveAssetDividendStates(IEnumerable<AssetDividendState> states) => AssetDividendStates.RemoveRange(states);

    public void AddPriceHistory(PriceHistory priceHistory) => PriceHistories.Add(priceHistory);

    public void AddDividendEvent(DividendEvent dividendEvent) => DividendEvents.Add(dividendEvent);

    public void AddAssetDividendState(AssetDividendState state) => AssetDividendStates.Add(state);

    public void AddFxRate(FxRate fxRate) => FxRates.Add(fxRate);

    public void AddPriceQuote(PriceQuote priceQuote) => PriceQuotes.Add(priceQuote);

    public void AddRefreshRun(RefreshRun refreshRun) => RefreshRuns.Add(refreshRun);

    public void AddSourceRefreshState(SourceRefreshState state) => SourceRefreshStates.Add(state);

    public void AddTwelveDataCreditLedgerEntry(TwelveDataCreditLedgerEntry entry) => TwelveDataCreditLedgerEntries.Add(entry);

    public void AddZakatPayment(ZakatPayment payment) => ZakatPayments.Add(payment);

    public void RemoveZakatPayment(ZakatPayment payment) => ZakatPayments.Remove(payment);

    public ValueTask<Asset?> FindAssetAsync(int id, CancellationToken cancellationToken) =>
        Assets.FindAsync([id], cancellationToken);

    public ValueTask<Transaction?> FindTransactionAsync(int id, CancellationToken cancellationToken) =>
        Transactions.FindAsync([id], cancellationToken);

    public ValueTask<ZakatPayment?> FindZakatPaymentAsync(int id, CancellationToken cancellationToken) =>
        ZakatPayments.FindAsync([id], cancellationToken);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PortfolioDbContext).Assembly);

        modelBuilder.Entity<Asset>().HasData(SeedData.Assets);
    }
}
