using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Centrifugal.Cent;

/// <summary>
/// JSON text sent as the same JSON value, number text kept, never decoded and encoded
/// again (spec 3.4): JSON the application already has - from a database, a queue, a
/// cache - or encoded with options of its own.
/// </summary>
/// <remarks>
/// It is checked to be one valid JSON value when it is made. <see cref="ToString"/> is
/// the JSON text.
/// <code>
/// var request = new PublishRequest { Channel = "news", Data = RawJson.Parse(json) };
/// </code>
/// </remarks>
public sealed class RawJson : IEquatable<RawJson>
{
    private readonly byte[] _utf8;

    private RawJson(byte[] utf8) => _utf8 = utf8;

    /// <summary>JSON <c>null</c>: a payload of its own, distinct from leaving a payload unset.</summary>
    public static RawJson Null { get; } = new("null"u8.ToArray());

    /// <summary>The JSON text, UTF-8 encoded.</summary>
    public ReadOnlyMemory<byte> Utf8 => _utf8;

    /// <summary>Takes JSON text.</summary>
    /// <param name="json">One JSON value.</param>
    /// <returns>The raw JSON.</returns>
    /// <exception cref="ArgumentException"><paramref name="json"/> is not one valid JSON value.</exception>
    public static RawJson Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        StrictStringConverter.Check(json);
        return Parse(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Takes UTF-8 JSON text.</summary>
    /// <param name="utf8Json">One JSON value, UTF-8 encoded.</param>
    /// <returns>The raw JSON.</returns>
    /// <exception cref="ArgumentException"><paramref name="utf8Json"/> is not one valid JSON value.</exception>
    public static RawJson Parse(ReadOnlySpan<byte> utf8Json)
    {
        Validate(utf8Json);
        return new RawJson(utf8Json.ToArray());
    }

    /// <summary>
    /// Encodes a value with the metadata of a <see cref="JsonSerializerContext"/>: the way to
    /// send an application's type from trimmed and native AOT applications, and to encode
    /// it with options of the application's own.
    /// </summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="typeInfo">Its JSON metadata, such as <c>MyJsonContext.Default.Message</c>.</param>
    /// <returns>The encoded value.</returns>
    public static RawJson Serialize<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return new RawJson(JsonSerializer.SerializeToUtf8Bytes(value, typeInfo));
    }

    private static void Validate(ReadOnlySpan<byte> utf8Json)
    {
        // The reader does not check the bytes of strings it is not asked to read.
        if (!System.Text.Unicode.Utf8.IsValid(utf8Json))
        {
            throw new ArgumentException("raw JSON which is not valid UTF-8", nameof(utf8Json));
        }
        try
        {
            var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
            if (!reader.Read())
            {
                throw new ArgumentException("raw JSON is empty", nameof(utf8Json));
            }
            reader.Skip();
            // Anything after the value is refused by the reader itself.
            reader.Read();
        }
        catch (JsonException e)
        {
            throw new ArgumentException($"not valid JSON: {e.Message}", nameof(utf8Json), e);
        }
    }

    /// <summary>The JSON text.</summary>
    /// <returns>The JSON text.</returns>
    public override string ToString() => Encoding.UTF8.GetString(_utf8);

    /// <inheritdoc/>
    public bool Equals(RawJson? other) => other is not null && _utf8.AsSpan().SequenceEqual(other._utf8);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RawJson r && Equals(r);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var h = new HashCode();
        h.AddBytes(_utf8);
        return h.ToHashCode();
    }

    internal void WriteTo(Utf8JsonWriter writer) => writer.WriteRawValue(_utf8, skipInputValidation: true);
}

/// <summary>Decodes payloads into an application's types.</summary>
public static class PayloadExtensions
{
    /// <summary>
    /// Decodes a payload as a <typeparamref name="T"/>, as the SDK encodes one: with
    /// <see cref="JsonSerializerDefaults.Web"/> - camelCase names, read case-insensitively -
    /// as ASP.NET Core and SignalR do.
    /// </summary>
    /// <typeparam name="T">The type to decode into.</typeparam>
    /// <param name="payload">A payload of a reply or a proxy request.</param>
    /// <returns>The decoded value; <c>default</c> for JSON <c>null</c>.</returns>
    /// <exception cref="JsonException">The payload is not a <typeparamref name="T"/>.</exception>
    [RequiresUnreferencedCode("Decodes through reflection. Use the overload taking a JsonTypeInfo<T> in trimmed and native AOT applications.")]
    [RequiresDynamicCode("Decodes through reflection. Use the overload taking a JsonTypeInfo<T> in trimmed and native AOT applications.")]
    public static T? Decode<T>(this JsonElement payload) => payload.Deserialize<T>(Payload.Options);

