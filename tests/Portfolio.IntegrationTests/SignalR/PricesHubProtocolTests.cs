using System.Buffers;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Portfolio.Application.Dtos;
using Portfolio.Domain.Enums;

namespace Portfolio.IntegrationTests.SignalR;

/// <summary>
/// Regression test for a real defect found by live-testing a SignalR client against
/// <c>/hubs/prices</c>: SignalR has its own protocol serializer, entirely separate from
/// <c>ConfigureHttpJsonOptions</c> in <c>Program.cs</c>. Registering <c>JsonStringEnumConverter</c>
/// only against the minimal-API pipeline left the hub sending enums as raw ints
/// (<c>"source":0</c>) while REST sent the same field as a name (<c>"source":"TwelveData"</c>) —
/// one logical field, two encodings, in payloads the frontend has to merge.
///
/// This test resolves the *actual* <see cref="IHubProtocol"/> that <c>Program.cs</c> wires up via
/// <c>AddSignalR().AddJsonProtocol(...)</c> — not a hand-rolled copy of that configuration — and
/// serializes a real <see cref="PriceRefreshStatus"/> through it, exactly as
/// <see cref="Portfolio.Api.Hubs.PricesHub"/> and <see cref="Portfolio.Api.Hubs.SignalRPriceBroadcaster"/>
/// would when a client connects. It fails the same way the live client that caught the bug did,
/// and would have failed before the <c>AddJsonProtocol</c> fix was added.
/// </summary>
public sealed class PricesHubProtocolTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public void JsonHubProtocol_SerializesQuoteProviderKind_AsNames_NotRawInts()
    {
        using var scope = factory.Services.CreateScope();

        var protocol = scope.ServiceProvider
            .GetServices<IHubProtocol>()
            .Single(p => p.Name == "json");

        var status = new PriceRefreshStatus(
            LastRefreshedAt: DateTimeOffset.UtcNow,
            NyseOpen: false,
            SgxOpen: false,
            NextScheduledRunAt: DateTimeOffset.UtcNow.AddMinutes(2),
            Sources:
            [
                new SourceRefreshStatus(QuoteProviderKind.TwelveData, null, null, false, null, 0, null),
                new SourceRefreshStatus(QuoteProviderKind.Yahoo, null, null, false, null, 0, null),
                new SourceRefreshStatus(QuoteProviderKind.CoinGecko, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true, null, 3, null),
            ]);

        // Exactly what SignalRPriceBroadcaster.BroadcastRefreshStatusAsync sends over the wire.
        var message = new InvocationMessage("RefreshStatus", [status]);

        var buffer = new ArrayBufferWriter<byte>();
        protocol.WriteMessage(message, buffer);
        var wireJson = Encoding.UTF8.GetString(buffer.WrittenSpan);

        wireJson.Should().Contain("\"TwelveData\"");
        wireJson.Should().Contain("\"Yahoo\"");
        wireJson.Should().Contain("\"CoinGecko\"");

        // The int-encoded regression this test exists to catch: QuoteProviderKind.TwelveData=0,
        // Yahoo=1, CoinGecko=2.
        wireJson.Should().NotContain("\"source\":0");
        wireJson.Should().NotContain("\"source\":1");
        wireJson.Should().NotContain("\"source\":2");
    }

    /// <summary>
    /// Refresh-catch-up feature: <c>CatchUpCompleted</c> is a new SignalR event, added after the
    /// regression above was fixed — pinned here from day one rather than waiting for its own live
    /// defect, since the underlying trap (SignalR's own protocol serializer, entirely separate from
    /// <c>ConfigureHttpJsonOptions</c>) applies to every event this hub ever adds, not just the one
    /// that first exposed it.
    /// </summary>
    [Fact]
    public void JsonHubProtocol_SerializesCatchUpKind_AsNames_NotRawInts()
    {
        using var scope = factory.Services.CreateScope();

        var protocol = scope.ServiceProvider
            .GetServices<IHubProtocol>()
            .Single(p => p.Name == "json");

        var notification = new CatchUpCompletedNotification(
            CatchUpKind.PriceHistory,
            SucceededSymbols: ["AAPL"],
            Failed: [new CatchUpFailure("FX:USD/SGD", "Twelve Data returned HTTP 429.")],
            SkippedForBudgetSymbols: [],
            RowsInserted: 2,
            CompletedAt: DateTimeOffset.UtcNow,
            RunId: Guid.NewGuid());

        var message = new InvocationMessage("CatchUpCompleted", [notification]);

        var buffer = new ArrayBufferWriter<byte>();
        protocol.WriteMessage(message, buffer);
        var wireJson = Encoding.UTF8.GetString(buffer.WrittenSpan);

        wireJson.Should().Contain("\"PriceHistory\"");
        // The int-encoded regression this mirrors: CatchUpKind.PriceHistory=0, Dividends=1.
        wireJson.Should().NotContain("\"kind\":0");
        wireJson.Should().NotContain("\"kind\":1");
    }
}
