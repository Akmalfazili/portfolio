using FluentAssertions;
using Portfolio.Application.Services;

namespace Portfolio.UnitTests.Services;

/// <summary>
/// <see cref="ManualDividendBackfillInFlightGate"/> — the mechanism <c>POST /api/dividends/backfill</c>
/// (Defect 2's fix, giving <see cref="Portfolio.Domain.Enums.RefreshTrigger.DividendBackfillManual"/>
/// a real caller) uses to stop a second manual click from starting an overlapping detached run.
/// </summary>
public sealed class ManualDividendBackfillInFlightGateTests
{
    [Fact]
    public void TryEnter_WhenNotInFlight_SucceedsAndMarksInFlight()
    {
        var gate = new ManualDividendBackfillInFlightGate();

        gate.TryEnter().Should().BeTrue();
    }

    [Fact]
    public void TryEnter_WhenAlreadyInFlight_FailsUntilExit()
    {
        var gate = new ManualDividendBackfillInFlightGate();

        gate.TryEnter().Should().BeTrue();
        gate.TryEnter().Should().BeFalse("a second manual trigger must not start an overlapping run");

        gate.Exit();

        gate.TryEnter().Should().BeTrue("once the first run has exited, a new one may start");
    }

    [Fact]
    public void Gate_IsIndependentOfThePriceBackfillGate()
    {
        // Deliberately separate types (see the class's own remarks) so a manual price backfill
        // and a manual dividend backfill can run concurrently without tripping each other's flag.
        var dividendGate = new ManualDividendBackfillInFlightGate();
        var priceGate = new ManualBackfillInFlightGate();

        priceGate.TryEnter().Should().BeTrue();

        dividendGate.TryEnter().Should().BeTrue("the two backfills must not share an in-flight flag");
    }
}
