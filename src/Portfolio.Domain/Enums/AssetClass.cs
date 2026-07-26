namespace Portfolio.Domain.Enums;

/// <summary>
/// Segregates stocks from crypto end to end. Every portfolio-level query filters on this —
/// stocks and crypto never aggregate together.
/// </summary>
public enum AssetClass
{
    Stock = 0,
    Crypto = 1,
}
