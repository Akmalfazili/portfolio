using Microsoft.EntityFrameworkCore;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Common;
using Portfolio.Application.Dtos;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services;

public sealed class AssetService(IPortfolioDbContext db, TimeProvider timeProvider) : IAssetService
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
                a.QuoteProviderKind,
                a.ProviderSymbol,
                a.ProviderCoinId,
                a.IsActive,
                a.CreatedAt,
                // D27. Translated to SQL by EF, so this stays one query rather than an N+1 across
                // the asset list. PriceHistory is included as well as PriceQuote because a stock
                // that was backfilled but has no live quote HAS been priced — only an asset that
                // no source has ever produced a number for is worth pointing at.
                a.PriceQuote != null || a.PriceHistories.Any(),
                // D27 fresh-database fix: gate the escalation on the *provider* ever having
                // succeeded, not on this one asset's own history — see ProviderHasEverSucceeded's
                // own doc comment for why. Also translated to SQL, still one query.
                db.SourceRefreshStates.Any(s => s.Source == a.QuoteProviderKind && s.LastSuccessAt != null)))
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
                a.QuoteProviderKind,
                a.ProviderSymbol,
                a.ProviderCoinId,
                a.IsActive,
                a.CreatedAt,
                // D27. Translated to SQL by EF, so this stays one query rather than an N+1 across
                // the asset list. PriceHistory is included as well as PriceQuote because a stock
                // that was backfilled but has no live quote HAS been priced — only an asset that
                // no source has ever produced a number for is worth pointing at.
                a.PriceQuote != null || a.PriceHistories.Any(),
                db.SourceRefreshStates.Any(s => s.Source == a.QuoteProviderKind && s.LastSuccessAt != null)))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<ServiceResult<AssetDto>> CreateAsync(CreateAssetRequest request, CancellationToken cancellationToken)
    {
        var errors = ValidateCore(request.Symbol, request.Name, request.Currency, request.QuoteProviderKind, request.ProviderSymbol, request.ProviderCoinId);

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
            QuoteProviderKind = request.QuoteProviderKind,
            ProviderSymbol = request.ProviderSymbol,
            ProviderCoinId = request.ProviderCoinId,
            IsActive = true,
            CreatedAt = timeProvider.GetUtcNow(),
        };

        db.AddAsset(asset);
        await db.SaveChangesAsync(cancellationToken);

        // Freshly created: no source has had a chance to price it yet, so HasEverBeenPriced is
        // false by construction rather than by query. That is the normal, benign case D27's UI
        // has to distinguish from a wrong symbol — hence CreatedAt travelling alongside it.
        // ProviderHasEverSucceeded is still a real query, though — it asks about the *provider*,
        // shared with every other asset already routed through it, not about this brand-new row.
        var providerHasEverSucceeded = await db.SourceRefreshStates
            .AnyAsync(s => s.Source == asset.QuoteProviderKind && s.LastSuccessAt != null, cancellationToken);

        return ServiceResult<AssetDto>.Success(ToDto(asset, hasEverBeenPriced: false, providerHasEverSucceeded));
    }

    public async Task<ServiceResult<AssetDto>> UpdateAsync(int id, UpdateAssetRequest request, CancellationToken cancellationToken)
    {
        var asset = await db.FindAssetAsync(id, cancellationToken);
        if (asset is null)
        {
            return ServiceResult<AssetDto>.Failure(ServiceError.NotFound());
        }

        var errors = ValidateCore(request.Symbol, request.Name, request.Currency, request.QuoteProviderKind, request.ProviderSymbol, request.ProviderCoinId);

        if (errors.Count == 0 &&
            await db.Assets.AnyAsync(a => a.Id != id && a.Symbol == request.Symbol, cancellationToken))
        {
            errors["symbol"] = [$"An asset with symbol '{request.Symbol}' already exists."];
        }

        if (errors.Count > 0)
        {
            return ServiceResult<AssetDto>.Failure(ServiceError.Validation(errors));
        }

        asset.Symbol = request.Symbol;
        asset.Name = request.Name;
        asset.AssetClass = request.AssetClass;
        asset.Exchange = request.Exchange;
        asset.Currency = request.Currency;
        asset.QuoteProviderKind = request.QuoteProviderKind;
        asset.ProviderSymbol = request.ProviderSymbol;
        asset.ProviderCoinId = request.ProviderCoinId;
        asset.IsActive = request.IsActive;
        // CreatedAt is deliberately not settable through an update: D27's hint measures how long
        // an asset has gone unpriced, and letting a rename reset that clock would erase the signal.

        await db.SaveChangesAsync(cancellationToken);

        var hasEverBeenPriced = await db.PriceQuotes.AnyAsync(q => q.AssetId == id, cancellationToken)
            || await db.PriceHistories.AnyAsync(p => p.AssetId == id, cancellationToken);
        var providerHasEverSucceeded = await db.SourceRefreshStates
            .AnyAsync(s => s.Source == asset.QuoteProviderKind && s.LastSuccessAt != null, cancellationToken);

        return ServiceResult<AssetDto>.Success(ToDto(asset, hasEverBeenPriced, providerHasEverSucceeded));
    }

    /// <summary>
    /// D23: beyond the pre-existing "required, unique symbol" checks, the provider identifier
    /// field matching <paramref name="providerKind"/> is required too. <c>QuoteProviderRouter</c>
    /// dispatches purely on <see cref="Domain.Enums.QuoteProviderKind"/> — a null identifier for
    /// the chosen provider means no refresh or backfill cycle can ever price the asset, so this
    /// must be rejected at creation/update time rather than discovered later as a permanent
    /// "Awaiting price".
    /// </summary>
    private static Dictionary<string, string[]> ValidateCore(
        string? symbol, string? name, string? currency, QuoteProviderKind providerKind,
        string? providerSymbol, string? providerCoinId)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(symbol))
        {
            errors["symbol"] = ["Symbol is required."];
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["Name is required."];
        }

        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
        {
            errors["currency"] = ["Currency must be a 3-letter ISO 4217 code."];
        }

        switch (providerKind)
        {
            case QuoteProviderKind.TwelveData:
            case QuoteProviderKind.Yahoo:
                if (string.IsNullOrWhiteSpace(providerSymbol))
                {
                    errors["providerSymbol"] = [$"ProviderSymbol is required when QuoteProviderKind is {providerKind}."];
                }

                break;
            case QuoteProviderKind.CoinGecko:
                if (string.IsNullOrWhiteSpace(providerCoinId))
                {
                    errors["providerCoinId"] = ["ProviderCoinId is required when QuoteProviderKind is CoinGecko (the coin id, e.g. \"ethereum\" — not the ticker)."];
                }

                break;
        }

        return errors;
    }

    private static AssetDto ToDto(Asset a, bool hasEverBeenPriced, bool providerHasEverSucceeded) => new(
        a.Id,
        a.Symbol,
        a.Name,
        a.AssetClass,
        a.Exchange,
        a.Currency,
        a.QuoteProviderKind,
        a.ProviderSymbol,
        a.ProviderCoinId,
        a.IsActive,
        a.CreatedAt,
        hasEverBeenPriced,
        providerHasEverSucceeded);
}
