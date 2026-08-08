using Portfolio.Domain.Enums;

namespace Portfolio.Application.Dtos;

/// <summary>
/// One asset. <see cref="CreatedAt"/> and <see cref="HasEverBeenPriced"/> together are D27's
/// mitigation — see <see cref="HasEverBeenPriced"/>.
/// </summary>
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
    bool IsActive,
    DateTimeOffset CreatedAt,

    /// <summary>
    /// D27 — false when this asset has never had a single price recorded, from any source: no
    /// <c>PriceQuote</c> and no <c>PriceHistory</c> row exists for it.
    ///
    /// <para>D23 rejects a <i>malformed</i> record (a Twelve Data asset with no
    /// <c>ProviderSymbol</c>). It cannot cheaply reject a <b>well-formed but wrong</b> one —
    /// <c>APPL</c> for <c>AAPL</c>, or a CoinGecko id that does not exist — because verifying it
    /// means calling the provider, which spends a credit per creation and raises its own question
    /// of what to do when the provider is merely down. Such an asset is accepted, then renders
    /// "Awaiting price" forever with nothing distinguishing it from a closed market.</para>
    ///
    /// <para>This flag costs no provider call. Paired with <see cref="CreatedAt"/> it lets a
    /// caller say how long the silence has lasted, which is what separates the two cases: minutes
    /// is normal (no refresh cycle has run yet), days means the identifier is wrong. It does not
    /// <i>prove</i> a typo — a delisted symbol looks the same — so it is a hint pointing at the
    /// record, never an assertion that the record is invalid.</para>
    /// </summary>
    bool HasEverBeenPriced);

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
