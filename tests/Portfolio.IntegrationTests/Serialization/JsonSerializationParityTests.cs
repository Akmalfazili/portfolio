using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Portfolio.Application.Dtos;
using Portfolio.Domain.Enums;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Portfolio.IntegrationTests.Serialization;

/// <summary>
/// D7. This process runs <b>two</b> independent JSON serializers — the minimal-API response
/// serializer and SignalR's protocol serializer — and the frontend parses payloads from both with
/// the same TypeScript types. Phase 5 shipped a real bug from exactly this split
/// (<c>"source":0</c> over the hub versus <c>"source":"TwelveData"</c> over REST), fixed by
/// registering the converter in both places. That fix left the two agreeing only by coincidence of
/// defaults, with nothing asserting it.
///
/// <para>These tests resolve the <b>actually-registered</b> options objects out of the running
/// host's DI container — not a hand-rolled copy of <c>Program.cs</c>'s configuration — so they
/// fail if either registration is changed, dropped, or given its own opinion.</para>
/// </summary>
public sealed class JsonSerializationParityTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    /// <summary>A payload that actually crosses both pipelines: <c>GET /api/prices/status</c>
    /// returns it, and <c>SignalRPriceBroadcaster.BroadcastRefreshStatusAsync</c> pushes it.
    /// Carries the <see cref="QuoteProviderKind"/> enum that caused the original defect, plus
    /// nulls, booleans, dates and a nested collection.</summary>
    private static PriceRefreshStatus SampleStatus() => new(
        LastRefreshedAt: new DateTimeOffset(2026, 8, 8, 4, 30, 0, TimeSpan.Zero),
        NyseOpen: false,
        SgxOpen: true,
        NextScheduledRunAt: new DateTimeOffset(2026, 8, 8, 4, 32, 0, TimeSpan.Zero),
        Sources:
        [
            new SourceRefreshStatus(QuoteProviderKind.TwelveData, null, null, false, null, 0, null),
            new SourceRefreshStatus(QuoteProviderKind.Yahoo, null, null, false, "boom", 0, null),
            new SourceRefreshStatus(
                QuoteProviderKind.CoinGecko,
                new DateTimeOffset(2026, 8, 8, 4, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 8, 4, 30, 0, TimeSpan.Zero),
                true,
                null,
                3,
                null),
        ]);

    private (JsonSerializerOptions Http, JsonSerializerOptions Hub) ResolveBothConfigurations()
    {
        using var scope = factory.Services.CreateScope();

        var http = scope.ServiceProvider.GetRequiredService<IOptions<HttpJsonOptions>>()
            .Value.SerializerOptions;
        var hub = scope.ServiceProvider.GetRequiredService<IOptions<JsonHubProtocolOptions>>()
            .Value.PayloadSerializerOptions;

        return (http, hub);
    }

    /// <summary>
    /// The assertion that actually matters, and the reason this is a string comparison rather than
    /// a property-by-property check: it catches <i>any</i> divergence between the two
    /// configurations, including settings nobody thought to enumerate here. If a future change
    /// gives one pipeline a different naming policy, a different enum encoding, a different null
    /// handling or a different number format, this fails.
    /// </summary>
    [Fact]
    public void BothSerializers_ProduceIdenticalJson_ForTheSamePayload()
    {
        var (http, hub) = ResolveBothConfigurations();
        var status = SampleStatus();

        var overRest = JsonSerializer.Serialize(status, http);
        var overHub = JsonSerializer.Serialize(status, hub);

        overHub.Should().Be(
            overRest,
            "the REST endpoint and the SignalR hub send this same DTO to the same frontend types; "
            + "one logical field must never have two wire encodings (D7)");
    }

    /// <summary>
    /// Pins the encoding itself, not just the agreement between the two. Without this, both
    /// pipelines could drift to raw ints together and the parity test above would still pass.
    /// </summary>
    [Fact]
    public void BothSerializers_EncodeEnums_AsNames_NotRawInts()
    {
        var (http, hub) = ResolveBothConfigurations();
        var status = SampleStatus();

        foreach (var (name, options) in new[] { ("http", http), ("hub", hub) })
        {
            var json = JsonSerializer.Serialize(status, options);

            json.Should().Contain("\"TwelveData\"", $"the {name} serializer must send enum names");
            json.Should().Contain("\"Yahoo\"", $"the {name} serializer must send enum names");
            json.Should().Contain("\"CoinGecko\"", $"the {name} serializer must send enum names");

            // QuoteProviderKind.TwelveData=0, Yahoo=1, CoinGecko=2 — the original defect's shape.
            json.Should().NotContain("\"source\":0", $"the {name} serializer must not send raw ints");
            json.Should().NotContain("\"source\":1", $"the {name} serializer must not send raw ints");
            json.Should().NotContain("\"source\":2", $"the {name} serializer must not send raw ints");
        }
    }

    /// <summary>
    /// Both configurations must come from <c>PortfolioJsonSerialization.Apply</c>. Checking the
    /// two settings it sets is a cheap tripwire for someone registering a pipeline by hand again
    /// instead of routing it through the shared factory.
    /// </summary>
    [Fact]
    public void BothSerializers_ShareTheAppliedContract()
    {
        var (http, hub) = ResolveBothConfigurations();

        http.PropertyNamingPolicy.Should().BeSameAs(JsonNamingPolicy.CamelCase);
        hub.PropertyNamingPolicy.Should().BeSameAs(JsonNamingPolicy.CamelCase);

        http.Converters.Should().ContainSingle(c => c is JsonStringEnumConverter);
        hub.Converters.Should().ContainSingle(c => c is JsonStringEnumConverter);
    }

    /// <summary>
    /// <see cref="PriceRefreshStatus.Closes"/> is a trailing nullable parameter for exactly this
    /// reason: a <see cref="PriceRefreshStatus"/> serialized by a process running before this field
    /// existed (or before D38's three Twelve Data fields existed) has no <c>"closes"</c> property —
    /// or none of the four trailing properties — at all. Deserializing that older shape must not
    /// throw and must leave every trailing field null rather than defaulting to a misleadingly
    /// "attempted and failed" 0/false.
    /// </summary>
    [Fact]
    public void PriceRefreshStatus_DeserializesAnOlderSnapshot_WithNoTrailingFieldsAtAll()
    {
        var (http, _) = ResolveBothConfigurations();

        // Exactly what GET /api/prices/status returned before EffectiveTwelveDataIntervalSeconds,
        // CreditsUsedToday, CreditBudget and Closes existed — no trace of any of the four.
        const string legacyJson = """
            {
                "lastRefreshedAt": "2026-08-08T04:30:00+00:00",
                "nyseOpen": false,
                "sgxOpen": true,
                "nextScheduledRunAt": "2026-08-08T04:32:00+00:00",
                "sources": []
            }
            """;

        var status = JsonSerializer.Deserialize<PriceRefreshStatus>(legacyJson, http);

        status.Should().NotBeNull();
        status!.EffectiveTwelveDataIntervalSeconds.Should().BeNull();
        status.CreditsUsedToday.Should().BeNull();
        status.CreditBudget.Should().BeNull();
        status.Closes.Should().BeNull();
    }

    /// <summary>The round trip the other direction: a freshly enriched status, <c>Closes</c>
    /// included, must survive serialize-then-deserialize through both wire configurations without
    /// losing or renaming a field — the shape the frontend actually receives today.</summary>
    [Fact]
    public void PriceRefreshStatus_Closes_RoundTrips_ThroughBothSerializers()
    {
        var (http, hub) = ResolveBothConfigurations();

        var status = SampleStatus() with
        {
            EffectiveTwelveDataIntervalSeconds = 300,
            CreditsUsedToday = 50,
            CreditBudget = 800,
            Closes =
            [
                new MarketCloseStatus(Market.Nyse, null, null, null, null, null),
                new MarketCloseStatus(
                    Market.Sgx,
                    new DateOnly(2026, 9, 11),
                    new DateTimeOffset(2026, 9, 12, 17, 1, 2, TimeSpan.Zero),
                    new DateTimeOffset(2026, 9, 12, 17, 1, 2, TimeSpan.Zero),
                    true,
                    null),
            ],
        };

        foreach (var options in new[] { http, hub })
        {
            var json = JsonSerializer.Serialize(status, options);
            var roundTripped = JsonSerializer.Deserialize<PriceRefreshStatus>(json, options);

            roundTripped.Should().NotBeNull();
            roundTripped!.Closes.Should().HaveCount(2);
            roundTripped.Closes!.Single(c => c.Market == Market.Sgx).LatestCloseDate
                .Should().Be(new DateOnly(2026, 9, 11));
            roundTripped.Closes!.Single(c => c.Market == Market.Nyse).LatestCloseDate.Should().BeNull();
        }
    }
}
