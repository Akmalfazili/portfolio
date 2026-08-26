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

    /// <summary>
    /// Permanently deletes an asset and every child row that belongs to it — transactions,
    /// price history, and its quote — in one <c>SaveChangesAsync</c>. Irreversible, and NOT the
    /// same thing as <see cref="UpdateAsync"/> with <c>IsActive = false</c>: deactivation
    /// preserves the holding and its history, deletion destroys them.
    /// </summary>
    /// <returns><see langword="false"/> when no asset has that id; otherwise
    /// <see langword="true"/>.</returns>
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken);
}
