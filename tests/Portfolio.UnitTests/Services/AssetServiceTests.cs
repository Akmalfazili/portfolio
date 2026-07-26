using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services;
using Portfolio.Domain.Enums;
using Portfolio.Infrastructure.Persistence;

namespace Portfolio.UnitTests.Services;

public sealed class AssetServiceTests : IDisposable
{
    private readonly PortfolioDbContext _db;
    private readonly AssetService _sut;

    public AssetServiceTests()
    {
        var options = new DbContextOptionsBuilder<PortfolioDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _db = new PortfolioDbContext(options);
        _sut = new AssetService(_db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CreateAsync_RejectsDuplicateSymbol()
    {
        var request = new CreateAssetRequest("ETH", "Ethereum", AssetClass.Crypto, null, "USD", QuoteProviderKind.CoinGecko, null, "ethereum");

        var first = await _sut.CreateAsync(request, CancellationToken.None);
        first.IsSuccess.Should().BeTrue();

        var second = await _sut.CreateAsync(request, CancellationToken.None);

        second.IsSuccess.Should().BeFalse();
        second.Error!.ValidationErrors.Should().ContainKey("symbol");
    }

    [Fact]
    public async Task CreateAsync_RejectsMissingRequiredFields()
    {
        var request = new CreateAssetRequest(string.Empty, string.Empty, AssetClass.Stock, null, "US", QuoteProviderKind.TwelveData, null, null);

        var result = await _sut.CreateAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.ValidationErrors.Should().ContainKeys("symbol", "name", "currency");
    }

    [Fact]
    public async Task ListAsync_FiltersByAssetClass()
    {
        await _sut.CreateAsync(new CreateAssetRequest("AAPL", "Apple Inc.", AssetClass.Stock, "NASDAQ", "USD", QuoteProviderKind.TwelveData, "AAPL", null), CancellationToken.None);
        await _sut.CreateAsync(new CreateAssetRequest("ETH", "Ethereum", AssetClass.Crypto, null, "USD", QuoteProviderKind.CoinGecko, null, "ethereum"), CancellationToken.None);

        var stocksOnly = await _sut.ListAsync(AssetClass.Stock, CancellationToken.None);

        stocksOnly.Should().ContainSingle().Which.Symbol.Should().Be("AAPL");
    }
}
