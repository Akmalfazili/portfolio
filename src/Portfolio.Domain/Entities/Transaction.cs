using Portfolio.Domain.Enums;

namespace Portfolio.Domain.Entities;

/// <summary>
/// A single buy or sell fill for an <see cref="Asset"/>. Quantities support fractional units
/// (crypto) and fees are capitalised into cost basis on buys.
/// </summary>
public class Transaction
{
    public int Id { get; set; }

    public int AssetId { get; set; }

    public Asset? Asset { get; set; }

    public TransactionType Type { get; set; }

    /// <summary>The calendar date the trade executed, in the asset's local market — never a timestamp.</summary>
    public DateOnly TradeDate { get; set; }

    /// <summary>Units traded. Supports fractional crypto quantities down to 10 decimal places.</summary>
    public decimal Quantity { get; set; }

    /// <summary>Price per unit in <see cref="Currency"/>.</summary>
    public decimal PricePerUnit { get; set; }

    /// <summary>Transaction fees/commission, in <see cref="Currency"/>. Capitalised into cost basis on buys.</summary>
    public decimal Fees { get; set; }

    /// <summary>ISO 4217 currency this transaction was executed in.</summary>
    public required string Currency { get; set; }

    public string? Notes { get; set; }
}
