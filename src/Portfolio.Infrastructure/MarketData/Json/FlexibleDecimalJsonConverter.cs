using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Portfolio.Infrastructure.MarketData.Json;

/// <summary>
/// Twelve Data is inconsistent about how it encodes numeric fields: <c>/quote</c> returns them as
/// JSON strings (<c>"close":"333.019989"</c>), while <c>/exchange_rate</c> returns a bare JSON
/// number (<c>"rate":1.29073</c>). Both must land in a <see cref="decimal"/> without an
/// intermediate <see cref="double"/> hop, or sub-cent prices like ANVL's ~$0.0005326 lose
/// precision. This converter accepts either token type and always parses with
/// <see cref="CultureInfo.InvariantCulture"/>.
/// </summary>
public sealed class FlexibleDecimalJsonConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => decimal.Parse(
                reader.GetString() ?? throw new JsonException("Expected a numeric string but got null."),
                NumberStyles.Float,
                CultureInfo.InvariantCulture),
            JsonTokenType.Number => reader.GetDecimal(),
            _ => throw new JsonException($"Cannot convert token type {reader.TokenType} to decimal."),
        };

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}

/// <summary>Nullable counterpart of <see cref="FlexibleDecimalJsonConverter"/>, for fields that
/// can legitimately be JSON <c>null</c> (Yahoo's close array on non-trading days).</summary>
public sealed class FlexibleNullableDecimalJsonConverter : JsonConverter<decimal?>
{
    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => decimal.Parse(
                reader.GetString() ?? throw new JsonException("Expected a numeric string but got null."),
                NumberStyles.Float,
                CultureInfo.InvariantCulture),
            JsonTokenType.Number => reader.GetDecimal(),
            _ => throw new JsonException($"Cannot convert token type {reader.TokenType} to decimal?."),
        };

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value is { } v)
        {
            writer.WriteNumberValue(v);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
