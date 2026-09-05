using FluentAssertions;
using Portfolio.Application.Services.Calculators;
using Portfolio.Domain.Entities;

namespace Portfolio.UnitTests.Services.Calculators;

/// <summary>
/// <see cref="FxRateResolver"/> — carry-forward is the decided behaviour for a date with no
/// stored rate (weekends, holidays), and the earliest stored rate is used as a last resort for a
/// date before any rate exists at all, rather than throwing and blanking out an entire series.
/// </summary>
public sealed class FxRateResolverTests
{
    private static readonly FxRate Jan1 = new() { Date = new DateOnly(2026, 1, 1), Base = "USD", Quote = "SGD", Rate = 1.30m };
    private static readonly FxRate Jan5 = new() { Date = new DateOnly(2026, 1, 5), Base = "USD", Quote = "SGD", Rate = 1.31m };
    private static readonly FxRate Jan10 = new() { Date = new DateOnly(2026, 1, 10), Base = "USD", Quote = "SGD", Rate = 1.32m };

    private static readonly IReadOnlyList<FxRate> Rates = [Jan1, Jan5, Jan10];

    [Fact]
    public void Resolve_ExactDateMatch_ReturnsThatRate()
    {
        FxRateResolver.Resolve(Rates, new DateOnly(2026, 1, 5)).Should().Be(1.31m);
    }

    [Fact]
    public void Resolve_DateBetweenRates_CarriesForwardTheMostRecentPriorRate()
    {
        // A Saturday between the Friday (Jan 5) and next Wednesday (Jan 10) rates carries Jan 5's
        // rate forward rather than interpolating or looking ahead.
        FxRateResolver.Resolve(Rates, new DateOnly(2026, 1, 7)).Should().Be(1.31m);
    }

    [Fact]
    public void Resolve_DateAfterLastRate_CarriesForwardTheLastKnownRate()
    {
        FxRateResolver.Resolve(Rates, new DateOnly(2026, 3, 1)).Should().Be(1.32m);
    }

    [Fact]
    public void Resolve_DateBeforeEveryStoredRate_FallsBackToTheEarliestRate()
    {
        // Only possible before a currency's backfill has run for that far back — the earliest
        // rate is used as a last resort rather than throwing and losing an otherwise-computable
        // early data point.
        FxRateResolver.Resolve(Rates, new DateOnly(2025, 12, 25)).Should().Be(1.30m);
    }

    [Fact]
    public void Resolve_NoRatesAtAll_Throws()
    {
        var act = () => FxRateResolver.Resolve([], new DateOnly(2026, 1, 1));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ResolveDetailed_GenuineCarryForward_IsNotFlaggedAsCarriedBack()
    {
        // Jan 7 carries Jan 5's rate forward across a weekend — a normal, unremarkable case.
        var result = FxRateResolver.ResolveDetailed(Rates, new DateOnly(2026, 1, 7));

        result.Rate.Should().Be(1.31m);
        result.ResolvedDate.Should().Be(new DateOnly(2026, 1, 5));
        result.CarriedBack.Should().BeFalse();
    }

    [Fact]
    public void ResolveDetailed_DateBeforeEveryStoredRate_IsFlaggedAsCarriedBack()
    {
        // zakat.md §5: the resolved rate is dated AFTER the requested date, which means this was a
        // before-the-data fallback to the earliest rate, not a genuine carry-forward.
        var requested = new DateOnly(2025, 12, 25);
        var result = FxRateResolver.ResolveDetailed(Rates, requested);

        result.Rate.Should().Be(1.30m);
        result.ResolvedDate.Should().Be(new DateOnly(2026, 1, 1));
        result.RequestedDate.Should().Be(requested);
        result.CarriedBack.Should().BeTrue();
    }

    [Fact]
    public void ResolveDetailed_ExactDateMatch_IsNotFlaggedAsCarriedBack()
    {
        FxRateResolver.ResolveDetailed(Rates, new DateOnly(2026, 1, 5)).CarriedBack.Should().BeFalse();
    }
}
