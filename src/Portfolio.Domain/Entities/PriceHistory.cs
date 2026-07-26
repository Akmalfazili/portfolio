namespace Portfolio.Domain.Entities;

/// <summary>
/// A daily closing price for an asset. Unique per (AssetId, Date) — used to build historical
/// cost-vs-market-value series without repeated provider calls.
/// </summary>
public class PriceHistory
{
    public int Id { get; set; }

    public int AssetId { get; set; }

    public Asset? Asset { get; set; }

    public DateOnly Date { get; set; }

    /// <summary>Closing price in <see cref="Currency"/>.</summary>
    public decimal Close { get; set; }

    /// <summary>ISO 4217 currency the close was quoted in.</summary>
    public required string Currency { get; set; }
}
