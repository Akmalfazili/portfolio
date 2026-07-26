using System.Text.Json;
using FluentAssertions;
using Portfolio.Infrastructure.MarketData.Json;

namespace Portfolio.UnitTests.MarketData;

/// <summary>
/// Twelve Data encodes the same kind of value as a JSON string on one endpoint and a bare JSON
/// number on another. <see cref="FlexibleDecimalJsonConverter"/> must accept either token type
/// and never lose precision — this is the converter every provider model relies on to keep
/// sub-cent prices (ANVL ~$0.0005326) and fractional units exact.
/// </summary>
public sealed class FlexibleDecimalJsonConverterTests
{
    private sealed class Wrapper
    {
        [System.Text.Json.Serialization.JsonConverter(typeof(FlexibleDecimalJsonConverter))]
        public decimal Value { get; set; }
    }

    private sealed class NullableWrapper
    {
        [System.Text.Json.Serialization.JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))]
        public decimal? Value { get; set; }
    }

    [Theory]
    [InlineData("""{"Value":"333.019989"}""")]
    [InlineData("""{"Value":333.019989}""")]
    public void Read_AcceptsStringOrNumber_AndParsesToExactDecimal(string json)
    {
        var result = JsonSerializer.Deserialize<Wrapper>(json);

        result!.Value.Should().Be(333.019989m);
    }

    [Fact]
    public void Read_SubCentString_SurvivesExactly()
    {
        const string json = """{"Value":"0.0005326"}""";

        var result = JsonSerializer.Deserialize<Wrapper>(json);

        result!.Value.Should().Be(0.0005326m);
    }

    [Fact]
    public void Read_LargeQuantityTimesSubCentPrice_MatchesHandComputedTotal()
    {
        // 1,000,000 units @ $0.0005326 = $532.60 exactly — the canonical example from CLAUDE.md.
        const string json = """{"Value":"0.0005326"}""";
        var price = JsonSerializer.Deserialize<Wrapper>(json)!.Value;

        (price * 1_000_000m).Should().Be(532.6000000m);
    }

    [Fact]
    public void NullableConverter_ReadsJsonNull_AsNull()
    {
        const string json = """{"Value":null}""";

        var result = JsonSerializer.Deserialize<NullableWrapper>(json);

        result!.Value.Should().BeNull();
    }
}
