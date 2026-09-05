using Microsoft.EntityFrameworkCore;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Common;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services.Calculators;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services;

/// <summary>
/// Transaction CRUD with the domain validation rules: quantity must be positive, a sell cannot
/// exceed the units currently held for that asset, and the trade date cannot be in the future.
/// <see cref="TimeProvider"/> is injected (never <c>DateTime.Now</c>) so the "no future dates"
/// rule is deterministic and testable.
/// </summary>
public sealed class TransactionService(IPortfolioDbContext db, TimeProvider timeProvider) : ITransactionService
{
    public Task<IReadOnlyList<TransactionDto>> ListAsync(
        AssetClass? assetClass, int? assetId, CancellationToken cancellationToken) =>
        ListInternalAsync(assetClass, assetId, cancellationToken);

    private async Task<IReadOnlyList<TransactionDto>> ListInternalAsync(
        AssetClass? assetClass, int? assetId, CancellationToken cancellationToken)
    {
        var query = db.Transactions;

        if (assetClass is { } requestedClass)
        {
            query = query.Where(t => t.Asset!.AssetClass == requestedClass);
        }

        if (assetId is { } requestedAssetId)
        {
            query = query.Where(t => t.AssetId == requestedAssetId);
        }

        return await query
            .OrderByDescending(t => t.TradeDate)
            .ThenByDescending(t => t.Id)
            .Select(t => new TransactionDto(
                t.Id,
                t.AssetId,
                t.Asset!.Symbol,
                t.Asset!.AssetClass,
                t.Type,
                t.TradeDate,
                t.Quantity,
                t.PricePerUnit,
                t.Fees,
                t.Currency,
                t.Notes))
            .ToListAsync(cancellationToken);
    }

    public Task<TransactionDto?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        db.Transactions
            .Where(t => t.Id == id)
            .Select(t => new TransactionDto(
                t.Id,
                t.AssetId,
                t.Asset!.Symbol,
                t.Asset!.AssetClass,
                t.Type,
                t.TradeDate,
                t.Quantity,
                t.PricePerUnit,
                t.Fees,
                t.Currency,
                t.Notes))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<ServiceResult<TransactionDto>> CreateAsync(
        CreateTransactionRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        var asset = await db.FindAssetAsync(request.AssetId, cancellationToken);
        if (asset is null)
        {
            errors["assetId"] = [$"Asset {request.AssetId} does not exist."];
        }

        ValidateCommon(request.Quantity, request.PricePerUnit, request.Fees, request.TradeDate, request.Currency, errors);

        if (errors.Count == 0 && request.Type == TransactionType.Sell)
        {
            var held = await GetHeldQuantityAsync(request.AssetId, excludeTransactionId: null, cancellationToken);
            if (request.Quantity > held)
            {
                errors["quantity"] = [$"Sell quantity {request.Quantity} exceeds the {held} units currently held."];
            }
        }

        if (errors.Count > 0)
        {
            return ServiceResult<TransactionDto>.Failure(ServiceError.Validation(errors));
        }

        var transaction = new Transaction
        {
            AssetId = request.AssetId,
            Type = request.Type,
            TradeDate = request.TradeDate,
            Quantity = request.Quantity,
            PricePerUnit = request.PricePerUnit,
            Fees = request.Fees,
            Currency = request.Currency,
            Notes = request.Notes,
        };

        db.AddTransaction(transaction);
        await db.SaveChangesAsync(cancellationToken);

        return ServiceResult<TransactionDto>.Success(ToDto(transaction, asset!));
    }

    public async Task<ServiceResult<TransactionDto>> UpdateAsync(
        int id, UpdateTransactionRequest request, CancellationToken cancellationToken)
    {
        var transaction = await db.FindTransactionAsync(id, cancellationToken);
        if (transaction is null)
        {
            return ServiceResult<TransactionDto>.Failure(ServiceError.NotFound());
        }

        var errors = new Dictionary<string, string[]>();

        var asset = await db.FindAssetAsync(request.AssetId, cancellationToken);
        if (asset is null)
        {
            errors["assetId"] = [$"Asset {request.AssetId} does not exist."];
        }

        ValidateCommon(request.Quantity, request.PricePerUnit, request.Fees, request.TradeDate, request.Currency, errors);

        if (errors.Count == 0 && request.Type == TransactionType.Sell)
        {
            var held = await GetHeldQuantityAsync(request.AssetId, excludeTransactionId: id, cancellationToken);
            if (request.Quantity > held)
            {
                errors["quantity"] = [$"Sell quantity {request.Quantity} exceeds the {held} units currently held."];
            }
        }

        if (errors.Count > 0)
        {
            return ServiceResult<TransactionDto>.Failure(ServiceError.Validation(errors));
        }

        transaction.AssetId = request.AssetId;
        transaction.Type = request.Type;
        transaction.TradeDate = request.TradeDate;
        transaction.Quantity = request.Quantity;
        transaction.PricePerUnit = request.PricePerUnit;
        transaction.Fees = request.Fees;
        transaction.Currency = request.Currency;
        transaction.Notes = request.Notes;

        await db.SaveChangesAsync(cancellationToken);

        return ServiceResult<TransactionDto>.Success(ToDto(transaction, asset!));
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        var transaction = await db.FindTransactionAsync(id, cancellationToken);
        if (transaction is null)
        {
            return false;
        }

        db.RemoveTransaction(transaction);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Units currently held for an asset: sum of buys minus sum of sells, optionally
    /// excluding one transaction (used when re-validating an edit against its own prior effect).</summary>
    private async Task<decimal> GetHeldQuantityAsync(
        int assetId, int? excludeTransactionId, CancellationToken cancellationToken)
    {
        var query = db.Transactions.Where(t => t.AssetId == assetId);

        if (excludeTransactionId is { } excludeId)
        {
            query = query.Where(t => t.Id != excludeId);
        }

        var bought = await query
            .Where(t => t.Type == TransactionType.Buy)
            .SumAsync(t => (decimal?)t.Quantity, cancellationToken) ?? 0m;

        var sold = await query
            .Where(t => t.Type == TransactionType.Sell)
            .SumAsync(t => (decimal?)t.Quantity, cancellationToken) ?? 0m;

        return bought - sold;
    }

    private void ValidateCommon(
        decimal quantity,
        decimal pricePerUnit,
        decimal fees,
        DateOnly tradeDate,
        string currency,
        Dictionary<string, string[]> errors)
    {
        if (quantity <= 0)
        {
            errors["quantity"] = ["Quantity must be positive."];
        }

        if (pricePerUnit < 0)
        {
            errors["pricePerUnit"] = ["Price per unit cannot be negative."];
        }

        if (fees < 0)
        {
            errors["fees"] = ["Fees cannot be negative."];
        }

        var today = ReportingClock.Today(timeProvider);
        if (tradeDate > today)
        {
            errors["tradeDate"] = ["Trade date cannot be in the future."];
        }

        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
        {
            errors["currency"] = ["Currency must be a 3-letter ISO 4217 code."];
        }
    }

    private static TransactionDto ToDto(Transaction t, Asset a) => new(
        t.Id,
        t.AssetId,
        a.Symbol,
        a.AssetClass,
        t.Type,
        t.TradeDate,
        t.Quantity,
        t.PricePerUnit,
        t.Fees,
        t.Currency,
        t.Notes);
}
