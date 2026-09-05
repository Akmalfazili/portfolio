using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services;
using Portfolio.Domain.Entities;
using Portfolio.Infrastructure.Persistence;
using Portfolio.UnitTests.TestSupport;

namespace Portfolio.UnitTests.Services;

/// <summary>
/// <see cref="FxSpotRateService"/> — the cache-and-fall-back-on-failure layer in front of
/// <see cref="IFxRateProvider.GetSpotRateAsync"/>. The single most important behaviour pinned here
/// is that the TTL is measured from <see cref="FxSpotQuote.FetchedAt"/>, never
/// <see cref="FxSpotQuote.AsOf"/> — see that entity's remarks for the weekend scenario this
/// protects against.
/// </summary>
public sealed class FxSpotRateServiceTests : IDisposable
{
    private readonly PortfolioDbContext _db;
    private readonly IFxRateProvider _provider = Substitute.For<IFxRateProvider>();

    public FxSpotRateServiceTests()
    {
        var options = new DbContextOptionsBuilder<PortfolioDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new PortfolioDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    private FxSpotRateService CreateSut(DateTimeOffset now, TimeSpan? ttl = null) =>
        new(_db, _provider, new FixedTimeProvider(now),
            Options.Create(new FxSpotRateOptions { Ttl = ttl ?? TimeSpan.FromMinutes(15) }),
            NullLogger<FxSpotRateService>.Instance);

    [Fact]
    public async Task GetOrRefreshAsync_NoStoredRow_CallsProvider_AndPersistsTheResult()
    {
        var now = new DateTimeOffset(2026, 9, 5, 11, 31, 0, TimeSpan.Zero);
        var providerAsOf = new DateTimeOffset(2026, 9, 5, 11, 31, 0, TimeSpan.Zero);
        _provider.GetSpotRateAsync("USD", "SGD", Arg.Any<CancellationToken>())
            .Returns(new FxSpotResult(1.26691m, providerAsOf));

        var sut = CreateSut(now);
        var result = await sut.GetOrRefreshAsync("USD", "SGD", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Rate.Should().Be(1.26691m);
        result.AsOf.Should().Be(providerAsOf);
        result.FetchedAt.Should().Be(now);

        var stored = await _db.FxSpotQuotes.SingleAsync();
        stored.Rate.Should().Be(1.26691m);
    }

    [Fact]
    public async Task GetOrRefreshAsync_FreshCache_DoesNotCallTheProviderAtAll()
    {
        var fetchedAt = new DateTimeOffset(2026, 9, 5, 11, 0, 0, TimeSpan.Zero);
        _db.FxSpotQuotes.Add(new FxSpotQuote
        {
            Base = "USD", Quote = "SGD", Rate = 1.30m, AsOf = fetchedAt, FetchedAt = fetchedAt,
        });
        await _db.SaveChangesAsync();

        // Five minutes later — inside the default 15-minute TTL.
        var now = fetchedAt.AddMinutes(5);
        var sut = CreateSut(now);

        var result = await sut.GetOrRefreshAsync("USD", "SGD", CancellationToken.None);

        result!.Rate.Should().Be(1.30m);
        await _provider.DidNotReceive().GetSpotRateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrRefreshAsync_StaleCache_CallsTheProvider_AndOverwritesTheStoredRow()
    {
        var fetchedAt = new DateTimeOffset(2026, 9, 5, 11, 0, 0, TimeSpan.Zero);
        _db.FxSpotQuotes.Add(new FxSpotQuote
        {
            Base = "USD", Quote = "SGD", Rate = 1.30m, AsOf = fetchedAt, FetchedAt = fetchedAt,
        });
        await _db.SaveChangesAsync();

        // 20 minutes later — past the default 15-minute TTL.
        var now = fetchedAt.AddMinutes(20);
        var newAsOf = now;
        _provider.GetSpotRateAsync("USD", "SGD", Arg.Any<CancellationToken>())
            .Returns(new FxSpotResult(1.31m, newAsOf));

        var sut = CreateSut(now);
        var result = await sut.GetOrRefreshAsync("USD", "SGD", CancellationToken.None);

        result!.Rate.Should().Be(1.31m);
        result.FetchedAt.Should().Be(now);

        // Overwritten in place — still exactly one row for the pair, matching PriceQuote's
        // "one row per asset, overwritten on refresh" idiom.
        (await _db.FxSpotQuotes.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task GetOrRefreshAsync_TtlIsMeasuredFromFetchedAt_NotAsOf()
    {
        // The weekend scenario: the provider's own AsOf timestamp is three days old (it keeps
        // returning Friday's timestamp all weekend), but WE fetched it one minute ago. A TTL keyed
        // off AsOf would never be satisfied and would call the provider on every single request;
        // keyed off FetchedAt, this must NOT call the provider.
        var now = new DateTimeOffset(2026, 9, 7, 10, 0, 0, TimeSpan.Zero); // Monday
        var staleAsOf = now.AddDays(-3); // Friday
        var recentFetchedAt = now.AddMinutes(-1);
        _db.FxSpotQuotes.Add(new FxSpotQuote
        {
            Base = "USD", Quote = "SGD", Rate = 1.30m, AsOf = staleAsOf, FetchedAt = recentFetchedAt,
        });
        await _db.SaveChangesAsync();

        var sut = CreateSut(now);
        var result = await sut.GetOrRefreshAsync("USD", "SGD", CancellationToken.None);

        result!.AsOf.Should().Be(staleAsOf);
        await _provider.DidNotReceive().GetSpotRateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrRefreshAsync_ProviderReturnsNull_WithAStoredRow_FallsBackToTheStaleRow()
    {
        var fetchedAt = new DateTimeOffset(2026, 9, 5, 11, 0, 0, TimeSpan.Zero);
        _db.FxSpotQuotes.Add(new FxSpotQuote
        {
            Base = "USD", Quote = "SGD", Rate = 1.30m, AsOf = fetchedAt, FetchedAt = fetchedAt,
        });
        await _db.SaveChangesAsync();

        var now = fetchedAt.AddMinutes(20); // stale, would normally refetch
        _provider.GetSpotRateAsync("USD", "SGD", Arg.Any<CancellationToken>())
            .Returns((FxSpotResult?)null); // throttle denial / 429 / provider error

        var sut = CreateSut(now);
        var result = await sut.GetOrRefreshAsync("USD", "SGD", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Rate.Should().Be(1.30m); // the stale row, not thrown away
        result.FetchedAt.Should().Be(fetchedAt); // NOT bumped — no successful fetch happened
    }

    [Fact]
    public async Task GetOrRefreshAsync_ProviderReturnsNull_WithNothingStored_ReturnsNull()
    {
        _provider.GetSpotRateAsync("USD", "SGD", Arg.Any<CancellationToken>())
            .Returns((FxSpotResult?)null);

        var sut = CreateSut(new DateTimeOffset(2026, 9, 5, 11, 0, 0, TimeSpan.Zero));
        var result = await sut.GetOrRefreshAsync("USD", "SGD", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetOrRefreshAsync_ProviderThrows_DoesNotPropagate_FallsBackToStoredRow()
    {
        var fetchedAt = new DateTimeOffset(2026, 9, 5, 11, 0, 0, TimeSpan.Zero);
        _db.FxSpotQuotes.Add(new FxSpotQuote
        {
            Base = "USD", Quote = "SGD", Rate = 1.30m, AsOf = fetchedAt, FetchedAt = fetchedAt,
        });
        await _db.SaveChangesAsync();

        var now = fetchedAt.AddMinutes(20);
        _provider.GetSpotRateAsync("USD", "SGD", Arg.Any<CancellationToken>())
            .Returns<Task<FxSpotResult?>>(_ => throw new HttpRequestException("network blip"));

        var sut = CreateSut(now);
        var act = async () => await sut.GetOrRefreshAsync("USD", "SGD", CancellationToken.None);

        // Must never throw — this sits on the zakat report's read path.
        var result = await act.Should().NotThrowAsync();
        result.Subject!.Rate.Should().Be(1.30m);
    }

    [Fact]
    public async Task GetOrRefreshAsync_ProviderThrows_WithNothingStored_ReturnsNull_DoesNotPropagate()
    {
        _provider.GetSpotRateAsync("USD", "SGD", Arg.Any<CancellationToken>())
            .Returns<Task<FxSpotResult?>>(_ => throw new HttpRequestException("network blip"));

        var sut = CreateSut(new DateTimeOffset(2026, 9, 5, 11, 0, 0, TimeSpan.Zero));
        var act = async () => await sut.GetOrRefreshAsync("USD", "SGD", CancellationToken.None);

        var result = await act.Should().NotThrowAsync();
        result.Subject.Should().BeNull();
    }
}
