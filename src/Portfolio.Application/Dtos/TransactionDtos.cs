using Portfolio.Domain.Enums;

namespace Portfolio.Application.Dtos;

public sealed record TransactionDto(
    int Id,
    int AssetId,
    string AssetSymbol,
    AssetClass AssetClass,
    TransactionType Type,
    DateOnly TradeDate,
    decimal Quantity,
    decimal PricePerUnit,
    decimal Fees,
    string Currency,
    string? Notes);

public sealed record CreateTransactionRequest(
    int AssetId,
    TransactionType Type,
    DateOnly TradeDate,
    decimal Quantity,
    decimal PricePerUnit,
    decimal Fees,
    string Currency,
    string? Notes);

public sealed record UpdateTransactionRequest(
    int AssetId,
    TransactionType Type,
    DateOnly TradeDate,
    decimal Quantity,
    decimal PricePerUnit,
    decimal Fees,
    string Currency,
    string? Notes);
