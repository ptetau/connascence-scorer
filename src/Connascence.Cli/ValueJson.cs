using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Connascence.Cli
{
    /// <summary>
    /// JSON for Core's value-equal collections: a ValueList is an array, a ValueMap is an object, null is empty.
    /// They live here, in the shell, so Core stays free of serialization (and builds for the netstandard2.0 analyzer).
    /// </summary>
    public sealed class ValueCollectionsJson : JsonConverterFactory
    {
        public override bool CanConvert(Type type) =>
            type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(ValueList<>) || type.GetGenericTypeDefinition() == typeof(ValueMap<>));

        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options)
        {
            var item = type.GetGenericArguments()[0];
            var converter = type.GetGenericTypeDefinition() == typeof(ValueList<>)
                ? typeof(ListConverter<>).MakeGenericType(item)
                : typeof(MapConverter<>).MakeGenericType(item);
            return (JsonConverter)Activator.CreateInstance(converter);
        }

        sealed class ListConverter<T> : JsonConverter<ValueList<T>>
        {
            public override bool HandleNull => true;

            public override ValueList<T> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
                reader.TokenType == JsonTokenType.Null ? default : (JsonSerializer.Deserialize<List<T>>(ref reader, options) ?? new List<T>()).ToValueList();

            public override void Write(Utf8JsonWriter writer, ValueList<T> value, JsonSerializerOptions options) =>
                JsonSerializer.Serialize(writer, value.Items.ToList(), options);
        }

        sealed class MapConverter<T> : JsonConverter<ValueMap<T>>
        {
            public override bool HandleNull => true;

            // Keys are written as given: they are names (Meaning, SameType), not properties for the naming policy.
            public override ValueMap<T> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
                reader.TokenType == JsonTokenType.Null ? default
                    : (JsonSerializer.Deserialize<List<KeyValuePair<string, T>>>(ref reader, Ordered(options)) ?? new List<KeyValuePair<string, T>>()).ToValueMap();

            public override void Write(Utf8JsonWriter writer, ValueMap<T> value, JsonSerializerOptions options)
            {
                writer.WriteStartObject();
                foreach (var entry in value)
                {
                    writer.WritePropertyName(entry.Key);
                    JsonSerializer.Serialize(writer, entry.Value, options);
                }
                writer.WriteEndObject();
            }

            // A JSON object read as its entries, in file order.
            static JsonSerializerOptions Ordered(JsonSerializerOptions options) =>
                new JsonSerializerOptions(options) { Converters = { new EntriesConverter() } };

            sealed class EntriesConverter : JsonConverter<List<KeyValuePair<string, T>>>
            {
                public override List<KeyValuePair<string, T>> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
                {
                    if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("expected an object of names and values");
                    var entries = new List<KeyValuePair<string, T>>();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        var key = reader.GetString();
                        reader.Read();
                        entries.Add(new KeyValuePair<string, T>(key, JsonSerializer.Deserialize<T>(ref reader, options)));
                    }
                    return entries;
                }

                public override void Write(Utf8JsonWriter writer, List<KeyValuePair<string, T>> value, JsonSerializerOptions options) =>
                    throw new NotSupportedException();
            }
        }
    }
}
