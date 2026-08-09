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

public sealed class AssetServiceTests : IDisposable
{
    /// <summary>Fixed so D27's <c>CreatedAt</c> stamp is assertable rather than "roughly now".</summary>
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly PortfolioDbContext _db;
    private readonly AssetService _sut;

    public AssetServiceTests()
    {
        var options = new DbContextOptionsBuilder<PortfolioDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _db = new PortfolioDbContext(options);
        _sut = new AssetService(_db, new FixedTimeProvider(Now));
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

    // --- D27: distinguishing "your record is wrong" from "the market is closed", without
    // spending a provider credit to do it.

    [Fact]
    public async Task CreateAsync_StampsCreatedAt_AndReportsNotYetPriced()
    {
        var request = new CreateAssetRequest("APPL", "Apple (typo'd)", AssetClass.Stock, "NASDAQ", "USD", QuoteProviderKind.TwelveData, "APPL", null);

        var result = await _sut.CreateAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.CreatedAt.Should().Be(Now);
        result.Value.HasEverBeenPriced.Should().BeFalse(
            "nothing has had a chance to price it yet — this is the benign case the UI must not "
            + "confuse with a wrong symbol");
    }

    /// <summary>
    /// The D27 case itself: a well-formed but wrong symbol that D23 cannot reject. It is accepted,
    /// and stays unpriced forever — so the flag that says so is the only signal pointing at the
    /// record rather than at the market.
    /// </summary>
    [Fact]
    public async Task ListAsync_AssetThatNoSourceHasEverPriced_ReportsHasEverBeenPricedFalse()
    {
        await _sut.CreateAsync(
            new CreateAssetRequest("APPL", "Apple (typo'd)", AssetClass.Stock, "NASDAQ", "USD", QuoteProviderKind.TwelveData, "APPL", null),
            CancellationToken.None);

        var assets = await _sut.ListAsync(null, CancellationToken.None);

        assets.Should().ContainSingle().Which.HasEverBeenPriced.Should().BeFalse();
    }

    [Fact]
    public async Task ListAsync_AssetWithALiveQuote_ReportsHasEverBeenPricedTrue()
    {
        var created = await _sut.CreateAsync(
            new CreateAssetRequest("AAPL", "Apple Inc.", AssetClass.Stock, "NASDAQ", "USD", QuoteProviderKind.TwelveData, "AAPL", null),
            CancellationToken.None);
        _db.PriceQuotes.Add(new PriceQuote
        {
            AssetId = created.Value!.Id, Price = 333.02m, Currency = "USD", AsOf = Now,
        });
        await _db.SaveChangesAsync();

        var assets = await _sut.ListAsync(null, CancellationToken.None);

        assets.Should().ContainSingle().Which.HasEverBeenPriced.Should().BeTrue();
    }

    /// <summary>
    /// PriceHistory counts too. A backfilled stock outside market hours has no <c>PriceQuote</c>
    /// at all — treating that as "never priced" would fire the D27 hint at every US stock every
    /// weekend, which is exactly the false alarm that would train the user to ignore it.
    /// </summary>
    [Fact]
    public async Task ListAsync_AssetWithOnlyBackfilledHistory_ReportsHasEverBeenPricedTrue()
    {
        var created = await _sut.CreateAsync(
            new CreateAssetRequest("AAPL", "Apple Inc.", AssetClass.Stock, "NASDAQ", "USD", QuoteProviderKind.TwelveData, "AAPL", null),
            CancellationToken.None);
        _db.PriceHistories.Add(new PriceHistory
        {
            AssetId = created.Value!.Id, Date = new DateOnly(2026, 7, 24), Close = 333.02m, Currency = "USD",
        });
        await _db.SaveChangesAsync();

        var assets = await _sut.ListAsync(null, CancellationToken.None);

        assets.Should().ContainSingle().Which.HasEverBeenPriced.Should().BeTrue();
    }

    /// <summary>
    /// Renaming or deactivating an asset must not reset the clock — the whole value of the hint is
    /// that it measures how long the silence has lasted.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_DoesNotResetCreatedAt()
    {
        var created = await _sut.CreateAsync(
            new CreateAssetRequest("APPL", "Apple (typo'd)", AssetClass.Stock, "NASDAQ", "USD", QuoteProviderKind.TwelveData, "APPL", null),
            CancellationToken.None);
        var id = created.Value!.Id;

        var updated = await _sut.UpdateAsync(
            id,
            new UpdateAssetRequest("APPL", "Apple (renamed)", AssetClass.Stock, "NASDAQ", "USD", QuoteProviderKind.TwelveData, "APPL", null, false),
            CancellationToken.None);

        updated.Value!.CreatedAt.Should().Be(Now);
        updated.Value.HasEverBeenPriced.Should().BeFalse();
    }

    // --- D27 fresh-database fix: ProviderHasEverSucceeded gates the escalated warning on the
    // *provider*, not the individual asset — found live on a freshly created Docker database
    // where every seeded asset's CreatedAt is a static seed constant, so HasEverBeenPriced alone
    // made every asset look like a 14-day-old identifier problem on day one.

    [Fact]
    public async Task ListAsync_NoSourceHasEverSucceededInThisDatabase_ReportsProviderHasEverSucceededFalse()
    {
        await _sut.CreateAsync(
            new CreateAssetRequest("AAPL", "Apple Inc.", AssetClass.Stock, "NASDAQ", "USD", QuoteProviderKind.TwelveData, "AAPL", null),
            CancellationToken.None);

        var assets = await _sut.ListAsync(null, CancellationToken.None);

        assets.Should().ContainSingle().Which.ProviderHasEverSucceeded.Should().BeFalse(
            "SourceRefreshStates is empty on a genuinely fresh database — nothing has ever "
            + "proven this provider even works here, so there is no evidence the record is wrong");
    }

    /// <summary>
    /// The exact scenario the fix targets: a brand-new asset that itself has no quote or history
    /// yet must still report <c>ProviderHasEverSucceeded: true</c> once ANY asset sharing its
    /// provider has succeeded — the gate is per-provider, not per-asset. Without this, a second
    /// AAPL-like stock added the day after the first one started working would still read as
    /// unproven.
    /// </summary>
    [Fact]
    public async Task ListAsync_ASiblingAssetOnTheSameProviderHasSucceeded_ReportsProviderHasEverSucceededTrue()
    {
        await _sut.CreateAsync(
            new CreateAssetRequest("AAPL", "Apple Inc.", AssetClass.Stock, "NASDAQ", "USD", QuoteProviderKind.TwelveData, "AAPL", null),
            CancellationToken.None);
        // A brand-new, never-priced sibling on the SAME provider.
        await _sut.CreateAsync(
            new CreateAssetRequest("MSFT", "Microsoft", AssetClass.Stock, "NASDAQ", "USD", QuoteProviderKind.TwelveData, "MSFT", null),
            CancellationToken.None);

        _db.AddSourceRefreshState(new SourceRefreshState
        {
            Source = QuoteProviderKind.TwelveData,
            LastAttemptedAt = Now,
            LastSuccessAt = Now,
            LastRunSuccess = true,
            SymbolsRefreshed = 1,
        });
        await _db.SaveChangesAsync();

        var assets = await _sut.ListAsync(null, CancellationToken.None);

        assets.Should().OnlyContain(a => a.ProviderHasEverSucceeded);
    }

    [Fact]
    public async Task ListAsync_ProviderHasEverSucceeded_IsIsolatedPerProvider()
    {
        // CoinGecko has succeeded; TwelveData has not. A TwelveData asset must not borrow
        // CoinGecko's success — the two providers' reliability say nothing about each other.
        await _sut.CreateAsync(
            new CreateAssetRequest("AAPL", "Apple Inc.", AssetClass.Stock, "NASDAQ", "USD", QuoteProviderKind.TwelveData, "AAPL", null),
            CancellationToken.None);
        await _sut.CreateAsync(
            new CreateAssetRequest("ETH", "Ethereum", AssetClass.Crypto, null, "USD", QuoteProviderKind.CoinGecko, null, "ethereum"),
            CancellationToken.None);

        _db.AddSourceRefreshState(new SourceRefreshState
        {
            Source = QuoteProviderKind.CoinGecko,
            LastAttemptedAt = Now,
            LastSuccessAt = Now,
            LastRunSuccess = true,
            SymbolsRefreshed = 1,
        });
        await _db.SaveChangesAsync();

        var assets = await _sut.ListAsync(null, CancellationToken.None);

        assets.Should().ContainSingle(a => a.Symbol == "AAPL").Which.ProviderHasEverSucceeded.Should().BeFalse();
        assets.Should().ContainSingle(a => a.Symbol == "ETH").Which.ProviderHasEverSucceeded.Should().BeTrue();
    }

    /// <summary>
    /// A provider whose most recent attempt failed (D2's false-success bug fixed) but which DID
    /// succeed at some earlier point must still count — <c>LastSuccessAt</c> is "ever", not
    /// "most recently". Recording an old success as evidence the provider genuinely works, even
    /// through a currently-failing streak, is deliberate: a transient outage should not un-prove
    /// what a real prior success already established.
    /// </summary>
    [Fact]
    public async Task ListAsync_ProviderCurrentlyFailing_ButSucceededBefore_StillReportsProviderHasEverSucceededTrue()
    {
        await _sut.CreateAsync(
            new CreateAssetRequest("ETH", "Ethereum", AssetClass.Crypto, null, "USD", QuoteProviderKind.CoinGecko, null, "ethereum"),
            CancellationToken.None);

        _db.AddSourceRefreshState(new SourceRefreshState
        {
            Source = QuoteProviderKind.CoinGecko,
            LastAttemptedAt = Now,
            LastSuccessAt = Now.AddDays(-1), // succeeded yesterday
            LastRunSuccess = false, // but the most recent attempt (today) failed — D2's fix
            LastError = "CoinGecko returned HTTP 401.",
            SymbolsRefreshed = 0,
        });
        await _db.SaveChangesAsync();

        var assets = await _sut.ListAsync(null, CancellationToken.None);

        assets.Should().ContainSingle().Which.ProviderHasEverSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task GetByIdAsync_ReportsProviderHasEverSucceeded_SameAsListAsync()
    {
        var created = await _sut.CreateAsync(
            new CreateAssetRequest("ETH", "Ethereum", AssetClass.Crypto, null, "USD", QuoteProviderKind.CoinGecko, null, "ethereum"),
            CancellationToken.None);

        _db.AddSourceRefreshState(new SourceRefreshState
        {
            Source = QuoteProviderKind.CoinGecko,
            LastAttemptedAt = Now,
            LastSuccessAt = Now,
            LastRunSuccess = true,
            SymbolsRefreshed = 1,
        });
        await _db.SaveChangesAsync();

        var reread = await _sut.GetByIdAsync(created.Value!.Id, CancellationToken.None);

        reread!.ProviderHasEverSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_ReportsProviderHasEverSucceeded_ReflectingExistingProviderState()
    {
        // The provider already works — proven by an unrelated, earlier asset — before this
        // brand-new one is created. Its own HasEverBeenPriced is still false (nothing has priced
        // IT yet), but ProviderHasEverSucceeded must already be true.
        _db.AddSourceRefreshState(new SourceRefreshState
        {
            Source = QuoteProviderKind.TwelveData,
            LastAttemptedAt = Now,
            LastSuccessAt = Now,
            LastRunSuccess = true,
            SymbolsRefreshed = 1,
        });
        await _db.SaveChangesAsync();

        var result = await _sut.CreateAsync(
            new CreateAssetRequest("MSFT", "Microsoft", AssetClass.Stock, "NASDAQ", "USD", QuoteProviderKind.TwelveData, "MSFT", null),
            CancellationToken.None);

        result.Value!.HasEverBeenPriced.Should().BeFalse();
        result.Value.ProviderHasEverSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateAsync_ReportsProviderHasEverSucceeded_ReflectingCurrentProviderState()
    {
        var created = await _sut.CreateAsync(
            new CreateAssetRequest("MSFT", "Microsoft", AssetClass.Stock, "NASDAQ", "USD", QuoteProviderKind.TwelveData, "MSFT", null),
            CancellationToken.None);

        _db.AddSourceRefreshState(new SourceRefreshState
        {
            Source = QuoteProviderKind.TwelveData,
            LastAttemptedAt = Now,
            LastSuccessAt = Now,
            LastRunSuccess = true,
            SymbolsRefreshed = 1,
        });
        await _db.SaveChangesAsync();

        var updated = await _sut.UpdateAsync(
            created.Value!.Id,
            new UpdateAssetRequest("MSFT", "Microsoft Corp.", AssetClass.Stock, "NASDAQ", "USD", QuoteProviderKind.TwelveData, "MSFT", null, true),
            CancellationToken.None);

        updated.Value!.ProviderHasEverSucceeded.Should().BeTrue();
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
