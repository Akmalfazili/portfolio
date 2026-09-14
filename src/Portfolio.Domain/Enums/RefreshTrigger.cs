namespace Portfolio.Domain.Enums;

/// <summary>
/// How a <see cref="Entities.RefreshRun"/> was initiated. <see cref="Scheduled"/>/<see cref="Manual"/>
/// are written by <c>PriceRefreshService</c> (live quotes, <c>PriceQuote</c>);
/// <see cref="BackfillScheduled"/>/<see cref="BackfillManual"/> are written by
/// <c>PriceBackfillService</c> (daily closes, <c>PriceHistory</c>) — see D12;
/// <see cref="DividendBackfillScheduled"/>/<see cref="DividendBackfillManual"/> are written by
/// <c>DividendBackfillService</c> (<c>DividendEvent</c>); <see cref="BackfillCatchUp"/>/
/// <see cref="DividendBackfillCatchUp"/> are written by the same two services when narrowed to
/// exactly what the refresh-button catch-up feature found missing (see
/// <c>IRefreshCatchUpService</c>) — a click that costs zero provider calls when nothing is missing,
/// distinguished from an ordinary scheduled/manual/retry run so the audit trail (and
/// <c>PriceRefreshStatusEnricher</c>'s per-market close status) can tell a catch-up attempt apart
/// from the others without conflating them. Kept as one enum so every audit trail shares the
/// <see cref="Entities.RefreshRun"/> table rather than each growing its own, but a query over one
/// group must never accidentally include another. Appended only — never renumber an existing
/// member, the column stores ints.
/// </summary>
public enum RefreshTrigger
{
    Scheduled = 0,
    Manual = 1,
    BackfillScheduled = 2,
    BackfillManual = 3,
    DividendBackfillScheduled = 4,
    DividendBackfillManual = 5,
    BackfillCatchUp = 6,
    DividendBackfillCatchUp = 7,
}
