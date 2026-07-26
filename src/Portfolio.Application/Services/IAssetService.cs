using Portfolio.Application.Common;
using Portfolio.Application.Dtos;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services;

public interface IAssetService
{
    Task<IReadOnlyList<AssetDto>> ListAsync(AssetClass? assetClass, CancellationToken cancellationToken);

    Task<AssetDto?> GetByIdAsync(int id, CancellationToken cancellationToken);

    Task<ServiceResult<AssetDto>> CreateAsync(CreateAssetRequest request, CancellationToken cancellationToken);
}
