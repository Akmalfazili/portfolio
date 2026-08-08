using Portfolio.Application.Abstractions;
using Portfolio.Application.Dtos;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services.Calendar;

/// <summary>
/// D4 — is a quote from the trading session happening right now, or from an earlier one?
///
/// <para>Answering "live" purely because a quote exists was the defect. There is one
/// <c>PriceQuote</c> row per asset, overwritten in place, carrying the <i>provider's</i> own
/// timestamp — so a quote fetched on a day the exchange never traded is byte-for-byte
/// indistinguishable from a fresh one unless something checks its date.</para>
///
/// <para><b>The SGX case this was written for.</b> <see cref="SgxHolidayCalendar"/> models only
/// Gregorian fixed-date holidays; Chinese New Year, Vesak, Hari Raya and Deepavali are not in it
/// and, by decision, never will be. On those days <c>IsOpen</c> wrongly returns true, the refresh
/// service polls Yahoo, and Yahoo answers with the <i>previous</i> session's close — correctly
/// stamped with that session's <c>regularMarketTime</c>. Z74 then read as a live price on a day it
/// had not traded.</para>
///
/// <para><b>Why two conditions, not one.</b> They cover each other's blind spot, which is the
/// whole design:</para>
/// <list type="bullet">
/// <item>The <b>date</b> check is what fixes D4. It compares the quote's own timestamp against now
/// in exchange-local terms and never consults the holiday table, so it stays correct on precisely
/// the days that table is wrong. A calendar that has never heard of Deepavali still cannot make a
/// Friday quote look like a Tuesday price.</item>
/// <item>The <b>open</b> check catches the hours after a session ends on a day the calendar
/// <i>does</i> know about — 18:00 ET on a normal Friday, where the quote's local date is still
/// "today" but the market has been shut for two hours. NYSE's calendar is fully rule-based, so this
/// half is trustworthy where it applies.</item>
/// </list>
///
/// <para>Note this is a <i>session</i> rule, not an age threshold. A quote taken at the opening
/// bell is still live six hours later in the same session, which a naive "must be minutes old"
/// staleness check would wrongly demote on any thinly-traded symbol.</para>
///
/// <para><b>Crypto is exempt, and that is not an oversight.</b> CoinGecko has no market, no session
/// boundary and no calendar gate, and is polled every two minutes, so "which session does this
/// belong to?" has no meaning for it — see <see cref="ProviderMarkets"/>. The residual: if CoinGecko
/// itself goes dark for a long stretch, a stale crypto quote still reports as live. That is a
/// provider-outage signal rather than a market-hours one, and <see cref="PriceSource.Close"/> would
/// be the wrong word for it since crypto stores no closes at all.</para>
/// </summary>
public static class QuoteFreshness
{
    /// <summary>
    /// Classifies a stored or freshly fetched quote. Returns <see cref="PriceSource.Live"/> only
    /// for a quote belonging to the session in progress; otherwise <see cref="PriceSource.Close"/>,
    /// meaning "a real price, from a session that has already ended" — the caller is expected to
    /// carry <paramref name="quoteAsOf"/> through to the user rather than substituting "now".
    /// </summary>
    public static PriceSource Classify(
        IMarketCalendar calendar,
        QuoteProviderKind provider,
        DateTimeOffset quoteAsOf,
        DateTimeOffset now)
    {
        if (ProviderMarkets.For(provider) is not { } market)
        {
            return PriceSource.Live;
        }

        var sameSession = calendar.LocalDateOn(market, quoteAsOf) == calendar.LocalDateOn(market, now)
            && calendar.IsOpen(market, now);

        return sameSession ? PriceSource.Live : PriceSource.Close;
    }
}
