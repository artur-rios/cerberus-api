using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArturRios.Cerberus.WebApi;

public sealed class CanonicalGuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException();
        var text = reader.GetString();
        if (!Guid.TryParseExact(text, "D", out var value) || value == Guid.Empty || value.ToString("D") != text)
            throw new JsonException();
        return value;
    }

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
