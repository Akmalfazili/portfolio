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

    void AddAsset(Asset asset);

    void AddTransaction(Transaction transaction);

    void RemoveTransaction(Transaction transaction);

    void AddPriceHistory(PriceHistory priceHistory);

    void AddFxRate(FxRate fxRate);

    void AddPriceQuote(PriceQuote priceQuote);

    void AddRefreshRun(RefreshRun refreshRun);

    void AddSourceRefreshState(SourceRefreshState state);

    void AddTwelveDataCreditLedgerEntry(TwelveDataCreditLedgerEntry entry);

    ValueTask<Asset?> FindAssetAsync(int id, CancellationToken cancellationToken);

    ValueTask<Transaction?> FindTransactionAsync(int id, CancellationToken cancellationToken);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
