using System.Collections;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Centrifugal.Cent.Proxy;

namespace Centrifugal.Cent;

/// <summary>
/// How the SDK's types go on the wire: field names exactly the protos' (case-sensitive),
/// zero values not sent, <c>null</c> read as the zero value, payloads kept as they are.
/// </summary>
internal static class Wire
{
    /// <summary>The options of every request, reply and proxy message, read-only.</summary>
    public static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>The JSON metadata of a type of the SDK.</summary>
    public static JsonTypeInfo<T> Info<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            // Zero values are left out of the protos' types only: the SDK's own say what they
            // write - a shared poll signature's keys, an empty array included.
            TypeInfoResolver = JsonTypeInfoResolver.Combine(
                ApiJsonContext.Default.WithAddedModifier(LeaveOutZeroValues),
                ProxyJsonContext.Default.WithAddedModifier(LeaveOutZeroValues),
                HandwrittenJsonContext.Default),
            // Non-ASCII text as it is: escaping it would triple the size of payloads in
            // most languages. What is sent is JSON for Centrifugo, never HTML.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters =
            {
                StrictStringConverter.Instance,
                new NullAsZeroConverter<bool>(),
                new NullAsZeroConverter<int>(),
                new NullAsZeroConverter<long>(),
                new NullAsZeroConverter<uint>(),
                new NullAsZeroConverter<ulong>(),
                new NullAsZeroConverter<double>(),
                new NullAsZeroConverter<float>(),
            },
        };
        options.MakeReadOnly();
        return options;
    }

    /// <summary>
    /// A field left at its zero value is not sent (spec 2). A nullable field -
    /// a payload, a message, a <c>BoolValue</c> - is left out only when null: a
    /// <c>false</c> it holds is sent.
    /// </summary>
    private static void LeaveOutZeroValues(JsonTypeInfo type)
    {
        if (type.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }
        foreach (var p in type.Properties)
        {
            var t = p.PropertyType;
            if (t == typeof(string))
            {
                p.ShouldSerialize = static (_, v) => v is string { Length: > 0 };
            }
            else if (t.IsValueType && Nullable.GetUnderlyingType(t) is null)
            {
                p.ShouldSerialize = static (_, v) => !IsZero(v);
            }
            else if (t != typeof(object) && typeof(IEnumerable).IsAssignableFrom(t))
            {
                p.ShouldSerialize = static (_, v) => v switch
                {
                    null => false,
                    ICollection c => c.Count > 0,
                    IEnumerable e => e.GetEnumerator().MoveNext(),
                    _ => true,
                };
            }
            else
            {
                p.ShouldSerialize = static (_, v) => v is not null;
            }
        }
    }

    private static bool IsZero(object? v) => v switch
    {
        null => true,
        bool b => !b,
        int i => i == 0,
        long l => l == 0,
        uint u => u == 0,
        ulong u => u == 0,
        double d => d == 0,
        float f => f == 0,
        _ => false,
    };

    /// <summary>Encodes a request for its method, as <see cref="CentInvalidRequestException"/> when it cannot be.</summary>
    public static byte[] Encode<T>(string method, T request, JsonTypeInfo<T> info)
    {
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(request, info);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            throw new CentInvalidRequestException(method, e.Message, e);
        }
    }
}

/// <summary>Types written by hand which go on the wire.</summary>
[JsonSerializable(typeof(BroadcastWire))]
[JsonSerializable(typeof(ApiErrorWire))]
[JsonSerializable(typeof(Command))]
[JsonSerializable(typeof(SignedKeys))]
[JsonSerializable(typeof(TokenAnswer))]
[JsonSerializable(typeof(ChannelRequest))]
[JsonSerializable(typeof(SignatureRequest))]
internal sealed partial class HandwrittenJsonContext : JsonSerializerContext;

/// <summary>
/// Refuses a string which is not valid UTF-16 - an unpaired surrogate - rather than
/// sending it changed: the encoder would write U+FFFD in its place (spec 3.5).
/// </summary>
internal sealed class StrictStringConverter : JsonConverter<string>
{
    public static readonly StrictStringConverter Instance = new();

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : reader.GetString();

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        Check(value);
        writer.WriteStringValue(value);
    }

    // Map keys - tags, metrics, a payload's dictionary - are strings too.
    public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString()!;

    public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        Check(value);
        writer.WritePropertyName(value);
    }

    public static void Check(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                throw new ArgumentException($"a string with an unpaired surrogate (U+{(int)c:X4}) at index {i}: it is not valid Unicode, and cannot be sent as it is");
            }
        }
    }
}

/// <summary>Reads <c>null</c> as the zero value: a server between Centrifugo and the SDK may write zero values so.</summary>
internal sealed class NullAsZeroConverter<T> : JsonConverter<T>
    where T : struct
{
    public override bool HandleNull => true;

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return default;
        }
        object value = typeof(T) switch
        {
            var t when t == typeof(bool) => reader.GetBoolean(),
            var t when t == typeof(int) => reader.GetInt32(),
            var t when t == typeof(long) => reader.GetInt64(),
            var t when t == typeof(uint) => reader.GetUInt32(),
            var t when t == typeof(ulong) => reader.GetUInt64(),
            var t when t == typeof(double) => reader.GetDouble(),
            var t when t == typeof(float) => reader.GetSingle(),
            _ => throw new NotSupportedException(typeof(T).Name),
        };
        return (T)value;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case uint u:
                writer.WriteNumberValue(u);
                break;
            case ulong u:
                writer.WriteNumberValue(u);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            default:
                throw new NotSupportedException(typeof(T).Name);
        }
    }
}

/// <summary>
/// A wrapper message - <c>BoolValue</c>, <c>Int32Value</c> - as a nullable of its value:
/// <c>null</c> leaves the server's setting alone, which <c>false</c> or a zero cannot say
/// (spec 2). On the wire it is <c>{"value": ...}</c>.
/// </summary>
internal sealed class WrapperConverter<T> : JsonConverter<T?>
    where T : struct
{
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"a {typeof(T).Name} wrapper is not an object");
        }
        T value = default;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            reader.Read();
            if (name == "value" && reader.TokenType != JsonTokenType.Null)
            {
                value = typeof(T) == typeof(bool) ? (T)(object)reader.GetBoolean() : (T)(object)reader.GetInt32();
            }
            else
            {
                reader.Skip();
            }
        }
        return value;
    }

    public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options)
    {
        if (value is not { } v)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        writer.WritePropertyName("value");
        if (v is bool b)
        {
            writer.WriteBooleanValue(b);
        }
        else
        {
            writer.WriteNumberValue((int)(object)v);
        }
        writer.WriteEndObject();
    }
}
