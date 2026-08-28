namespace Portfolio.Application.Dtos;

/// <summary>
/// Outcome of fetching one asset's latest quote as part of a (possibly batched) provider call.
/// Modelled as a per-asset result rather than throwing so that one bad symbol inside a Twelve
/// Data batch (returned as a nested <c>status: "error"</c> object) cannot fail quotes for the
/// sibling symbols that succeeded.
/// </summary>
public sealed record QuoteFetchResult(
    int AssetId,
    bool Success,
    decimal? Price,
    string? Currency,
    DateTimeOffset? AsOf,
    string? Error);

/// <summary>One daily close, in the asset's native currency.</summary>
public sealed record PriceHistoryPoint(DateOnly Date, decimal Close, string Currency);

/// <summary>
/// Outcome of one <c>IQuoteProvider.GetHistoryAsync</c> call. Deliberately distinguishes three
/// cases a caller must be able to tell apart, rather than collapsing them all into an empty
/// list: the call succeeded and covers the full requested range (<see cref="Success"/> true,
/// <see cref="Truncated"/> false); the call succeeded but a provider-side window limit forced a
/// later start than requested (<see cref="Truncated"/> true, <see cref="EffectiveFrom"/> later
/// than <see cref="RequestedFrom"/> — e.g. CoinGecko's keyless API refuses ranges older than 365
/// days); or the call actually failed (<see cref="Success"/> false, <see cref="Error"/> set).
/// A silent empty list for all three is exactly what let a truncated CoinGecko backfill look
/// identical to a transient network failure.
/// </summary>
public sealed record HistoryFetchResult(
    IReadOnlyList<PriceHistoryPoint> Points,
    DateOnly RequestedFrom,
    DateOnly EffectiveFrom,
    bool Truncated,
    bool Success,
    string? Error)
{
    public static HistoryFetchResult Ok(IReadOnlyList<PriceHistoryPoint> points, DateOnly from) =>
        new(points, from, from, false, true, null);

    public static HistoryFetchResult Failed(DateOnly from, string error) =>
        new([], from, from, false, false, error);
}

/// <summary>A spot FX rate, quote currency units per one unit of base currency.</summary>
public sealed record FxSpotResult(decimal Rate, DateTimeOffset AsOf);

/// <summary>One daily FX rate.</summary>
public sealed record FxRatePoint(DateOnly Date, decimal Rate);

/// <summary>
/// Outcome of one <c>IFxRateProvider.GetHistoryAsync</c> call. Mirrors
/// <see cref="HistoryFetchResult"/>'s <see cref="Success"/>/<see cref="Error"/> split for the same
/// reason: a transient provider failure (e.g. a 429) must not collapse into the same empty list as
/// "no rates in this range". FX has no per-provider window-truncation quirk to track today, so
/// unlike <see cref="HistoryFetchResult"/> this does not carry a <c>Truncated</c> flag - add one if
/// that ever changes.
/// </summary>
public sealed record FxHistoryFetchResult(
    IReadOnlyList<FxRatePoint> Points,
    bool Success,
    string? Error)
{
    public static FxHistoryFetchResult Ok(IReadOnlyList<FxRatePoint> points) => new(points, true, null);

    public static FxHistoryFetchResult Failed(string error) => new([], false, error);
}

/// <summary>One dividend ex-date event, in the asset's native currency.</summary>
public sealed record DividendPoint(DateOnly ExDate, decimal AmountPerShare, string Currency);

/// <summary>
/// Outcome of one <c>IDividendProvider.GetDividendHistoryAsync</c> call. Mirrors
/// <see cref="HistoryFetchResult"/>/<see cref="FxHistoryFetchResult"/>'s <see cref="Success"/>/
/// <see cref="Error"/> split for the same reason: a transient Yahoo failure must not collapse into
/// the same empty list as "this stock genuinely paid no dividends in the requested range" — the
/// exact ambiguity that would make a stock look real-zero when the fetch simply never landed.
/// </summary>
public sealed record DividendHistoryFetchResult(
    IReadOnlyList<DividendPoint> Points,
    bool Success,
    string? Error)
{
    public static DividendHistoryFetchResult Ok(IReadOnlyList<DividendPoint> points) => new(points, true, null);

    public static DividendHistoryFetchResult Failed(string error) => new([], false, error);
}
