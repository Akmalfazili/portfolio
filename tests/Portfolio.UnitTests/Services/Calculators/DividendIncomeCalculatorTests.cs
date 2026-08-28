using FluentAssertions;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Services.Calculators;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;

namespace Portfolio.UnitTests.Services.Calculators;

/// <summary>
/// <see cref="DividendIncomeCalculator"/> — the ex-date entitlement rule (a buy strictly before
/// the ex-date earns the payment, a buy ON the ex-date does not), partial and full sell-downs, and
/// USD conversion (handled entirely by the caller here — see <see cref="DividendIncomeInput"/> —
/// so these tests exercise pure quantity/income arithmetic without any FX fixtures).
/// </summary>
public sealed class DividendIncomeCalculatorTests
{
    private readonly DividendIncomeCalculator _sut = new();

    private static Transaction Buy(DateOnly date, decimal quantity) => new()
    {
        Type = TransactionType.Buy, TradeDate = date, Quantity = quantity, PricePerUnit = 100m, Currency = "USD",
    };

    private static Transaction Sell(DateOnly date, decimal quantity) => new()
    {
        Type = TransactionType.Sell, TradeDate = date, Quantity = quantity, PricePerUnit = 100m, Currency = "USD",
    };

    private static DividendIncomeInput Event(DateOnly exDate, decimal amountPerShare) =>
        new(exDate, amountPerShare, "USD", amountPerShare);

    [Fact]
    public void Calculate_BuyStrictlyBeforeExDate_EarnsTheDividend()
    {
        var transactions = new[] { Buy(new DateOnly(2026, 1, 1), 10m) };
        var events = new[] { Event(new DateOnly(2026, 2, 1), 0.50m) };

        var result = _sut.Calculate(transactions, events);

        result.Should().ContainSingle();
        result[0].UnitsHeldAtExDate.Should().Be(10m);
        result[0].IncomeUsd.Should().Be(5m); // 10 * 0.50
    }

    [Fact]
    public void Calculate_BuyOnTheExDateItself_DoesNotEarnThatDividend()
    {
        // The real market rule this calculator exists to enforce: the ex-date is the first day a
        // share trades WITHOUT entitlement to the upcoming payment.
        var exDate = new DateOnly(2026, 2, 1);
        var transactions = new[] { Buy(exDate, 10m) };
        var events = new[] { Event(exDate, 0.50m) };

        var result = _sut.Calculate(transactions, events);

        result.Should().ContainSingle();
        result[0].UnitsHeldAtExDate.Should().Be(0m);
        result[0].IncomeUsd.Should().Be(0m);
    }

    [Fact]
    public void Calculate_BuyTheDayAfterTheExDate_DoesNotEarnIt()
    {
        var exDate = new DateOnly(2026, 2, 1);
        var transactions = new[] { Buy(exDate.AddDays(1), 10m) };
        var events = new[] { Event(exDate, 0.50m) };

        var result = _sut.Calculate(transactions, events);

        result[0].UnitsHeldAtExDate.Should().Be(0m);
        result[0].IncomeUsd.Should().Be(0m);
    }

    [Fact]
    public void Calculate_PartiallySoldPosition_EarnsOnTheRemainingUnitsOnly()
    {
        var transactions = new[]
        {
            Buy(new DateOnly(2026, 1, 1), 20m),
            Sell(new DateOnly(2026, 1, 20), 8m), // strictly before the ex-date below
        };
        var events = new[] { Event(new DateOnly(2026, 2, 1), 1.00m) };

        var result = _sut.Calculate(transactions, events);

        result[0].UnitsHeldAtExDate.Should().Be(12m); // 20 - 8
        result[0].IncomeUsd.Should().Be(12m);
    }

    [Fact]
    public void Calculate_FullySoldDownPosition_EarnsNothing_ButStillAppearsAsAZeroIncomeLine()
    {
        var transactions = new[]
        {
            Buy(new DateOnly(2026, 1, 1), 10m),
            Sell(new DateOnly(2026, 1, 20), 10m),
        };
        var events = new[] { Event(new DateOnly(2026, 2, 1), 1.00m) };

        var result = _sut.Calculate(transactions, events);

        // Still surfaced, not silently dropped — a payment history should show the payment
        // happened while the position was closed rather than vanishing.
        result.Should().ContainSingle();
        result[0].UnitsHeldAtExDate.Should().Be(0m);
        result[0].IncomeUsd.Should().Be(0m);
    }

    [Fact]
    public void Calculate_NeverHeldAnyUnitsBeforeTheExDate_EarnsNothing()
    {
        var transactions = new[] { Buy(new DateOnly(2026, 3, 1), 10m) }; // after the ex-date
        var events = new[] { Event(new DateOnly(2026, 2, 1), 1.00m) };

        var result = _sut.Calculate(transactions, events);

        result[0].UnitsHeldAtExDate.Should().Be(0m);
        result[0].IncomeUsd.Should().Be(0m);
    }

    [Fact]
    public void Calculate_MultipleExDates_TracksRunningQuantityAcrossEvents()
    {
        var transactions = new[]
        {
            Buy(new DateOnly(2026, 1, 1), 10m),
            Buy(new DateOnly(2026, 2, 15), 5m), // strictly before the second ex-date, after the first
        };
        var events = new[]
        {
            Event(new DateOnly(2026, 2, 1), 1.00m),
            Event(new DateOnly(2026, 5, 1), 1.00m),
        };

        var result = _sut.Calculate(transactions, events);

        result.Should().HaveCount(2);
        result[0].UnitsHeldAtExDate.Should().Be(10m); // only the first buy counts yet
        result[0].IncomeUsd.Should().Be(10m);
        result[1].UnitsHeldAtExDate.Should().Be(15m); // both buys now count
        result[1].IncomeUsd.Should().Be(15m);
    }

    [Fact]
    public void Calculate_UsesTheCallerSuppliedUsdAmount_NotTheNativeAmount()
    {
        // FX conversion is entirely the caller's responsibility (see DividendIncomeInput's own
        // remarks) - this pins that the calculator trusts AmountPerShareUsd, not
        // AmountPerShareNative, when they differ.
        var transactions = new[] { Buy(new DateOnly(2026, 1, 1), 100m) };
        var events = new[] { new DividendIncomeInput(new DateOnly(2026, 2, 1), 0.103m, "SGD", 0.0824m) };

        var result = _sut.Calculate(transactions, events);

        result[0].AmountPerShareNative.Should().Be(0.103m);
        result[0].Currency.Should().Be("SGD");
        result[0].IncomeUsd.Should().Be(8.24m); // 100 * 0.0824
    }

    [Fact]
    public void Calculate_NoDividendEvents_ReturnsEmpty()
    {
        var transactions = new[] { Buy(new DateOnly(2026, 1, 1), 10m) };

        var result = _sut.Calculate(transactions, []);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Calculate_EventsSuppliedOutOfOrder_StillProducedInExDateOrder()
    {
        var transactions = new[] { Buy(new DateOnly(2026, 1, 1), 10m) };
        var events = new[]
        {
            Event(new DateOnly(2026, 5, 1), 1.00m),
            Event(new DateOnly(2026, 2, 1), 1.00m),
        };

        var result = _sut.Calculate(transactions, events);

        result.Should().HaveCount(2);
        result[0].ExDate.Should().Be(new DateOnly(2026, 2, 1));
        result[1].ExDate.Should().Be(new DateOnly(2026, 5, 1));
    }
}
