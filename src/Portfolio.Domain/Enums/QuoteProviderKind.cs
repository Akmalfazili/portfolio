namespace Portfolio.Domain.Enums;

/// <summary>
/// The market-data provider responsible for quoting an <see cref="Entities.Asset"/>.
/// <see cref="AssetClass"/> alone is not enough to route a request — stocks split across two
/// providers (Twelve Data for US equities, Yahoo Finance for SGX) because Twelve Data's free
/// tier cannot serve <c>Z74</c>. This field is the explicit dispatch key a provider router uses;
/// it is set once per asset (via seed data or when a new asset is added) rather than inferred
/// from currency, exchange, or symbol shape.
/// </summary>
public enum QuoteProviderKind
{
    TwelveData = 0,
    Yahoo = 1,
    CoinGecko = 2,
}
