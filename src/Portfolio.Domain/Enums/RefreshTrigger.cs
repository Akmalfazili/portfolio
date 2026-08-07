namespace Portfolio.Domain.Enums;

/// <summary>
/// How a <see cref="Entities.RefreshRun"/> was initiated. <see cref="Scheduled"/>/<see cref="Manual"/>
/// are written by <c>PriceRefreshService</c> (live quotes, <c>PriceQuote</c>);
/// <see cref="BackfillScheduled"/>/<see cref="BackfillManual"/> are written by
/// <c>PriceBackfillService</c> (daily closes, <c>PriceHistory</c>) — see D12. Kept as one enum so
/// both audit trails share the <see cref="Entities.RefreshRun"/> table rather than each growing
/// its own, but a query over one pair must never accidentally include the other.
/// </summary>
public enum RefreshTrigger
{
    Scheduled = 0,
    Manual = 1,
    BackfillScheduled = 2,
    BackfillManual = 3,
}
