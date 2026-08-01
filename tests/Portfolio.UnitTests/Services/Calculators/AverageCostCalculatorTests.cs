using FluentAssertions;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Services.Calculators;
using Portfolio.Domain.Enums;

namespace Portfolio.UnitTests.Services.Calculators;

/// <summary>
/// <see cref="AverageCostCalculator"/> — fees capitalised into basis on buys, deducted from
/// proceeds on sells, partial sells costed at the average cost immediately before the sale, and a
/// sell that fully closes a position snapping both quantity and cost basis to exactly zero.
/// </summary>
public sealed class AverageCostCalculatorTests
{
    private readonly AverageCostCalculator _sut = new();

    [Fact]
    public void Calculate_SingleBuy_CapitalisesFeesIntoBasis()
    {
        var transactions = new[]
        {
            new CostBasisTransaction(new DateOnly(2026, 1, 1), TransactionType.Buy, 10m, 1000m, 5m),
        };

        var steps = _sut.Calculate(transactions);

        steps.Should().HaveCount(1);
        steps[0].QuantityHeld.Should().Be(10m);
        steps[0].CostBasisUsd.Should().Be(1005m); // 1000 gross + 5 fee
        steps[0].RealizedPnlUsd.Should().Be(0m);
    }

    [Fact]
    public void Calculate_PartialSell_CostsOutAtAverageCost_AndBooksRealizedPnl()
    {
        // Buy 10 @ 100 + 5 fee -> cost basis 1005, average cost 100.5/unit.
        // Sell 4 @ 150 - 2 fee -> cost of 4 units = 402, net proceeds = 598, realized = 196.
        // Remaining: 6 units, cost basis 1005 - 402 = 603.
        var transactions = new[]
        {
            new CostBasisTransaction(new DateOnly(2026, 1, 1), TransactionType.Buy, 10m, 1000m, 5m),
            new CostBasisTransaction(new DateOnly(2026, 2, 1), TransactionType.Sell, 4m, 600m, 2m),
        };

        var steps = _sut.Calculate(transactions);

        steps.Should().HaveCount(2);
        var afterSell = steps[1];
        afterSell.QuantityHeld.Should().Be(6m);
        afterSell.CostBasisUsd.Should().Be(603m);
        afterSell.RealizedPnlUsd.Should().Be(196m);
    }

    [Fact]
    public void Calculate_SellThatClosesPositionEntirely_SnapsQuantityAndCostBasisToZero()
    {
        // Continuing from the partial-sell example: sell the remaining 6 @ 120 - 3 fee.
        // Average cost per unit going in = 603 / 6 = 100.5 exactly.
        // Cost of 6 units = 603, net proceeds = 720 - 3 = 717, realized this sale = 114.
        // Total realized across both sales = 196 + 114 = 310.
        var transactions = new[]
        {
            new CostBasisTransaction(new DateOnly(2026, 1, 1), TransactionType.Buy, 10m, 1000m, 5m),
            new CostBasisTransaction(new DateOnly(2026, 2, 1), TransactionType.Sell, 4m, 600m, 2m),
            new CostBasisTransaction(new DateOnly(2026, 3, 1), TransactionType.Sell, 6m, 720m, 3m),
        };

        var steps = _sut.Calculate(transactions);

        steps.Should().HaveCount(3);
        var final = steps[2];
        final.QuantityHeld.Should().Be(0m);
        final.CostBasisUsd.Should().Be(0m);
        final.RealizedPnlUsd.Should().Be(310m);
    }

    [Fact]
    public void Calculate_FractionalSubCentQuantities_NeverRoundsToZero()
    {
        // 1,000,000 units at a sub-cent price — decimal(18,2)-style rounding would floor this to
        // 0.00 and destroy every figure below. Buy 1,000,000 @ 0.0005326, no fee -> basis 532.6000
        // exactly. Sell half @ 0.0006 -> cost of 500,000 units = 266.3, proceeds = 300, realized = 33.7.
        var transactions = new[]
        {
            new CostBasisTransaction(new DateOnly(2026, 1, 1), TransactionType.Buy, 1_000_000m, 532.6m, 0m),
            new CostBasisTransaction(new DateOnly(2026, 2, 1), TransactionType.Sell, 500_000m, 300m, 0m),
        };

        var steps = _sut.Calculate(transactions);

        steps[0].CostBasisUsd.Should().Be(532.6m);
        steps[1].QuantityHeld.Should().Be(500_000m);
        steps[1].CostBasisUsd.Should().Be(266.3m);
        steps[1].RealizedPnlUsd.Should().Be(33.7m);
    }

    [Fact]
    public void Calculate_MultipleBuysAtDifferentPrices_BlendsIntoOneAverageCost()
    {
        // Buy 10 @ 100 (no fee) -> basis 1000. Buy 10 more @ 200 (no fee) -> basis 3000, 20 units,
        // average cost 150/unit. Sell 5 @ 300 -> cost of 5 units = 750, proceeds = 1500,
        // realized = 750.
        var transactions = new[]
        {
            new CostBasisTransaction(new DateOnly(2026, 1, 1), TransactionType.Buy, 10m, 1000m, 0m),
            new CostBasisTransaction(new DateOnly(2026, 1, 15), TransactionType.Buy, 10m, 2000m, 0m),
            new CostBasisTransaction(new DateOnly(2026, 2, 1), TransactionType.Sell, 5m, 1500m, 0m),
        };

        var steps = _sut.Calculate(transactions);

        steps[1].QuantityHeld.Should().Be(20m);
        steps[1].CostBasisUsd.Should().Be(3000m);

        var afterSell = steps[2];
        afterSell.QuantityHeld.Should().Be(15m);
        afterSell.CostBasisUsd.Should().Be(2250m); // 3000 - 750
        afterSell.RealizedPnlUsd.Should().Be(750m); // 1500 proceeds - 750 cost
    }

    [Fact]
    public void Calculate_OutOfOrderInput_IsSortedByTradeDate()
    {
        var transactions = new[]
        {
            new CostBasisTransaction(new DateOnly(2026, 2, 1), TransactionType.Sell, 4m, 600m, 0m),
            new CostBasisTransaction(new DateOnly(2026, 1, 1), TransactionType.Buy, 10m, 1000m, 0m),
        };

        var steps = _sut.Calculate(transactions);

        steps[0].TradeDate.Should().Be(new DateOnly(2026, 1, 1));
        steps[1].TradeDate.Should().Be(new DateOnly(2026, 2, 1));
        steps[1].QuantityHeld.Should().Be(6m);
    }

    [Fact]
    public void Calculate_EmptyInput_ReturnsEmpty()
    {
        _sut.Calculate([]).Should().BeEmpty();
    }
}
