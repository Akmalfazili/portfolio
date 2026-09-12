using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using Portfolio.Api.Hubs;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services;
using Portfolio.Application.Services.Calendar;
using Portfolio.Infrastructure.Persistence;
using Portfolio.UnitTests.TestSupport;

namespace Portfolio.UnitTests.Hubs;

/// <summary>
/// Regression coverage for the transport asymmetry fixed alongside <see cref="PriceRefreshStatusEnricher"/>:
/// <see cref="PricesHub.OnConnectedAsync"/> used to send <see cref="PriceRefreshStatusStore"/>'s bare
/// snapshot straight to the newly connected caller, so <see cref="PriceRefreshStatus.EffectiveTwelveDataIntervalSeconds"/>,
/// <see cref="PriceRefreshStatus.CreditsUsedToday"/> and <see cref="PriceRefreshStatus.CreditBudget"/>
/// were always null on this transport even though <c>GET /api/prices/status</c> populated all three.
/// Exercises the hub directly — no live SignalR connection, no network — by substituting
/// <see cref="Hub.Clients"/> and <see cref="Hub.Context"/>, which SignalR's <c>Hub</c> base class
/// exposes with public setters exactly so it can be unit tested this way.
/// </summary>
public sealed class PricesHubTests : IDisposable
{
    private readonly PortfolioDbContext _db = new(
        new DbContextOptionsBuilder<PortfolioDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task OnConnectedAsync_SendsAStatus_WithTheDerivedCadenceAndCreditFieldsPopulated()
    {
        var creditThrottle = Substitute.For<ITwelveDataCreditThrottle>();
        creditThrottle.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(new TwelveDataCreditStatus(CreditsUsedToday: 123, DailyBudget: 800, RemainingToday: 677));

        var store = new PriceRefreshStatusStore(_db);
        var enricher = new PriceRefreshStatusEnricher(_db, creditThrottle, Options.Create(new PriceRefreshOptions()));
        var calendar = Substitute.For<IMarketCalendar>();
        var timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero));

        var hub = new PricesHub(store, enricher, calendar, timeProvider);

        var callerProxy = Substitute.For<ISingleClientProxy>();
        var clients = Substitute.For<IHubCallerClients>();
        clients.Caller.Returns(callerProxy);
        hub.Clients = clients;
        hub.Context = Substitute.For<HubCallerContext>();

        await hub.OnConnectedAsync();

        await callerProxy.Received(1).SendCoreAsync(
            "RefreshStatus",
            Arg.Is<object?[]>(args =>
                args != null &&
                args.Length == 1 &&
                args[0] is PriceRefreshStatus &&
                ((PriceRefreshStatus)args[0]!).CreditsUsedToday == 123 &&
                ((PriceRefreshStatus)args[0]!).CreditBudget == 800 &&
                ((PriceRefreshStatus)args[0]!).EffectiveTwelveDataIntervalSeconds != null),
            Arg.Any<CancellationToken>());
    }
}
