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

/// <summary>The rules the client checks before sending (spec 3.5): no other rule of Centrifugo's is copied.</summary>
internal static class Validation
{
    public static void Publish(PublishRequest r)
    {
        if (string.IsNullOrEmpty(r.Channel))
        {
            throw new CentInvalidRequestException("publish", "no channel");
        }
        if (r.Data is null && string.IsNullOrEmpty(r.B64Data))
        {
            throw new CentInvalidRequestException("publish", "neither Data nor B64Data is set; JSON null is RawJson.Null");
        }
    }

    public static void Broadcast(BroadcastRequest r)
    {
        if (r.Channels is null || r.Channels.Count == 0)
        {
            throw new CentInvalidRequestException("broadcast", "no channels");
        }
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
