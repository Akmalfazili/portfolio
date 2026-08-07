using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Portfolio.Application.Common;
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

    // --- D23: the provider-routing coherence checks that stop an asset from being silently
    // unpriceable forever.

    [Theory]
    [InlineData(QuoteProviderKind.TwelveData)]
    [InlineData(QuoteProviderKind.Yahoo)]
    public async Task CreateAsync_RejectsMissingProviderSymbol_ForTwelveDataOrYahoo(QuoteProviderKind kind)
    {
        var request = new CreateAssetRequest("MSFT", "Microsoft", AssetClass.Stock, "NASDAQ", "USD", kind, null, null);

        var result = await _sut.CreateAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.ValidationErrors.Should().ContainKey("providerSymbol");
    }

    [Fact]
    public async Task CreateAsync_RejectsMissingProviderCoinId_ForCoinGecko()
    {
        var request = new CreateAssetRequest("BTC", "Bitcoin", AssetClass.Crypto, null, "USD", QuoteProviderKind.CoinGecko, null, null);

        var result = await _sut.CreateAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.ValidationErrors.Should().ContainKey("providerCoinId");
    }

    [Fact]
    public async Task CreateAsync_AcceptsCoherentTwelveDataAsset()
    {
        var request = new CreateAssetRequest("AAPL", "Apple Inc.", AssetClass.Stock, "NASDAQ", "USD", QuoteProviderKind.TwelveData, "AAPL", null);

        var result = await _sut.CreateAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.ProviderSymbol.Should().Be("AAPL");
        result.Value.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_AcceptsCoherentYahooAsset_SgxWithDotSiSuffix()
    {
        var request = new CreateAssetRequest("Z74", "Singtel", AssetClass.Stock, "SGX", "SGD", QuoteProviderKind.Yahoo, "Z74.SI", null);

        var result = await _sut.CreateAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    // --- UpdateAsync / deactivate (Phase 12)

    [Fact]
    public async Task UpdateAsync_UnknownId_ReturnsNotFound()
    {
        var request = new UpdateAssetRequest("AAPL", "Apple Inc.", AssetClass.Stock, "NASDAQ", "USD", QuoteProviderKind.TwelveData, "AAPL", null, true);

        var result = await _sut.UpdateAsync(999, request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ServiceErrorKind.NotFound);
    }

    [Fact]
    public async Task UpdateAsync_SettingIsActiveFalse_DeactivatesTheAsset()
    {
        var created = await _sut.CreateAsync(
            new CreateAssetRequest("AAPL", "Apple Inc.", AssetClass.Stock, "NASDAQ", "USD", QuoteProviderKind.TwelveData, "AAPL", null),
            CancellationToken.None);
        var id = created.Value!.Id;

        var request = new UpdateAssetRequest("AAPL", "Apple Inc.", AssetClass.Stock, "NASDAQ", "USD", QuoteProviderKind.TwelveData, "AAPL", null, false);
        var result = await _sut.UpdateAsync(id, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.IsActive.Should().BeFalse();

        var reread = await _sut.GetByIdAsync(id, CancellationToken.None);
        reread!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAsync_RejectsProviderRoutingMismatch_SameAsCreate()
    {
        var created = await _sut.CreateAsync(
            new CreateAssetRequest("AAPL", "Apple Inc.", AssetClass.Stock, "NASDAQ", "USD", QuoteProviderKind.TwelveData, "AAPL", null),
            CancellationToken.None);
        var id = created.Value!.Id;

        // Switching provider to CoinGecko without a ProviderCoinId must be rejected on update too.
        var request = new UpdateAssetRequest("AAPL", "Apple Inc.", AssetClass.Stock, "NASDAQ", "USD", QuoteProviderKind.CoinGecko, "AAPL", null, true);
        var result = await _sut.UpdateAsync(id, request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.ValidationErrors.Should().ContainKey("providerCoinId");
    }

    [Fact]
    public async Task UpdateAsync_RejectsDuplicateSymbol_AgainstADifferentAsset()
    {
        await _sut.CreateAsync(
            new CreateAssetRequest("AAPL", "Apple Inc.", AssetClass.Stock, "NASDAQ", "USD", QuoteProviderKind.TwelveData, "AAPL", null),
            CancellationToken.None);
        var msft = await _sut.CreateAsync(
            new CreateAssetRequest("MSFT", "Microsoft", AssetClass.Stock, "NASDAQ", "USD", QuoteProviderKind.TwelveData, "MSFT", null),
            CancellationToken.None);

        var request = new UpdateAssetRequest("AAPL", "Microsoft", AssetClass.Stock, "NASDAQ", "USD", QuoteProviderKind.TwelveData, "MSFT", null, true);
        var result = await _sut.UpdateAsync(msft.Value!.Id, request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.ValidationErrors.Should().ContainKey("symbol");
    }

    [Fact]
    public async Task UpdateAsync_KeepingItsOwnSymbol_DoesNotTriggerTheDuplicateCheck()
    {
        var created = await _sut.CreateAsync(
            new CreateAssetRequest("AAPL", "Apple Inc.", AssetClass.Stock, "NASDAQ", "USD", QuoteProviderKind.TwelveData, "AAPL", null),
            CancellationToken.None);

        var request = new UpdateAssetRequest("AAPL", "Apple Incorporated", AssetClass.Stock, "NASDAQ", "USD", QuoteProviderKind.TwelveData, "AAPL", null, true);
        var result = await _sut.UpdateAsync(created.Value!.Id, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Name.Should().Be("Apple Incorporated");
    }
}
