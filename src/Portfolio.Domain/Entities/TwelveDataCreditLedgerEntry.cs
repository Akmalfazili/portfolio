namespace Portfolio.Domain.Entities;

/// <summary>
/// Total Twelve Data credits actually spent on one UTC calendar day — one row per day, upserted
/// in place as credits are spent. Persisted (rather than tracked only in process memory) so the
/// 800-credit/day budget is enforced from real recorded spend and survives an API restart; an
/// in-memory-only counter reset to zero on every deploy is exactly the kind of assumption that let
/// D38 (a batch that always 429s) go unnoticed for 11 days.
/// </summary>
public class TwelveDataCreditLedgerEntry
{
    /// <summary>UTC calendar day this row accounts for. Primary key — never generated.</summary>
    public DateOnly Date { get; set; }

    public int CreditsUsed { get; set; }
}
