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
    public static decimal Resolve(IReadOnlyList<FxRate> ratesAscendingByDate, DateOnly date) =>
        ResolveDetailed(ratesAscendingByDate, date).Rate;

    /// <summary>
    /// Same resolution as <see cref="Resolve"/>, but also returns the origin date of the rate that
    /// was actually used. Ordinary callers only need <see cref="Resolve"/> — this exists for the
    /// zakat report, the first caller that must tell a genuine carry-<i>forward</i> (a weekend, a
    /// holiday — fine) apart from falling back to the earliest stored rate because <paramref
    /// name="date"/> precedes every row on file (not fine for a single headline figure — see
    /// zakat.md §5). Compare <see cref="FxRateResolution.ResolvedDate"/> against the requested date
    /// yourself, or use <see cref="FxRateResolution.CarriedBack"/>.
    /// </summary>
    public static FxRateResolution ResolveDetailed(IReadOnlyList<FxRate> ratesAscendingByDate, DateOnly date)
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

        var resolved = carriedForward ?? ratesAscendingByDate[0];
        return new FxRateResolution(resolved.Rate, resolved.Date, date);
    }
}

/// <summary>
/// The rate <see cref="FxRateResolver.ResolveDetailed"/> chose, plus enough of its own provenance
/// to tell a normal carry-forward apart from a before-the-data fallback. See
/// <see cref="FxRateResolver.ResolveDetailed"/>'s remarks.
/// </summary>
public sealed record FxRateResolution(decimal Rate, DateOnly ResolvedDate, DateOnly RequestedDate)
{
    /// <summary>
    /// True when the resolved rate is dated <i>after</i> the requested date — i.e.
    /// <see cref="FxRateResolver"/> found nothing at or before <see cref="RequestedDate"/> and fell
    /// back to the earliest rate on file rather than genuinely carrying one forward. Carrying a
    /// rate <i>forward</i> (the normal case, a weekend) leaves this false.
    /// </summary>
    public bool CarriedBack => ResolvedDate > RequestedDate;
}
