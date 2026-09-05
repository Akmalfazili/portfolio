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
    /// Calendar month of this company's financial year end, 1-12; null means not configured. Always
    /// null for <see cref="Enums.AssetClass.Crypto"/>. Feeds the zakat-on-shares report — see
    /// zakat.md — nowhere else. Either this and <see cref="FiscalYearEndDay"/> are both set or both
    /// null; enforced by <c>AssetService</c>, not the database.
    /// </summary>
    int? FiscalYearEndMonth,

    /// <summary>Day of month of the financial year end, 1-31 (29 is allowed for February — it
    /// clamps to 28 in a non-leap year at the point the zakat report resolves it, never rejected
    /// here). See <see cref="FiscalYearEndMonth"/>.</summary>
    int? FiscalYearEndDay,

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
    bool HasEverBeenPriced,

    /// <summary>
    /// True when this asset's <see cref="QuoteProviderKind"/> has recorded at least one genuinely
    /// successful refresh cycle anywhere in this database — i.e. <c>SourceRefreshStates.LastSuccessAt</c>
    /// is non-null for that provider, across every asset that shares it, not only this one.
    ///
    /// <para>Found live on a freshly created container database (2026-08-09): every seeded asset's
    /// <see cref="CreatedAt"/> is a static <c>HasData</c> seed value, so on a database created
    /// after that date every asset reads as having sat unpriced for however many days have passed
    /// since the constant — <see cref="HasEverBeenPriced"/> alone escalated the D27 warning on
    /// assets whose identifiers were entirely correct, purely because nothing in this database had
    /// ever priced anything yet.</para>
    ///
    /// <para>This is the gate that gives the D27 escalation actual evidentiary weight: if this
    /// provider has never once worked in this database, this asset's silence carries no
    /// information about its identifier — it is exactly as explained by "the provider has never
    /// run" as by "the identifier is wrong", so there is nothing to escalate on. A caller should
    /// suppress the escalated ("check this identifier") warning entirely when this is false, and
    /// keep only the calm "no price yet" state, regardless of how many days
    /// <see cref="HasEverBeenPriced"/> has been false. Once this flips true, the existing
    /// per-provider day threshold becomes meaningful again, because a sibling asset on the same
    /// provider proves the pipe genuinely works.</para>
    /// </summary>
    bool ProviderHasEverSucceeded);

public sealed record CreateAssetRequest(
    string Symbol,
    string Name,
    AssetClass AssetClass,
    string? Exchange,
    string Currency,
    QuoteProviderKind QuoteProviderKind,
    string? ProviderSymbol,
    string? ProviderCoinId,

    /// <summary>See <see cref="AssetDto.FiscalYearEndMonth"/>. Both null (not configured, the only
    /// valid state for a <see cref="AssetClass.Crypto"/> request) or both set — validated in
    /// <c>AssetService</c>.</summary>
    int? FiscalYearEndMonth = null,
    int? FiscalYearEndDay = null);

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
    bool IsActive,

    /// <summary>See <see cref="CreateAssetRequest.FiscalYearEndMonth"/>.</summary>
    int? FiscalYearEndMonth = null,
    int? FiscalYearEndDay = null);
