namespace Portfolio.Application.Abstractions;

/// <summary>
/// The equity markets the refresh service polls. Crypto has no market — it trades 24/7 and is
/// never gated by <see cref="IMarketCalendar"/>.
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
}
