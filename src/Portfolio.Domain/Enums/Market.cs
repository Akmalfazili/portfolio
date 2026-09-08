namespace Portfolio.Domain.Enums;

/// <summary>
/// The equity markets the refresh service polls. Crypto has no market — it trades 24/7 and is
/// never gated by <c>IMarketCalendar</c> (<c>Portfolio.Application</c>).
///
/// <para>Lives in <c>Portfolio.Domain</c>, not <c>Portfolio.Application</c>, even though
/// <c>IMarketCalendar</c> is the type that mostly consumes it: <see cref="Entities.RefreshRun.Market"/>
/// (D47) needs this enum on a Domain entity, and Domain references nothing, so the enum an entity
/// property uses must live at or below Domain in the dependency graph. <c>IMarketCalendar</c> and
/// <c>ProviderMarkets</c> reference this same definition rather than each keeping a private copy —
/// see D7, which is exactly the two-copies-drift bug a duplicate enum would repeat.</para>
/// </summary>
public enum Market
{
    /// <summary>NYSE/NASDAQ regular session — covers every US-listed equity, quoted by Twelve
    /// Data. Time zone <c>America/New_York</c>.</summary>
    Nyse,

    /// <summary>Singapore Exchange — covers Z74, quoted by Yahoo Finance. Time zone
    /// <c>Asia/Singapore</c>.</summary>
    Sgx,
}
