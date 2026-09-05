namespace Portfolio.Application.Services.Calculators;

/// <summary>
/// The single definition of "today" for user-facing reporting dates — Singapore time (UTC+8),
/// not UTC. The frontend already works in browser-local dates throughout
/// (<c>shared/util/local-date.ts</c>'s <c>todayDateOnly()</c> bounds every datepicker at local
/// midnight), which for this single, Singapore-based user IS SGT. Deriving "today" from UTC on
/// the backend meant the UI and the API disagreed about the calendar day for the eight hours
/// between SGT midnight and UTC midnight (00:00–08:00 SGT) — a trade dated "today" in the
/// browser could be rejected as "in the future" by the API, and a report's reference date could
/// read yesterday while the datepicker above it already said today.
///
/// A fixed <see cref="TimeSpan"/> offset is used deliberately, in preference to
/// <c>TimeZoneInfo.FindSystemTimeZoneById("Asia/Singapore")</c> (the mechanism
/// <c>MarketCalendar.ZoneFor</c> uses for NYSE/SGX): Singapore has observed no DST since 1935, so
/// there is no seasonal shift for a fixed offset to get wrong, and unlike a named-zone lookup a
/// fixed offset cannot fail under <c>DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true</c> — a live
/// hazard in this project's chiselled container images (see <c>CLAUDE.md</c> → Containers).
/// <see cref="MarketCalendar"/> keeps <see cref="TimeZoneInfo"/> because NYSE's own DST transitions
/// change the wall-clock gap between markets twice a year and must be modelled; nothing about
/// zakat, dividends, transactions or holdings needs that.
///
/// <para><b>This is reporting-only. Two other things in this codebase intentionally still key on
/// UTC or a provider's own timestamp, and must not be "fixed" to use this helper</b>:</para>
///
/// <list type="bullet">
/// <item><description><b><c>TwelveDataCreditThrottle</c>'s daily ledger</b> stays on UTC because
/// it reconciles against Twelve Data's own <c>/api_usage</c> counter, which itself resets at UTC
/// midnight. Keying the local ledger row on SGT would roll it over eight hours early and
/// reconcile a new day from a counter still holding yesterday's spend, silently disabling the
/// D39/D45 self-correction.</description></item>
/// <item><description><b>Provider close timestamps becoming <c>PriceHistory.Date</c></b> (Yahoo,
/// CoinGecko) stay on the provider's own axis. NYSE closes ~20:00 UTC, which is ~04:00 SGT the
/// <i>next calendar day</i> — converting to SGT here would stamp every US close with the
/// following date and corrupt the price-history axis <c>CloseAsOf</c> reads.</description></item>
/// </list>
/// </summary>
public static class ReportingClock
{
    private static readonly TimeSpan SingaporeOffset = TimeSpan.FromHours(8);

    /// <summary>Today's date in Singapore time, derived from <paramref name="timeProvider"/>'s
    /// current UTC instant via a fixed +8 offset. Use this for every user-facing "today" —
    /// future-date validation, report reference dates, trailing-window calculations, and
    /// once-per-day background gates that are meant to line up with the Singapore calendar day —
    /// never for the UTC-keyed credit ledger or a provider timestamp (see the class remarks).</summary>
    public static DateOnly Today(TimeProvider timeProvider) => DateFor(timeProvider.GetUtcNow());

    /// <summary>The Singapore calendar date a given instant falls on, via the same fixed +8
    /// offset as <see cref="Today"/>. For comparing a stored <see cref="DateTimeOffset"/> (e.g. a
    /// <c>RefreshRun.StartedAt</c>) against <see cref="Today"/> on the same axis — a daily-run gate
    /// must convert both sides of its comparison together, or the two dates it compares are not
    /// actually on the same clock.</summary>
    public static DateOnly DateFor(DateTimeOffset instant) =>
        DateOnly.FromDateTime((instant + SingaporeOffset).UtcDateTime);
}
