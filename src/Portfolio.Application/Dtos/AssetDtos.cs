using Portfolio.Domain.Enums;

namespace Portfolio.Application.Dtos;

public sealed record AssetDto(
    int Id,
    string Symbol,
    string Name,
    AssetClass AssetClass,
    string? Exchange,
    string Currency,
    QuoteProviderKind QuoteProviderKind,
    string? ProviderSymbol,
    string? ProviderCoinId,
    bool IsActive);

public sealed record CreateAssetRequest(
    string Symbol,
    string Name,
    AssetClass AssetClass,
    string? Exchange,
    string Currency,
    QuoteProviderKind QuoteProviderKind,
    string? ProviderSymbol,
    string? ProviderCoinId);
