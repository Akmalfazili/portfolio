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

/// <summary>Full replace of an existing asset, including <see cref="IsActive"/> — the only way to
/// deactivate one (D23/Phase 12). Same D23 provider-routing coherence rules as
/// <see cref="CreateAssetRequest"/> apply on every update, not only on create.</summary>
public sealed record UpdateAssetRequest(
    string Symbol,
    string Name,
    AssetClass AssetClass,
    string? Exchange,
    string Currency,
    QuoteProviderKind QuoteProviderKind,
    string? ProviderSymbol,
    string? ProviderCoinId,
    bool IsActive);
