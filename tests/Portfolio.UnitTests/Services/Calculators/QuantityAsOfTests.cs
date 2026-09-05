using FluentAssertions;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Services.Calculators;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;

namespace Portfolio.UnitTests.Services.Calculators;

/// <summary>
/// <see cref="QuantityAsOf"/> — zakat.md §5: a second, independent definition of "quantity held",
/// only safe because <see cref="Calculate_AgreesWithTheLastCostBasisStepQuantityHeld_OverTheSameTransactionSet"/>
/// proves it agrees with <c>AverageCostCalculator</c>'s own <c>CostBasisStep.QuantityHeld</c> over
/// the same transaction set.
/// </summary>
public sealed class QuantityAsOfTests
{
    [Fact]
    public void Calculate_SumsBuysMinusSells_UpToAndIncludingTheGivenDate()
    {
        var transactions = new[]
        {
            Buy(1, new DateOnly(2026, 1, 1), 10m),
            Sell(2, new DateOnly(2026, 2, 1), 4m),
            Buy(3, new DateOnly(2026, 3, 1), 5m), // after the cutoff below — excluded
        };

        var quantity = QuantityAsOf.Calculate(transactions, new DateOnly(2026, 2, 1));

        quantity.Should().Be(6m); // 10 - 4
    }

    [Fact]
    public void Calculate_TransactionExactlyOnTheDate_IsIncluded()
    {
        var transactions = new[] { Buy(1, new DateOnly(2026, 6, 30), 3m) };

        QuantityAsOf.Calculate(transactions, new DateOnly(2026, 6, 30)).Should().Be(3m);
    }

    [Fact]
    public void Calculate_NoTransactionsAtOrBeforeDate_ReturnsZero()
    {
        var transactions = new[] { Buy(1, new DateOnly(2026, 6, 30), 3m) };

        QuantityAsOf.Calculate(transactions, new DateOnly(2026, 1, 1)).Should().Be(0m);
    }

    [Fact]
    public void Calculate_FullySoldDown_ReturnsZero()
    {
        var transactions = new[]
        {
            Buy(1, new DateOnly(2026, 1, 1), 10m),
            Sell(2, new DateOnly(2026, 2, 1), 10m),
        };

        QuantityAsOf.Calculate(transactions, new DateOnly(2026, 2, 1)).Should().Be(0m);
    }

    /// <summary>
    /// The pinning test zakat.md §5 requires: without this, <see cref="QuantityAsOf"/> is a second
    /// definition of "quantity held" that nothing proves agrees with the cost-basis path's own
    /// notion of the same thing, across a mixed multi-buy/multi-sell/same-day sequence.
    /// </summary>
    [Fact]
    public void Calculate_AgreesWithTheLastCostBasisStepQuantityHeld_OverTheSameTransactionSet()
    {
        var transactions = new[]
        {
            Buy(1, new DateOnly(2026, 1, 1), 10m),
            Buy(2, new DateOnly(2026, 1, 1), 5m), // same-day as #1 — order matters for the stable sort
            Sell(3, new DateOnly(2026, 2, 1), 4m),
            Buy(4, new DateOnly(2026, 3, 15), 2.5m), // fractional, crypto-style
            Sell(5, new DateOnly(2026, 4, 1), 6m),
        };

        var asOf = new DateOnly(2026, 4, 1);

        var costBasisTransactions = transactions
            .Select(t => new CostBasisTransaction(t.TradeDate, t.Type, t.Quantity, t.Quantity * 10m, 0m))
            .ToList();
        var lastStep = new AverageCostCalculator().Calculate(costBasisTransactions)[^1];

        var quantityAsOf = QuantityAsOf.Calculate(transactions, asOf);

        quantityAsOf.Should().Be(lastStep.QuantityHeld);
    }

    private static Transaction Buy(int id, DateOnly tradeDate, decimal quantity) =>
        new() { Id = id, Type = TransactionType.Buy, TradeDate = tradeDate, Quantity = quantity, PricePerUnit = 1m, Currency = "USD" };

    private static Transaction Sell(int id, DateOnly tradeDate, decimal quantity) =>
        new() { Id = id, Type = TransactionType.Sell, TradeDate = tradeDate, Quantity = quantity, PricePerUnit = 1m, Currency = "USD" };
}
