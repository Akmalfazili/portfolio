using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;

namespace Portfolio.Infrastructure.MarketData.Yahoo;

/// <summary>
/// Maps an <see cref="Asset"/> onto the symbol Yahoo Finance's chart endpoint expects for dividend
/// history — <b>explicitly</b>, never inferred from <see cref="Asset.QuoteProviderKind"/> or any
/// other field, per this project's "never infer routing" rule (see CLAUDE.md).
///
/// <para>Dividend history has a wider reach than the live-quote router: Yahoo is the dividend
/// source for <i>every</i> stock, including Twelve Data-routed US equities, because Twelve Data's
/// free tier gates fundamentals data and Yahoo is the only free, keyless option that has any. So
/// this resolver does not switch on <see cref="Asset.QuoteProviderKind"/> at all:</para>
///
/// <list type="bullet">
/// <item><description>Any <see cref="AssetClass.Stock"/> asset — whether
/// <see cref="QuoteProviderKind.TwelveData"/> (e.g. AAPL, MSFT) or
/// <see cref="QuoteProviderKind.Yahoo"/> (Z74) — resolves to its own
/// <see cref="Asset.ProviderSymbol"/> unchanged. For Yahoo-routed assets this is already Yahoo's
/// own form (e.g. "Z74.SI"). For Twelve Data-routed US equities, <see cref="Asset.ProviderSymbol"/>
/// is already the plain ticker (e.g. "AAPL") that Yahoo expects too — Twelve Data and Yahoo happen
/// to agree on US ticker spelling, so no translation is needed.</description></item>
/// <item><description>Any <see cref="AssetClass.Crypto"/> asset resolves to <c>null</c> — crypto
/// pays no dividends and is out of scope entirely.</description></item>
/// </list>
/// </summary>
public static class YahooDividendSymbolResolver
{
    public static string? Resolve(Asset asset) =>
        asset.AssetClass == AssetClass.Stock ? asset.ProviderSymbol : null;
}
