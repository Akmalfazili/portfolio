using Portfolio.Domain.Entities;

namespace Portfolio.Application.Abstractions;

/// <summary>
/// Minimal abstraction over the EF Core context so services in <c>Portfolio.Application</c> can
/// express query and mutation logic — including <c>IQueryable</c> projections — without taking a
/// package reference on any specific EF Core provider. <c>Portfolio.Infrastructure</c>'s
/// <c>PortfolioDbContext</c> implements this directly; <c>Portfolio.Application</c> only ever
/// sees Domain entities and <see cref="IQueryable{T}"/>.
/// </summary>
public interface IPortfolioDbContext
{
    IQueryable<Asset> Assets { get; }

    IQueryable<Transaction> Transactions { get; }

    IQueryable<PriceHistory> PriceHistories { get; }

    IQueryable<FxRate> FxRates { get; }

    IQueryable<PriceQuote> PriceQuotes { get; }

    IQueryable<RefreshRun> RefreshRuns { get; }

    IQueryable<SourceRefreshState> SourceRefreshStates { get; }

    IQueryable<TwelveDataCreditLedgerEntry> TwelveDataCreditLedgerEntries { get; }

    IQueryable<DividendEvent> DividendEvents { get; }

    IQueryable<AssetDividendState> AssetDividendStates { get; }

    void AddAsset(Asset asset);

    /// <summary>
    /// Removes an asset row only. Its children are NOT removed implicitly — see
    /// <see cref="RemoveTransactions"/>, <see cref="RemovePriceHistories"/> and
    /// <see cref="RemovePriceQuotes"/>, which callers must invoke explicitly. Relying on the
    /// database's cascade would behave differently under the EF Core InMemory provider (which
    /// only cascades to entities already tracked) than under SQL Server, and the Transaction
    /// foreign key is <c>Restrict</c> on purpose, so a cascade there does not exist at all.
    /// </summary>
    void RemoveAsset(Asset asset);

    void AddTransaction(Transaction transaction);

    void RemoveTransaction(Transaction transaction);

    void RemoveTransactions(IEnumerable<Transaction> transactions);

    void RemovePriceHistories(IEnumerable<PriceHistory> priceHistories);

    void RemovePriceQuotes(IEnumerable<PriceQuote> priceQuotes);

    /// <summary>
    /// Removes an asset's dividend payment history. Not delegated to the database's cascade —
    /// see <see cref="RemoveAsset"/>'s remarks, which apply identically here: the Asset →
    /// DividendEvent foreign key is <c>DeleteBehavior.Restrict</c> on purpose.
    /// </summary>
    void RemoveDividendEvents(IEnumerable<DividendEvent> dividendEvents);

    /// <summary>Removes an asset's dividend backfill state row, if it has one. Same explicit-delete
    /// reasoning as <see cref="RemoveDividendEvents"/>.</summary>
    void RemoveAssetDividendStates(IEnumerable<AssetDividendState> states);

    void AddPriceHistory(PriceHistory priceHistory);

    void AddDividendEvent(DividendEvent dividendEvent);

    void AddAssetDividendState(AssetDividendState state);

    void AddFxRate(FxRate fxRate);

    void AddPriceQuote(PriceQuote priceQuote);

    void AddRefreshRun(RefreshRun refreshRun);

    void AddSourceRefreshState(SourceRefreshState state);

    void AddTwelveDataCreditLedgerEntry(TwelveDataCreditLedgerEntry entry);

    ValueTask<Asset?> FindAssetAsync(int id, CancellationToken cancellationToken);

    ValueTask<Transaction?> FindTransactionAsync(int id, CancellationToken cancellationToken);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
