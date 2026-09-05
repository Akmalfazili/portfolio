using FluentAssertions;
using Portfolio.Application.Services.Calculators;
using Portfolio.Domain.Entities;

namespace Portfolio.UnitTests.Services.Calculators;

/// <summary>
/// <see cref="CloseAsOf"/> — carry-forward across a gap in the trading calendar, and the one
/// deliberate difference from <see cref="FxRateResolver"/>: no fallback to the earliest close on
/// file when nothing exists at or before the requested date.
/// </summary>
public sealed class CloseAsOfTests
{
    // Friday close, then the next stored close is the following Monday — a weekend gap.
    private static readonly PriceHistory Friday = new() { Date = new DateOnly(2026, 1, 2), Close = 100m, Currency = "USD" };
    private static readonly PriceHistory Monday = new() { Date = new DateOnly(2026, 1, 5), Close = 102m, Currency = "USD" };

    private static readonly IReadOnlyList<PriceHistory> History = [Friday, Monday];

    [Fact]
    public void Resolve_ExactDateMatch_ReturnsThatRow()
    {
        CloseAsOf.Resolve(History, new DateOnly(2026, 1, 2))!.Close.Should().Be(100m);
    }

    [Fact]
    public void Resolve_WeekendGap_CarriesForwardFridaysClose()
    {
        // Saturday, between the Friday and Monday closes — carries Friday's close forward.
        CloseAsOf.Resolve(History, new DateOnly(2026, 1, 3))!.Close.Should().Be(100m);
        CloseAsOf.Resolve(History, new DateOnly(2026, 1, 3))!.Date.Should().Be(new DateOnly(2026, 1, 2));
    }

    [Fact]
    public void Resolve_DateAfterEveryStoredClose_CarriesForwardTheLatestOne()
    {
        CloseAsOf.Resolve(History, new DateOnly(2026, 6, 1))!.Close.Should().Be(102m);
    }

    /// <summary>
    /// The deliberate difference from <see cref="FxRateResolver"/>: nothing at or before the
    /// requested date is a named failure (null), never a reach back to the earliest close on file.
    /// </summary>
    [Fact]
    public void Resolve_NothingAtOrBeforeTheDate_ReturnsNull_RatherThanTheEarliestClose()
    {
        CloseAsOf.Resolve(History, new DateOnly(2025, 12, 1)).Should().BeNull();
    }

    [Fact]
    public void Resolve_EmptyHistory_ReturnsNull()
    {
        CloseAsOf.Resolve([], new DateOnly(2026, 1, 1)).Should().BeNull();
    }
}
