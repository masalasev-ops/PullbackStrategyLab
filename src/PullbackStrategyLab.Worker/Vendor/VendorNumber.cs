using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PullbackStrategyLab.Worker.Vendor;

/// <summary>
/// A number the vendor may answer with a word.
///
/// EODHD returns <c>"NA"</c> for a figure it does not hold, in a field it fills with a JSON number
/// everywhere else. <c>JsonSerializerDefaults.Web</c> reads a number from a string, so a quoted
/// <c>"12481812480"</c> would have been fine and <c>"NA"</c> throws mid-deserialization, which took
/// the whole stage down with it: the sector walk on 2026-08-27 asked 149 names, resolved 148, and
/// died on the 149th, whose capitalisation came back as <c>"NA"</c>.
///
/// <b>The absence is the answer, so it reads as null rather than as a failure.</b> A name the vendor
/// holds no capitalisation for is a name with no capitalisation, which is a thing the lab already
/// records and already handles: <c>SectorResolver</c> stamps it so the question is not asked again,
/// and the short side's market-cap gate fails a name with no resolved capitalisation. Treating it as
/// a malformed document instead made an ordinary answer fatal.
///
/// <b>What it does not do is guess.</b> Only a string that is empty, whitespace, or one of the
/// vendor's own absence words reads as null. Any other unparseable string still throws, because a
/// converter that swallowed everything would turn a genuine change in the vendor's shape into a
/// column of quiet nulls, which is the failure this whole class of bug is made of.
/// </summary>
public sealed class TolerantDecimalConverter : JsonConverter<decimal?>
{
    /// <summary>The vendor's own words for a figure it does not hold. Compared case-insensitively.</summary>
    private static readonly string[] Absent = ["NA", "N/A", "None", "null", "-"];

    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.Number:
                return reader.GetDecimal();

            case JsonTokenType.String:
                string? text = reader.GetString();

                if (string.IsNullOrWhiteSpace(text)
                    || Absent.Contains(text.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    return null;
                }

                if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed))
                {
                    return parsed;
                }

                throw new JsonException(
                    $"The vendor answered '{text}' where a number was expected, and it is not one of its own "
                    + "words for a figure it does not hold. That is a change in the vendor's shape rather than a "
                    + "missing value, and reading it as absent would hide it.");

            default:
                throw new JsonException($"Expected a number or a string and read {reader.TokenType}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value is decimal number)
        {
            writer.WriteNumberValue(number);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
