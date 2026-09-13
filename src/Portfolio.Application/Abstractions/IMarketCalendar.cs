using Portfolio.Domain.Enums;

namespace Portfolio.Application.Abstractions;

/// <summary>
/// Answers "is this exchange in a regular trading session right now?" so the refresh service
/// never spends a provider credit polling a market that is closed. DST-safe by construction:
/// implementations must convert the UTC instant into the exchange's local wall-clock time via
/// <see cref="TimeZoneInfo"/> rather than assuming a fixed UTC offset, because NYSE's own
/// UTC offset changes twice a year even though SGX's never does.
/// </summary>
public interface IMarketCalendar
{
    /// <summary>True if <paramref name="market"/> is in a regular trading session at
    /// <paramref name="instant"/> — not a weekend, not a holiday, and within trading hours
    /// (for SGX, outside the midday lunch break).</summary>
    bool IsOpen(Market market, DateTimeOffset instant);

    /// <summary>
    /// The exchange-local calendar date at <paramref name="instant"/> — the trading day a UTC
    /// instant falls on <i>from the exchange's point of view</i>, which is not the UTC date. An
    /// SGX close at 17:00 SGT is 09:00 UTC the same day, but an NYSE close at 16:00 ET is 20:00 or
    /// 21:00 UTC depending on DST, and a 23:30 UTC instant is already the next SGT day.
    ///
    /// <para>Used to decide whether a stored <c>PriceQuote</c> belongs to the current trading day
    /// or an earlier one (D4). This deliberately does <b>not</b> consult the holiday table: the
    /// whole point is to catch the days the table gets wrong.</para>
    /// </summary>
    DateOnly LocalDateOn(Market market, DateTimeOffset instant);

    /// <summary>
    /// The instant of the most recently <b>completed</b> regular session close for
    /// <paramref name="market"/> at or before <paramref name="instant"/> — walking back over
    /// weekends and holidays until it lands on an actual trading day. If <paramref name="instant"/>
    /// itself is at or after that day's close, the close returned is <i>today's own</i>; otherwise
    /// it walks back to the prior trading day's close.
    ///
    /// <para>Unlike <see cref="LocalDateOn"/>, this <b>does</b> consult the holiday table
    /// (<c>NyseHolidayCalendar</c> / <c>SgxHolidayCalendar</c>) — the two methods answer different
    /// questions. <see cref="LocalDateOn"/> exists specifically to catch the days the holiday table
    /// is wrong (D4), so it must never trust that table. This method instead answers "when did the
    /// close I should have on file get published?" for a scheduled backfill's due-ness check
    /// (D47) — a wrong answer here means re-fetching a close that was never actually published
    /// (harmless — Yahoo/Twelve Data just return the same data again) rather than a stale quote
    /// masquerading as current (D4's failure mode), so trusting the holiday table's incompleteness
    /// is the acceptable side to be wrong on for this method, unlike <see cref="LocalDateOn"/>.</para>
    /// </summary>
    DateTimeOffset LastSessionCloseAt(Market market, DateTimeOffset instant);

    /// <summary>
    /// The earliest instant at or after <paramref name="instant"/> at which <paramref name="market"/>
    /// is in a regular trading session — i.e. the smallest <c>t &gt;= instant</c> for which
    /// <see cref="IsOpen"/> would return true. Walks forward over weekends, the holiday table, and
    /// (for SGX) the midday lunch break, so it lands on the correct reopen in every case: NYSE's
    /// next calendar day's open, SGX's next calendar day's open, or SGX's same-day 13:00 SGT
    /// post-lunch reopen when <paramref name="instant"/> falls inside the lunch break itself.
    ///
    /// <para><b>If the market is already open at <paramref name="instant"/>, this returns
    /// <paramref name="instant"/> itself</b> — "the next instant the market is open" is trivially
    /// now, not tomorrow's open. Callers that only care about a currently-closed market (this
    /// interface's only consumer today, <c>PriceRefreshService</c>'s closed-market schedule cap)
    /// only ever call this after their own <see cref="IsOpen"/> check has already returned false,
    /// but the contract holds for any instant so it can never surprise a future caller.</para>
    ///
    /// <para>Exists to cap a closed market's next-scheduled-check time at the market's actual
    /// reopen, rather than a fixed interval that can land up to an hour past the open (D54). Reuses
    /// exactly the session/holiday data <see cref="IsOpen"/> and <see cref="LastSessionCloseAt"/>
    /// already encode — it must never drift from either.</para>
    /// </summary>
    DateTimeOffset NextOpenAt(Market market, DateTimeOffset instant);
}
