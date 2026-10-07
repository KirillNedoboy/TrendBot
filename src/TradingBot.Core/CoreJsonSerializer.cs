using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>Serializes passive Core contracts using one deterministic JSON contract.</summary>
public static class CoreJsonSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    /// <summary>Serializes a Core contract as compact camelCase JSON.</summary>
    public static string Serialize<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        try
        {
            // A runtime-inferred derived MarketEvent still needs the base polymorphic contract
            // so every event carries the stable wire discriminator.
            Type serializationType = value is MarketEvent ? typeof(MarketEvent) : typeof(T);
            return JsonSerializer.Serialize(value, serializationType, SerializerOptions);
        }
        catch (JsonException)
        {
            throw;
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("The Core contract could not be serialized.", exception);
        }
    }

    /// <summary>Deserializes a Core contract and validates its domain invariants.</summary>
    public static T Deserialize<T>(string json)
    {
        if (json is null)
        {
            throw new JsonException("A Core contract JSON value cannot be null.");
        }

        try
        {
            T? value = typeof(MarketEvent).IsAssignableFrom(typeof(T)) && HasTypeDiscriminator(json)
                ? DeserializeMarketEvent<T>(json)
                : JsonSerializer.Deserialize<T>(json, SerializerOptions);
            return value is null
                ? throw new JsonException("A Core contract JSON value cannot be null.")
                : value;
        }
        catch (JsonException)
        {
            throw;
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("The Core contract violates a domain invariant.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new JsonException("The Core contract JSON is invalid.", exception);
        }
        catch (NotSupportedException exception)
        {
            throw new JsonException("The Core contract type is not supported.", exception);
        }
        catch (FormatException exception)
        {
            throw new JsonException("The Core contract JSON has an invalid format.", exception);
        }
        catch (OverflowException exception)
        {
            throw new JsonException("The Core contract JSON contains an out-of-range value.", exception);
        }
    }

    private static T? DeserializeMarketEvent<T>(string json)
    {
        MarketEvent? value = JsonSerializer.Deserialize<MarketEvent>(json, SerializerOptions);
        return value is T typedValue
            ? typedValue
            : throw new JsonException($"The JSON market-event type does not match {typeof(T).Name}.");
    }

    private static bool HasTypeDiscriminator(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("$type", out _);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new ImmutableValueListJsonConverterFactory());
        return options;
    }
}

internal sealed class ImmutableValueListJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert.IsGenericType
            && typeToConvert.GetGenericTypeDefinition() == typeof(ImmutableValueList<>);
    }

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        Type valueType = typeToConvert.GetGenericArguments()[0];
        Type converterType = typeof(ImmutableValueListJsonConverter<>).MakeGenericType(valueType);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}

internal sealed class ImmutableValueListJsonConverter<T> : JsonConverter<ImmutableValueList<T>>
{
    public override ImmutableValueList<T> Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            throw new JsonException("An immutable value list cannot be null.");
        }

        List<T>? values = JsonSerializer.Deserialize<List<T>>(ref reader, options);
        return values is null
            ? throw new JsonException("An immutable value list cannot be null.")
            : new ImmutableValueList<T>(values);
    }

    public override void Write(Utf8JsonWriter writer, ImmutableValueList<T> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (T item in value)
        {
            JsonSerializer.Serialize(writer, item, options);
        }

        writer.WriteEndArray();
    }
}
