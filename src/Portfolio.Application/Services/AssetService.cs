using Microsoft.EntityFrameworkCore;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Common;
using Portfolio.Application.Dtos;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services;

public sealed class AssetService(IPortfolioDbContext db) : IAssetService
{
    public async Task<IReadOnlyList<AssetDto>> ListAsync(AssetClass? assetClass, CancellationToken cancellationToken)
    {
        var query = db.Assets;

        if (assetClass is { } requestedClass)
        {
            query = query.Where(a => a.AssetClass == requestedClass);
        }

        return await query
            .OrderBy(a => a.Symbol)
            .Select(a => new AssetDto(
                a.Id,
                a.Symbol,
                a.Name,
                a.AssetClass,
                a.Exchange,
                a.Currency,
                a.ProviderSymbol,
                a.ProviderCoinId,
                a.IsActive))
            .ToListAsync(cancellationToken);
    }

    public Task<AssetDto?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        db.Assets
            .Where(a => a.Id == id)
            .Select(a => new AssetDto(
                a.Id,
                a.Symbol,
                a.Name,
                a.AssetClass,
                a.Exchange,
                a.Currency,
                a.ProviderSymbol,
                a.ProviderCoinId,
                a.IsActive))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<ServiceResult<AssetDto>> CreateAsync(CreateAssetRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.Symbol))
        {
            errors["symbol"] = ["Symbol is required."];
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = ["Name is required."];
        }

        if (string.IsNullOrWhiteSpace(request.Currency) || request.Currency.Length != 3)
        {
            errors["currency"] = ["Currency must be a 3-letter ISO 4217 code."];
        }

        if (errors.Count == 0 &&
            await db.Assets.AnyAsync(a => a.Symbol == request.Symbol, cancellationToken))
        {
            errors["symbol"] = [$"An asset with symbol '{request.Symbol}' already exists."];
        }

        if (errors.Count > 0)
        {
            return ServiceResult<AssetDto>.Failure(ServiceError.Validation(errors));
        }

        var asset = new Asset
        {
            Symbol = request.Symbol,
            Name = request.Name,
            AssetClass = request.AssetClass,
            Exchange = request.Exchange,
            Currency = request.Currency,
            ProviderSymbol = request.ProviderSymbol,
            ProviderCoinId = request.ProviderCoinId,
            IsActive = true,
        };

        db.AddAsset(asset);
        await db.SaveChangesAsync(cancellationToken);

        return ServiceResult<AssetDto>.Success(ToDto(asset));
    }

    private static AssetDto ToDto(Asset a) => new(
        a.Id,
        a.Symbol,
        a.Name,
        a.AssetClass,
        a.Exchange,
        a.Currency,
        a.ProviderSymbol,
        a.ProviderCoinId,
        a.IsActive);
}
