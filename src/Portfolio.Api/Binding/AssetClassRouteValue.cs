using Portfolio.Domain.Enums;

namespace Portfolio.Api.Binding;

/// <summary>
/// Case-insensitive route/query binder for <see cref="AssetClass"/> — see D11 in tracker.md.
/// ASP.NET Core minimal APIs bind an enum parameter straight off the request via
/// <c>Enum.TryParse</c> with <c>ignoreCase: false</c>, which is why
/// <c>/api/portfolio/stock/summary</c> 400s while only <c>/api/portfolio/Stock/summary</c> binds —
/// route path *literals* are matched case-insensitively by the router, but the <c>{assetClass}</c>
/// placeholder's *value* is not, and neither is the <c>?assetClass=</c> query string on
/// <c>/api/assets</c> and <c>/api/transactions</c>. <see cref="AssetClass"/> itself is a plain
/// enum and cannot carry a custom <c>TryParse</c>, so this wrapper implements
/// <see cref="IParsable{TSelf}"/> — which minimal APIs bind natively, including through
/// <see cref="Nullable{T}"/> for the optional query-string case — and is used at every route and
/// query call site that accepts an asset class, never just one, so the field is not encoded two
/// different ways (see D7).
/// </summary>
public readonly struct AssetClassRouteValue(AssetClass value) : IParsable<AssetClassRouteValue>
{
    public AssetClass Value { get; } = value;

    public static AssetClassRouteValue Parse(string s, IFormatProvider? provider) =>
        TryParse(s, provider, out var result)
            ? result
            : throw new FormatException($"'{s}' is not a valid asset class.");

    public static bool TryParse(string? s, IFormatProvider? provider, out AssetClassRouteValue result)
    {
        if (Enum.TryParse<AssetClass>(s, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            result = new AssetClassRouteValue(parsed);
            return true;
        }

        result = default;
        return false;
    }

    public static implicit operator AssetClass(AssetClassRouteValue value) => value.Value;
}
