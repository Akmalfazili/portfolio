namespace Portfolio.Domain.Entities;

/// <summary>
/// A record of zakat actually paid — the one part of the zakat feature that is a recorded fact
/// rather than a number derived from market data on read. Entered by hand; never written by the
/// zakat report itself.
///
/// <para><b>Deliberately hangs off nothing.</b> No foreign key to <see cref="Asset"/>, no stored
/// "computed amount", no covering-period field. The report may display the computed figure from
/// the zakat calculation beside this history, but must never write into this table — the same
/// rule as "a close is never written into <see cref="PriceQuote"/>": a derived number persisted as
/// a recorded fact becomes indistinguishable from the real thing a year later, and the whole point
/// of this table is that the amount paid IS the real thing. Deleting an asset must never touch
/// this table — see <c>ZakatPaymentSurvivesAssetDeletionTests</c>.</para>
/// </summary>
public class ZakatPayment
{
    public int Id { get; set; }

    /// <summary>The date the payment was made. Never in the future — enforced in <c>ZakatService</c>.</summary>
    public DateOnly PaidOn { get; set; }

    /// <summary>Amount actually paid, in SGD. Entered by hand — never derived from the computed
    /// zakat report. Must be greater than zero — enforced in <c>ZakatService</c>.</summary>
    public decimal AmountSgd { get; set; }
}
