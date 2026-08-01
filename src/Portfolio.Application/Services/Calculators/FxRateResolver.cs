using Portfolio.Domain.Entities;

namespace Portfolio.Application.Services.Calculators;

/// <summary>
/// Resolves the USD conversion rate for a currency on a given date via carry-forward — the most
/// recently stored rate at or before that date — because <see cref="FxRate"/> rows only exist for
/// days a provider actually quoted (weekdays), while transactions and price history can fall on
/// any calendar day (a Saturday trade date, a Monday close needing Friday's or an even older
/// rate over a long weekend). Carry-forward is the chosen behaviour for a date with no stored
/// rate — see the Phase 6 decision in tracker.md — rather than, say, interpolating or failing the
/// whole calculation over one missing day.
///
/// If a date precedes every stored rate (only possible for the first sliver of history before a
/// currency's backfill has ever run), the earliest stored rate is used instead of throwing, so a
/// single missing early rate does not blank out an otherwise-computable series. Callers needing to
/// tell "genuine carry-forward" apart from "before-the-data fallback" can compare the resolved
/// rate's origin date to the requested date themselves; today's callers do not need to.
/// </summary>
public static class FxRateResolver
{
    /// <summary>
    /// <paramref name="ratesAscendingByDate"/> must already be sorted ascending by
    /// <see cref="FxRate.Date"/> and must all share the same currency pair — the caller loads one
    /// list per currency once and reuses it, rather than this method re-filtering per call.
    /// </summary>
    public static decimal Resolve(IReadOnlyList<FxRate> ratesAscendingByDate, DateOnly date)
    {
        if (ratesAscendingByDate.Count == 0)
        {
            throw new InvalidOperationException(
                "No FX rates available to resolve a conversion. Run the price backfill for this currency first.");
        }

        FxRate? carriedForward = null;
        foreach (var rate in ratesAscendingByDate)
        {
            if (rate.Date > date)
            {
                break;
            }

            carriedForward = rate;
        }

        return (carriedForward ?? ratesAscendingByDate[0]).Rate;
    }
}
