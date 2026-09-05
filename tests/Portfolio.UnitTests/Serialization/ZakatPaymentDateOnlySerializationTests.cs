using System.Text.Json;
using FluentAssertions;
using Portfolio.Api.Serialization;
using Portfolio.Application.Dtos;

namespace Portfolio.UnitTests.Serialization;

/// <summary>
/// zakat.md §3.2 / CLAUDE.md wire contract: <see cref="ZakatPaymentDto.PaidOn"/> is a
/// <see cref="DateOnly"/>, serialised as a bare <c>YYYY-MM-DD</c> string with no time-of-day or UTC
/// offset component at all — unlike <c>DateTimeOffset.ToString("O")</c>/<c>toISOString()</c>, there
/// is nothing in this wire format for SGT's UTC+8 to shift, because .NET's built-in
/// <see cref="DateOnly"/> JSON converter never encodes a time component in the first place. Same
/// trap as <c>Transaction.TradeDate</c> — see <c>CLAUDE.md</c> → "Wire contract".
/// </summary>
public sealed class ZakatPaymentDateOnlySerializationTests
{
    private static readonly JsonSerializerOptions Options = PortfolioJsonSerialization.Apply(new JsonSerializerOptions());

    [Fact]
    public void ZakatPaymentDto_PaidOn_SerializesAsBareDateString_NoTimeOrOffset()
    {
        var dto = new ZakatPaymentDto(1, new DateOnly(2026, 3, 1), 250.75m);

        var json = JsonSerializer.Serialize(dto, Options);

        json.Should().Contain("\"paidOn\":\"2026-03-01\"");
        json.Should().NotContain("T00:00:00");
        json.Should().NotContain("Z\"");
    }

    [Fact]
    public void ZakatPaymentDto_PaidOn_RoundTrips_ExactlyUnshifted()
    {
        // A date near a Singapore-relevant UTC+8 boundary — if anything anywhere treated this as a
        // DateTimeOffset/DateTime and re-serialized via toISOString()-style UTC conversion, a
        // positive offset would shift the date backward by a day. DateOnly has no such path.
        var original = new DateOnly(2026, 1, 1);
        var dto = new CreateZakatPaymentRequest(original, 100m);

        var json = JsonSerializer.Serialize(dto, Options);
        var roundTripped = JsonSerializer.Deserialize<CreateZakatPaymentRequest>(json, Options);

        roundTripped!.PaidOn.Should().Be(original);
    }
}