    /// <summary>Decodes a payload with the metadata of a <see cref="JsonSerializerContext"/>.</summary>
    /// <typeparam name="T">The type to decode into.</typeparam>
    /// <param name="payload">A payload of a reply or a proxy request.</param>
    /// <param name="typeInfo">The metadata, such as <c>MyJsonContext.Default.Message</c>.</param>
    /// <returns>The decoded value; <c>default</c> for JSON <c>null</c>.</returns>
    /// <exception cref="JsonException">The payload is not a <typeparamref name="T"/>.</exception>
    public static T? Decode<T>(this JsonElement payload, JsonTypeInfo<T> typeInfo) => payload.Deserialize(typeInfo);
}

/// <summary>How payloads are encoded and decoded.</summary>
internal static class Payload
{
    /// <summary>
    /// Web defaults, as ASP.NET Core and SignalR encode what goes to browsers: camelCase
    /// names, read case-insensitively. NaN and infinities are refused, as the defaults do.
    /// </summary>
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { StrictStringConverter.Instance },
        };
#pragma warning disable IL2026, IL3050 // Guarded: reflection is only used where it is enabled.
        o.TypeInfoResolver = JsonSerializer.IsReflectionEnabledByDefault
            ? new DefaultJsonTypeInfoResolver()
            : JsonTypeInfoResolver.Combine();
#pragma warning restore IL2026, IL3050
        o.MakeReadOnly();
        return o;
    }

    /// <summary>Writes a payload a request was given (spec 3.4).</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "An application's own type is encoded through reflection where reflection is enabled; trimmed and native AOT applications pass JsonElement, JsonNode or RawJson.Serialize, and a type reflection cannot see fails the call with an InvalidRequest error saying so.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "As above.")]
    public static void Write(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case RawJson raw:
                raw.WriteTo(writer);
                break;
            case JsonElement e:
                if (e.ValueKind == JsonValueKind.Undefined)
                {
                    throw new ArgumentException("an undefined JsonElement (default(JsonElement)) is not a payload");
                }
                e.WriteTo(writer);
                break;
            case JsonNode n:
                n.WriteTo(writer, Options);
                break;
            case string s:
                StrictStringConverter.Check(s);
                writer.WriteStringValue(s);
                break;
            case byte[] or Memory<byte> or ReadOnlyMemory<byte> or ArraySegment<byte> or Stream:
                throw new ArgumentException(
                    "bytes are not a JSON payload: send binary data base64-encoded in a b64 field (B64Data, B64Info), " +
                    "or JSON text as RawJson.Parse");
            default:
                if (!JsonSerializer.IsReflectionEnabledByDefault)
                {
                    throw new NotSupportedException(
                        $"a {value.GetType().Name} cannot be encoded without reflection: pass a JsonElement or JsonNode, " +
                        "or RawJson.Serialize(value, MyJsonContext.Default.Type)");
                }
                JsonSerializer.Serialize(writer, value, value.GetType(), Options);
                break;
        }
    }
}

/// <summary>
/// A payload of a request: any JSON value (spec 3.4) - an application's object, an
/// anonymous type, a <see cref="JsonElement"/> or <see cref="JsonNode"/>, a string,
/// number or boolean, or <see cref="RawJson"/>. Read back, it is a <see cref="JsonElement"/>.
/// </summary>
internal sealed class PayloadConverter : JsonConverter<object?>
{
    public override bool HandleNull => true;

    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonElement.ParseValue(ref reader);

    public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }
        Payload.Write(writer, value);
    }
}

/// <summary>
/// A payload of a reply or a proxy request: <c>null</c> when absent, a
/// <see cref="JsonElement"/> otherwise - JSON <c>null</c> included, of
/// <see cref="JsonValueKind.Null"/> - so that it goes back as the same JSON value.
/// </summary>
internal sealed class ReplyPayloadConverter : JsonConverter<JsonElement?>
{
    public override bool HandleNull => true;

    public override JsonElement? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonElement.ParseValue(ref reader);

    public override void Write(Utf8JsonWriter writer, JsonElement? value, JsonSerializerOptions options)
    {
        if (value is { } e)
        {
            e.WriteTo(writer);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
