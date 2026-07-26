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

    void AddAsset(Asset asset);

    void AddTransaction(Transaction transaction);

    void RemoveTransaction(Transaction transaction);

    ValueTask<Asset?> FindAssetAsync(int id, CancellationToken cancellationToken);

    ValueTask<Transaction?> FindTransactionAsync(int id, CancellationToken cancellationToken);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
