using Portfolio.Application.Common;
using Portfolio.Application.Dtos;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services;

public interface ITransactionService
{
    Task<IReadOnlyList<TransactionDto>> ListAsync(
        AssetClass? assetClass, int? assetId, CancellationToken cancellationToken);

    Task<TransactionDto?> GetByIdAsync(int id, CancellationToken cancellationToken);

    Task<ServiceResult<TransactionDto>> CreateAsync(
        CreateTransactionRequest request, CancellationToken cancellationToken);

    Task<ServiceResult<TransactionDto>> UpdateAsync(
        int id, UpdateTransactionRequest request, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken);
}
