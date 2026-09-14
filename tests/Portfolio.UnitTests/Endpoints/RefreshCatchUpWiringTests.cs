using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Portfolio.Api.Endpoints;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services;
using Portfolio.Domain.Enums;

namespace Portfolio.UnitTests.Endpoints;

/// <summary>
/// Refresh-catch-up feature: pins <see cref="PricesEndpoints"/>'s catch-up decision helpers
/// directly (they are <c>internal</c>, exposed to this assembly via
/// <c>InternalsVisibleTo</c> — see the csproj) rather than through a full HTTP round trip. This is
/// deliberate, not a shortcut: no endpoint test exists for the sibling
/// <c>POST /api/prices/backfill</c> route either (see tracker.md), and a WebApplicationFactory test
/// here would need a real database and would exercise the gate/Task.Run machinery this file is
/// specifically NOT about — the wiring RULES (nothing missing takes no gate; an already-held gate
/// reports AlreadyRunning without starting a second run; a cooldown outcome skips catch-up
/// entirely) are pure decisions, extracted so they can be asserted without any of that.
/// </summary>
public sealed class RefreshCatchUpWiringTests
{
    private static readonly PriceHistoryCatchUpCandidates EmptyPriceHistory = new([], [], [], [], []);
    private static readonly DividendsCatchUpCandidates EmptyDividends = new([], []);

    [Fact]
    public void ShouldRunCatchUp_FalseOnlyForCooldownActive()
    {
        PricesEndpoints.ShouldRunCatchUp(PriceRefreshOutcome.CooldownActive).Should().BeFalse();

        PricesEndpoints.ShouldRunCatchUp(PriceRefreshOutcome.Completed).Should().BeTrue();
        PricesEndpoints.ShouldRunCatchUp(PriceRefreshOutcome.NothingDue).Should().BeTrue();
        PricesEndpoints.ShouldRunCatchUp(PriceRefreshOutcome.Queued).Should().BeTrue();
    }

    [Fact]
    public void HasPriceHistoryWorkToDo_FalseWhenBothFetchListsAreEmpty()
    {
        PricesEndpoints.HasPriceHistoryWorkToDo(EmptyPriceHistory).Should().BeFalse();
    }

    [Fact]
    public void HasPriceHistoryWorkToDo_TrueWhenOnlyAnFxPairIsMissing_NoAssetItself()
    {
        // The FX-only catch-up shape — an asset's own history already covered, but its currency's
        // stored rate is stale (see IRefreshCatchUpService's FX remarks).
        var candidates = new PriceHistoryCatchUpCandidates([], ["SGD"], [], [], [Market.Sgx]);

        PricesEndpoints.HasPriceHistoryWorkToDo(candidates).Should().BeTrue();
    }

    [Fact]
    public void HasPriceHistoryWorkToDo_TrueWhenAnAssetIsMissing()
    {
        var candidates = new PriceHistoryCatchUpCandidates(
            [new CatchUpAssetTarget(1, "AAPL", Market.Nyse)], [], [], [], [Market.Nyse]);

        PricesEndpoints.HasPriceHistoryWorkToDo(candidates).Should().BeTrue();
    }

    [Fact]
    public void HasDividendsWorkToDo_FalseWhenNothingToFetch()
    {
        PricesEndpoints.HasDividendsWorkToDo(EmptyDividends).Should().BeFalse();
    }

    [Fact]
    public void HasDividendsWorkToDo_TrueWhenAnAssetIsMissing()
    {
        var candidates = new DividendsCatchUpCandidates([new CatchUpAssetTarget(1, "AAPL", Market.Nyse)], []);

        PricesEndpoints.HasDividendsWorkToDo(candidates).Should().BeTrue();
    }

    [Fact]
    public void BuildPriceHistoryLeg_NothingToFetch_ReportsEmptyFetchListsButStillCarriesRetryAndNotYetAvailable()
    {
        var candidates = new PriceHistoryCatchUpCandidates([], [], ["RETRY"], ["FUTURE"], []);

        var leg = PricesEndpoints.BuildPriceHistoryLeg(candidates, CatchUpState.NothingToFetch);

        leg.State.Should().Be(CatchUpState.NothingToFetch);
        leg.FetchSymbols.Should().BeEmpty();
        leg.FxPairs.Should().BeEmpty();
        leg.RetryPendingSymbols.Should().Contain("RETRY");
        leg.NotYetAvailableSymbols.Should().Contain("FUTURE");
        leg.RunId.Should().BeNull("nothing started, so there is no run to correlate a completion push with");
    }

    [Fact]
    public void BuildPriceHistoryLeg_AlreadyRunning_StillReportsWhatWouldHaveBeenFetched_ButRunIdIsNull()
    {
        var candidates = new PriceHistoryCatchUpCandidates(
            [new CatchUpAssetTarget(1, "AAPL", Market.Nyse)], ["SGD"], [], [], [Market.Nyse, Market.Sgx]);

        var leg = PricesEndpoints.BuildPriceHistoryLeg(candidates, CatchUpState.AlreadyRunning);

        leg.State.Should().Be(CatchUpState.AlreadyRunning);
        leg.FetchSymbols.Should().Contain("AAPL");
        leg.FxPairs.Should().Contain("USD/SGD");
        leg.RunId.Should().BeNull("this click started nothing — the ALREADY-running task's own run id belongs to a prior click");
    }

