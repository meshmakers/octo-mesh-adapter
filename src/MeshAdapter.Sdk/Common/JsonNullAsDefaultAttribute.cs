using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Common;

/// <summary>
/// Reads a JSON null on an <see cref="int" /> property as the given default rather than failing.
/// <para>
/// Settings records are deserialized from a tenant's GlobalConfiguration entry, and that payload
/// is the serialized CK entity: every attribute the CK type declares is a key, and an optional
/// attribute nobody filled in carries null. A non-nullable <see cref="int" /> keeps its property
/// initializer only when the key is <em>absent</em>, so an unset optional attribute does not fall
/// back to the default - it fails the whole pipeline with "The JSON value could not be converted
/// to System.Int32". This attribute makes "key absent" and "key present but null" mean the same
/// thing: not configured.
/// </para>
/// <para>
/// It has to sit on the property because the deserialization happens inside
/// <c>IGlobalConfiguration.GetValue&lt;T&gt;</c>, which builds its own
/// <see cref="JsonSerializerOptions" /> - a converter registered anywhere else never reaches this
/// payload. The default is passed in because a converter cannot read a property initializer.
/// </para>
/// </summary>
/// <param name="defaultValue">
/// The value an unset attribute resolves to - pass the property's own initializer, so the two
/// cannot drift apart
/// </param>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class JsonNullAsDefaultAttribute(int defaultValue) : JsonConverterAttribute
{
    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert)
    {
        return new NullAsDefaultInt32Converter(defaultValue);
    }

    private sealed class NullAsDefaultInt32Converter(int defaultValue) : JsonConverter<int>
    {
        /// <summary>
        /// States what a converter over a non-nullable value type gets by default anyway: the
        /// null token reaches <see cref="Read" /> instead of being rejected before it. The whole
        /// point of this converter rests on that, and it would flip to false without a word if
        /// the converted type ever became nullable.
        /// </summary>
        public override bool HandleNull => true;

        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return defaultValue;
            }

            // Null is the one token that means "not configured". Everything else stays with the
            // built-in behaviour, so a broken entry keeps failing with its usual message and
            // JSON path instead of quietly resolving to the default.
            return JsonSerializer.Deserialize<int>(ref reader, options);
        }

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value);
        }
    }
}
