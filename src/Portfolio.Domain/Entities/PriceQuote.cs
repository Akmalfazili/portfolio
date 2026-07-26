namespace Portfolio.Domain.Entities;

/// <summary>
/// The latest known quote for an asset. One row per asset, overwritten on every refresh.
/// </summary>
public class PriceQuote
{
    public int Id { get; set; }

    public int AssetId { get; set; }

    public Asset? Asset { get; set; }

    /// <summary>Latest traded price in <see cref="Currency"/>.</summary>
    public decimal Price { get; set; }

    /// <summary>ISO 4217 currency the price was quoted in.</summary>
    public required string Currency { get; set; }

    /// <summary>UTC instant the quote was captured from the provider.</summary>
    public DateTimeOffset AsOf { get; set; }
}
