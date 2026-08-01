using FluentAssertions;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Services.Calculators;

namespace Portfolio.UnitTests.Services.Calculators;

/// <summary>
/// <see cref="PerformanceSeriesBuilder"/> — a step function for cost basis (constant between
/// transactions, jumps exactly on the transaction date) merged against whatever days have a
/// close, skipping days before the first transaction entirely rather than showing a false zero.
/// </summary>
public sealed class PerformanceSeriesBuilderTests
{
    private readonly PerformanceSeriesBuilder _sut = new();

    [Fact]
    public void Build_SkipsDatesBeforeTheFirstTransaction()
    {
        var steps = new[]
        {
            new CostBasisStep(new DateOnly(2026, 1, 10), 10m, 1000m, 0m),
        };
        var closes = new (DateOnly, decimal)[]
        {
            (new DateOnly(2026, 1, 5), 90m),  // before the position existed
            (new DateOnly(2026, 1, 10), 100m),
            (new DateOnly(2026, 1, 11), 105m),
        };

        var points = _sut.Build(steps, closes);

        points.Should().HaveCount(2);
        points[0].Date.Should().Be(new DateOnly(2026, 1, 10));
    }

    [Fact]
    public void Build_CostBasisHoldsFlat_UntilTheNextTransactionDate()
    {
        // Buy 10 @ cost basis 1000 on Jan 1; buy 10 more (basis 3000, qty 20) on Jan 20.
        // Every close between should report the Jan 1 cost basis and 10-unit market value; every
        // close on/after Jan 20 should report the new basis and 20-unit market value.
        var steps = new[]
        {
            new CostBasisStep(new DateOnly(2026, 1, 1), 10m, 1000m, 0m),
            new CostBasisStep(new DateOnly(2026, 1, 20), 20m, 3000m, 0m),
        };
        var closes = new (DateOnly, decimal)[]
        {
            (new DateOnly(2026, 1, 1), 100m),
            (new DateOnly(2026, 1, 10), 110m),
            (new DateOnly(2026, 1, 20), 150m),
            (new DateOnly(2026, 1, 25), 160m),
        };

        var points = _sut.Build(steps, closes);

        points.Should().HaveCount(4);
        points[0].Should().Be(new PerformanceSeriesPoint(new DateOnly(2026, 1, 1), 1000m, 1000m));   // 10 * 100
        points[1].Should().Be(new PerformanceSeriesPoint(new DateOnly(2026, 1, 10), 1000m, 1100m));  // 10 * 110, basis unchanged
        points[2].Should().Be(new PerformanceSeriesPoint(new DateOnly(2026, 1, 20), 3000m, 3000m));  // 20 * 150, basis jumped
        points[3].Should().Be(new PerformanceSeriesPoint(new DateOnly(2026, 1, 25), 3000m, 3200m));  // 20 * 160
    }

    [Fact]
    public void Build_NoCostBasisSteps_ReturnsEmpty()
    {
        _sut.Build([], [(new DateOnly(2026, 1, 1), 100m)]).Should().BeEmpty();
    }

    [Fact]
    public void Build_NoCloses_ReturnsEmpty()
    {
        var steps = new[] { new CostBasisStep(new DateOnly(2026, 1, 1), 10m, 1000m, 0m) };
        _sut.Build(steps, []).Should().BeEmpty();
    }

    [Fact]
    public void Build_UnsortedInputs_AreSortedBeforeMerging()
    {
        var steps = new[]
        {
            new CostBasisStep(new DateOnly(2026, 1, 20), 20m, 3000m, 0m),
            new CostBasisStep(new DateOnly(2026, 1, 1), 10m, 1000m, 0m),
        };
        var closes = new (DateOnly, decimal)[]
        {
            (new DateOnly(2026, 1, 20), 150m),
            (new DateOnly(2026, 1, 1), 100m),
        };

        var points = _sut.Build(steps, closes);

        points.Should().HaveCount(2);
        points[0].Date.Should().Be(new DateOnly(2026, 1, 1));
        points[0].MarketValueUsd.Should().Be(1000m);
        points[1].Date.Should().Be(new DateOnly(2026, 1, 20));
        points[1].MarketValueUsd.Should().Be(3000m);
    }
}