    [Fact]
    public void BuildPriceHistoryLeg_Queued_CarriesTheGivenRunId()
    {
        var candidates = new PriceHistoryCatchUpCandidates(
            [new CatchUpAssetTarget(1, "AAPL", Market.Nyse)], [], [], [], [Market.Nyse]);
        var runId = Guid.NewGuid();

        var leg = PricesEndpoints.BuildPriceHistoryLeg(candidates, CatchUpState.Queued, runId);

        leg.RunId.Should().Be(runId);
    }

    [Fact]
    public void BuildDividendsLeg_AlwaysReportsEmptyFxPairsAndNotYetAvailable()
    {
        var candidates = new DividendsCatchUpCandidates([new CatchUpAssetTarget(1, "AAPL", Market.Nyse)], ["RETRY"]);
        var runId = Guid.NewGuid();

        var leg = PricesEndpoints.BuildDividendsLeg(candidates, CatchUpState.Queued, runId);

        leg.FetchSymbols.Should().Contain("AAPL");
        leg.RetryPendingSymbols.Should().Contain("RETRY");
        leg.FxPairs.Should().BeEmpty();
        leg.NotYetAvailableSymbols.Should().BeEmpty();
        leg.RunId.Should().Be(runId);
    }

    [Fact]
    public void BuildDividendsLeg_NothingToFetch_RunIdIsNull()
    {
        var leg = PricesEndpoints.BuildDividendsLeg(EmptyDividends, CatchUpState.NothingToFetch);

        leg.RunId.Should().BeNull();
    }

    // --- Real gate object, real StartXxxCatchUp entry point — "nothing missing takes no gate" and
    // "a held gate reports AlreadyRunning and starts nothing" against the actual integration point,
    // not just the pure decision it delegates to.

    [Fact]
    public void StartPriceHistoryCatchUp_NothingToFetch_TakesNoGate_AndNeverTouchesTheScopeFactory()
    {
        var gate = new ManualBackfillInFlightGate();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();

        var leg = PricesEndpoints.StartPriceHistoryCatchUp(
            EmptyPriceHistory, scopeFactory, gate, NullLogger<Program>.Instance);

        leg.State.Should().Be(CatchUpState.NothingToFetch);
        leg.RunId.Should().BeNull();
        scopeFactory.DidNotReceive().CreateScope();
        // The gate was never taken — still free for something else (e.g. POST /api/prices/backfill)
        // to claim immediately afterward.
        gate.TryEnter().Should().BeTrue();
    }

    [Fact]
    public void StartPriceHistoryCatchUp_GateAlreadyHeld_ReportsAlreadyRunning_AndNeverTouchesTheScopeFactory()
    {
        var gate = new ManualBackfillInFlightGate();
        gate.TryEnter().Should().BeTrue("simulating a concurrently running manual backfill or catch-up");
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var candidates = new PriceHistoryCatchUpCandidates(
            [new CatchUpAssetTarget(1, "AAPL", Market.Nyse)], [], [], [], [Market.Nyse]);

        var leg = PricesEndpoints.StartPriceHistoryCatchUp(candidates, scopeFactory, gate, NullLogger<Program>.Instance);

        leg.State.Should().Be(CatchUpState.AlreadyRunning);
        leg.FetchSymbols.Should().Contain("AAPL", "still reports what WOULD have been fetched");
        leg.RunId.Should().BeNull("this click started nothing");
        scopeFactory.DidNotReceive().CreateScope();
    }

    [Fact]
    public void StartDividendsCatchUp_NothingToFetch_TakesNoGate_AndNeverTouchesTheScopeFactory()
    {
        var gate = new ManualDividendBackfillInFlightGate();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();

        var leg = PricesEndpoints.StartDividendsCatchUp(
            EmptyDividends, scopeFactory, gate, NullLogger<Program>.Instance);

        leg.State.Should().Be(CatchUpState.NothingToFetch);
        leg.RunId.Should().BeNull();
        scopeFactory.DidNotReceive().CreateScope();
        gate.TryEnter().Should().BeTrue();
    }

    [Fact]
    public void StartDividendsCatchUp_GateAlreadyHeld_ReportsAlreadyRunning_AndNeverTouchesTheScopeFactory()
    {
        var gate = new ManualDividendBackfillInFlightGate();
        gate.TryEnter().Should().BeTrue("simulating a concurrently running manual dividend backfill or catch-up");
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var candidates = new DividendsCatchUpCandidates([new CatchUpAssetTarget(1, "AAPL", Market.Nyse)], []);

        var leg = PricesEndpoints.StartDividendsCatchUp(candidates, scopeFactory, gate, NullLogger<Program>.Instance);

        leg.State.Should().Be(CatchUpState.AlreadyRunning);
        leg.FetchSymbols.Should().Contain("AAPL");
        leg.RunId.Should().BeNull("this click started nothing");
        scopeFactory.DidNotReceive().CreateScope();
    }
}
