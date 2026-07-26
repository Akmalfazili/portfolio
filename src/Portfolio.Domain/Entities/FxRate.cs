namespace Portfolio.Domain.Entities;

/// <summary>
/// A daily FX spot rate. Unique per (Date, Base, Quote). Historical reporting must use the
/// rate for the relevant date, never today's rate.
/// </summary>
public class FxRate
{
    public int Id { get; set; }

    public DateOnly Date { get; set; }

    /// <summary>ISO 4217 base currency, e.g. "USD".</summary>
    public required string Base { get; set; }

    /// <summary>ISO 4217 quote currency, e.g. "SGD".</summary>
    public required string Quote { get; set; }

    /// <summary>Units of <see cref="Quote"/> per one unit of <see cref="Base"/>.</summary>
    public decimal Rate { get; set; }
}
