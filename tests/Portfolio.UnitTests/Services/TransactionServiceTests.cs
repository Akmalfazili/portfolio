using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Portfolio.Application.Common;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;
using Portfolio.Infrastructure.Persistence;
using Portfolio.UnitTests.TestSupport;

namespace Portfolio.UnitTests.Services;

public sealed class TransactionServiceTests : IDisposable
{
    private readonly PortfolioDbContext _db;
    private readonly FixedTimeProvider _timeProvider;
    private readonly TransactionService _sut;
    private readonly Asset _ethAsset;

    public TransactionServiceTests()
    {
        var options = new DbContextOptionsBuilder<PortfolioDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _db = new PortfolioDbContext(options);

        _ethAsset = new Asset
        {
            Id = 1,
            Symbol = "ETH",
            Name = "Ethereum",
            AssetClass = AssetClass.Crypto,
            Currency = "USD",
            ProviderCoinId = "ethereum",
        };
        _db.Assets.Add(_ethAsset);
        _db.SaveChanges();

        _timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero));
        _sut = new TransactionService(_db, _timeProvider);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CreateAsync_RejectsNonPositiveQuantity()
    {
        var request = new CreateTransactionRequest(
            _ethAsset.Id, TransactionType.Buy, new DateOnly(2026, 1, 1), 0m, 2500m, 0m, "USD", null);

        var result = await _sut.CreateAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.ValidationErrors.Should().ContainKey("quantity");
    }

    [Fact]
    public async Task CreateAsync_RejectsNegativeQuantity()
    {
        var request = new CreateTransactionRequest(
            _ethAsset.Id, TransactionType.Buy, new DateOnly(2026, 1, 1), -0.5m, 2500m, 0m, "USD", null);

        var result = await _sut.CreateAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.ValidationErrors.Should().ContainKey("quantity");
    }

    [Fact]
    public async Task CreateAsync_RejectsFutureTradeDate()
    {
        var future = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime).AddDays(1);
        var request = new CreateTransactionRequest(
            _ethAsset.Id, TransactionType.Buy, future, 1m, 2500m, 0m, "USD", null);

        var result = await _sut.CreateAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.ValidationErrors.Should().ContainKey("tradeDate");
    }

    [Fact]
    public async Task CreateAsync_AllowsTradeDateOfToday()
    {
        var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
        var request = new CreateTransactionRequest(
            _ethAsset.Id, TransactionType.Buy, today, 1m, 2500m, 0m, "USD", null);

        var result = await _sut.CreateAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_Sell_RejectsQuantityExceedingHeldUnits()
    {
        // Hold 1.5 ETH from a prior buy.
        await _sut.CreateAsync(
            new CreateTransactionRequest(_ethAsset.Id, TransactionType.Buy, new DateOnly(2026, 1, 1), 1.5m, 2000m, 0m, "USD", null),
            CancellationToken.None);

        // A tenth of a billionth of a unit over what's held must still be rejected.
        var oversellRequest = new CreateTransactionRequest(
            _ethAsset.Id, TransactionType.Sell, new DateOnly(2026, 2, 1), 1.5000000001m, 2600m, 0m, "USD", null);

        var result = await _sut.CreateAsync(oversellRequest, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.ValidationErrors.Should().ContainKey("quantity");
    }

    [Fact]
    public async Task CreateAsync_Sell_AllowsSellingExactlyTheFractionalUnitsHeld()
    {
        await _sut.CreateAsync(
            new CreateTransactionRequest(_ethAsset.Id, TransactionType.Buy, new DateOnly(2026, 1, 1), 0.000123456m, 2000m, 0m, "USD", null),
            CancellationToken.None);

        var sellRequest = new CreateTransactionRequest(
            _ethAsset.Id, TransactionType.Sell, new DateOnly(2026, 2, 1), 0.000123456m, 2600m, 0m, "USD", null);

        var result = await _sut.CreateAsync(sellRequest, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Quantity.Should().Be(0.000123456m);
    }

    [Fact]
    public async Task CreateAsync_Sell_AccountsForMultiplePriorBuysWithFractionalQuantities()
    {
        // Two fractional buys totalling exactly 0.0003 must combine losslessly for the held-quantity check.
        await _sut.CreateAsync(
            new CreateTransactionRequest(_ethAsset.Id, TransactionType.Buy, new DateOnly(2026, 1, 1), 0.0001m, 2000m, 0m, "USD", null),
            CancellationToken.None);
        await _sut.CreateAsync(
            new CreateTransactionRequest(_ethAsset.Id, TransactionType.Buy, new DateOnly(2026, 1, 5), 0.0002m, 2100m, 0m, "USD", null),
            CancellationToken.None);

        var sellExact = await _sut.CreateAsync(
            new CreateTransactionRequest(_ethAsset.Id, TransactionType.Sell, new DateOnly(2026, 1, 10), 0.0003m, 2200m, 0m, "USD", null),
            CancellationToken.None);

        sellExact.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_RejectsUnknownAsset()
    {
        var request = new CreateTransactionRequest(
            999, TransactionType.Buy, new DateOnly(2026, 1, 1), 1m, 100m, 0m, "USD", null);

        var result = await _sut.CreateAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.ValidationErrors.Should().ContainKey("assetId");
    }

    [Fact]
    public async Task CreateAsync_SubCentPrice_PreservesPrecisionThroughTheServiceLayer()
    {
        // ANVL-style sub-cent price times a bulk quantity — decimal(18,2) would round this to
        // 0.00 and destroy the total below.
        const decimal quantity = 1_000_000m;
        const decimal pricePerUnit = 0.0005326m;

        var request = new CreateTransactionRequest(
            _ethAsset.Id, TransactionType.Buy, new DateOnly(2026, 1, 1), quantity, pricePerUnit, 0m, "USD", null);

        var result = await _sut.CreateAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Quantity.Should().Be(quantity);
        result.Value!.PricePerUnit.Should().Be(pricePerUnit);
        (result.Value!.Quantity * result.Value!.PricePerUnit).Should().Be(532.6000m);
    }

    [Fact]
    public async Task UpdateAsync_ExcludesTheTransactionBeingEditedFromItsOwnHeldQuantityCheck()
    {
        await _sut.CreateAsync(
            new CreateTransactionRequest(_ethAsset.Id, TransactionType.Buy, new DateOnly(2026, 1, 1), 2m, 2000m, 0m, "USD", null),
            CancellationToken.None);

        var sell = await _sut.CreateAsync(
            new CreateTransactionRequest(_ethAsset.Id, TransactionType.Sell, new DateOnly(2026, 1, 5), 2m, 2200m, 0m, "USD", null),
            CancellationToken.None);

        // Editing the sell to the same quantity must still succeed — it has to be excluded from
        // its own held-quantity check, otherwise this looks like an oversell against itself.
        var update = await _sut.UpdateAsync(
            sell.Value!.Id,
            new UpdateTransactionRequest(_ethAsset.Id, TransactionType.Sell, new DateOnly(2026, 1, 6), 2m, 2300m, 0m, "USD", "adjusted"),
            CancellationToken.None);

        update.IsSuccess.Should().BeTrue();
        update.Value!.Notes.Should().Be("adjusted");
    }

    [Fact]
    public async Task UpdateAsync_Sell_StillRejectsAGenuineOversellAfterExcludingItself()
    {
        await _sut.CreateAsync(
            new CreateTransactionRequest(_ethAsset.Id, TransactionType.Buy, new DateOnly(2026, 1, 1), 2m, 2000m, 0m, "USD", null),
            CancellationToken.None);

        var sell = await _sut.CreateAsync(
            new CreateTransactionRequest(_ethAsset.Id, TransactionType.Sell, new DateOnly(2026, 1, 5), 1m, 2200m, 0m, "USD", null),
            CancellationToken.None);

        // Trying to bump this sell up to 2.5 exceeds the 2 units held, even excluding itself.
        var update = await _sut.UpdateAsync(
            sell.Value!.Id,
            new UpdateTransactionRequest(_ethAsset.Id, TransactionType.Sell, new DateOnly(2026, 1, 6), 2.5m, 2300m, 0m, "USD", null),
            CancellationToken.None);

        update.IsSuccess.Should().BeFalse();
        update.Error!.ValidationErrors.Should().ContainKey("quantity");
    }

    [Fact]
    public async Task UpdateAsync_ReturnsNotFound_WhenTransactionDoesNotExist()
    {
        var result = await _sut.UpdateAsync(
            12345,
            new UpdateTransactionRequest(_ethAsset.Id, TransactionType.Buy, new DateOnly(2026, 1, 1), 1m, 100m, 0m, "USD", null),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ServiceErrorKind.NotFound);
    }

    [Fact]
    public async Task DeleteAsync_ReturnsFalse_WhenTransactionDoesNotExist()
    {
        var deleted = await _sut.DeleteAsync(999, CancellationToken.None);

        deleted.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_ReturnsTrue_AndFreesUpTheSoldUnitsForOversellChecks()
    {
        var buy = await _sut.CreateAsync(
            new CreateTransactionRequest(_ethAsset.Id, TransactionType.Buy, new DateOnly(2026, 1, 1), 1m, 2000m, 0m, "USD", null),
            CancellationToken.None);

        var deleted = await _sut.DeleteAsync(buy.Value!.Id, CancellationToken.None);
        deleted.Should().BeTrue();

        // With the only buy gone, even a tiny sell should now be an oversell.
        var sellResult = await _sut.CreateAsync(
            new CreateTransactionRequest(_ethAsset.Id, TransactionType.Sell, new DateOnly(2026, 1, 2), 0.0001m, 2100m, 0m, "USD", null),
            CancellationToken.None);

        sellResult.IsSuccess.Should().BeFalse();
        sellResult.Error!.ValidationErrors.Should().ContainKey("quantity");
    }

    [Fact]
    public async Task ListAsync_FiltersByAssetClass()
    {
        var msftAsset = new Asset
        {
            Id = 2,
            Symbol = "MSFT",
            Name = "Microsoft",
            AssetClass = AssetClass.Stock,
            Currency = "USD",
        };
        _db.Assets.Add(msftAsset);
        await _db.SaveChangesAsync();

        await _sut.CreateAsync(
            new CreateTransactionRequest(_ethAsset.Id, TransactionType.Buy, new DateOnly(2026, 1, 1), 1m, 2000m, 0m, "USD", null),
            CancellationToken.None);
        await _sut.CreateAsync(
            new CreateTransactionRequest(msftAsset.Id, TransactionType.Buy, new DateOnly(2026, 1, 1), 5m, 400m, 0m, "USD", null),
            CancellationToken.None);

        var cryptoOnly = await _sut.ListAsync(AssetClass.Crypto, null, CancellationToken.None);

        cryptoOnly.Should().ContainSingle().Which.AssetSymbol.Should().Be("ETH");
    }
}
