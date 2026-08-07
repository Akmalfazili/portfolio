using Portfolio.Application.Common;
using Portfolio.Application.Dtos;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services;

public interface IAssetService
{
    Task<IReadOnlyList<AssetDto>> ListAsync(AssetClass? assetClass, CancellationToken cancellationToken);

    Task<AssetDto?> GetByIdAsync(int id, CancellationToken cancellationToken);

    Task<ServiceResult<AssetDto>> CreateAsync(CreateAssetRequest request, CancellationToken cancellationToken);

    /// <summary>Full replace, including <see cref="Domain.Entities.Asset.IsActive"/> — the only
    /// way to deactivate an asset (D23/Phase 12). <see cref="ServiceError"/>'s
    /// <see cref="ServiceErrorKind.NotFound"/> kind covers an unknown id.</summary>
    Task<ServiceResult<AssetDto>> UpdateAsync(int id, UpdateAssetRequest request, CancellationToken cancellationToken);
}
