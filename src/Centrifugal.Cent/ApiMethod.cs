using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Centrifugal.Cent;

/// <summary>An API method: its name, whether only Centrifugo PRO has it, its request's and result's JSON, and the rules the client checks.</summary>
internal sealed class ApiMethod<TRequest, TResult>(
    string name,
    bool proOnly,
    JsonTypeInfo<TRequest> request,
    JsonTypeInfo<TResult> result,
    Action<TRequest>? validate)
{
    public string Name { get; } = name;

    public bool ProOnly { get; } = proOnly;

    /// <summary>Validates and encodes a request: <see cref="CentInvalidRequestException"/> when it cannot be sent (spec 3.5).</summary>
    public byte[] Encode(TRequest request1)
    {
        if (request1 is null)
        {
            throw new CentInvalidRequestException(Name, "no request");
        }
        if (request1 is IWireMessage m && m.Missing() is { } missing)
        {
            throw new CentInvalidRequestException(Name, missing + " is required");
        }
        validate?.Invoke(request1);
        return Wire.Encode(Name, request1, request);
    }

    /// <summary>Decodes a result: <see cref="CentDecodeException"/> when it is not of the result's shape.</summary>
    public TResult Decode(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new CentDecodeException(Name, $"the result is a JSON {element.ValueKind.ToString().ToLowerInvariant()}, not an object");
        }
        try
        {
            return element.Deserialize(result)!;
        }
        catch (JsonException e)
        {
            throw new CentDecodeException(Name, e.Message, e);
        }
    }
}

/// <summary>
/// A message of the protos which the SDK sends - a request, a message in one, a proxy
/// result: generated for each.
/// </summary>
internal interface IWireMessage
{
    /// <summary>
    /// Whether every field is at its zero value: the message is then left out of the one
    /// holding it, as a zero field is (spec 2). Never for a presence wrapper, which is sent
    /// whenever it is set, even empty.
    /// </summary>
    bool IsZero { get; }

    /// <summary>
    /// The path of a field the protos mark "Required." which the message leaves unset -
    /// <c>Channel</c>, <c>LimitStrategy.RateLimit.Policies[0].Rate</c> - or null. A field whose
    /// empty value has a meaning is not checked; a nested message is checked when it is set
    /// (spec 3.5).
    /// </summary>
    string? Missing();
}

/// <summary>The checks of messages held by others, for the generated code.</summary>
internal static class WireMessage
{
    public static bool IsZero(IWireMessage? m) => m is null || m.IsZero;

    /// <summary>The path of an unset required field in a nested message, when it is set.</summary>
    public static string? Missing(IWireMessage? m) => IsZero(m) ? null : m!.Missing();

    /// <summary>The path of an unset required field in a list's elements: <c>[0].Rate</c>.</summary>
    public static string? MissingIn<T>(IList<T>? list)
        where T : class, IWireMessage
    {
        for (var i = 0; list is not null && i < list.Count; i++)
        {
            if (list[i] is not { } element)
            {
                return $"[{i}]";
            }
            if (element.Missing() is { } missing)
            {
                return $"[{i}].{missing}";
            }
        }
        return null;
    }
}

/// <summary>
/// The rules the client checks before sending beyond the required fields (spec 3.5): no
/// other rule of Centrifugo's is copied.
/// </summary>
internal static class Validation
{
    public static void Publish(PublishRequest r)
    {
        if (r.Data is null && string.IsNullOrEmpty(r.B64Data))
        {
            throw new CentInvalidRequestException("publish", "neither Data nor B64Data is set; JSON null is RawJson.Null");
        }
    }

    public static void Broadcast(BroadcastRequest r)
    {
        for (var i = 0; i < r.Channels.Count; i++)
        {
            if (string.IsNullOrEmpty(r.Channels[i]))
            {
                throw new CentInvalidRequestException("broadcast", $"channel {i} is empty");
            }
        }
        if (r.Data is null && string.IsNullOrEmpty(r.B64Data))
        {
            throw new CentInvalidRequestException("broadcast", "neither Data nor B64Data is set; JSON null is RawJson.Null");
        }
    }
}
