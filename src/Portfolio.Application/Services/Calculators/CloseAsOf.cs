using Portfolio.Domain.Entities;

namespace Portfolio.Application.Services.Calculators;

/// <summary>
/// Resolves the stored daily close at or before a given date via carry-forward — the same
/// mechanism as <see cref="FxRateResolver"/>, because a fiscal year end can land on a weekend, a
/// public holiday or any other exchange closure, so "the row for that exact date" would come back
/// empty far too often to be useful.
///
/// <para><b>The one deliberate difference from <see cref="FxRateResolver"/>: no fallback to the
/// earliest close on file.</b> If nothing exists at or before the requested date, this returns
/// <c>null</c> rather than reaching for the oldest price in the table — a wrong-looking-right
/// number is worse here than an explicit, named failure (<c>ZakatAssetStatus.NoCloseOnOrBeforeFiscalYearEnd</c>
/// in the zakat report). Do not "fix" this by copying <see cref="FxRateResolver"/>'s
/// before-the-data fallback in — that behaviour was a deliberate choice for a daily FX series, not
/// a universal default.</para>
/// </summary>
public static class CloseAsOf
{
    /// <summary>
    /// <paramref name="historyAscendingByDate"/> must already be sorted ascending by
    /// <see cref="PriceHistory.Date"/> and must all belong to one asset.
    /// </summary>
    public static PriceHistory? Resolve(IReadOnlyList<PriceHistory> historyAscendingByDate, DateOnly date)
    {
        PriceHistory? carriedForward = null;

        foreach (var row in historyAscendingByDate)
        {
            if (row.Date > date)
            {
                break;
            }

            carriedForward = row;
        }

        return carriedForward;
    }
}
