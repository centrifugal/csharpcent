using System.Text.Json;
using System.Text.Json.Serialization;

namespace Centrifugal.Cent;

/// <summary>
/// The endpoints the <c>getToken</c> and <c>getSignature</c> callbacks of client SDKs call
/// (spec 12), around an application function deciding who gets what.
/// </summary>
/// <remarks>
/// A success is a JSON object with <c>Cache-Control: no-store</c>. The function throws
/// <see cref="EndpointUnauthorizedException"/> - also wrapped - to answer 403: client SDKs
/// stop asking, and disconnect or unsubscribe. Any other exception answers 500, reported to
/// <see cref="EndpointHandlerOptions.OnError"/>: client SDKs retry. A malformed request is a
/// 400, also reported.
/// </remarks>
/// <example>
/// <code>
/// var endpoints = new EndpointHandlers();
/// app.MapCentrifugo("/centrifugo/connection_token", endpoints.ConnectionToken(context =>
/// {
///     var user = context.Request.GetHttpContext().User.Identity?.Name // Centrifugal.Cent.AspNetCore
///         ?? throw new EndpointUnauthorizedException();
///     return Task.FromResult(Token.Connection(key, new ConnectionClaims { User = user, Expiry = Expiry.After(TimeSpan.FromMinutes(15)) }));
/// }));
/// </code>
/// </example>
public sealed class EndpointHandlers
{
    private readonly EndpointHandlerOptions _options;

    /// <summary>Creates handlers sharing their options.</summary>
    /// <param name="options">The options; defaults when null.</param>
    public EndpointHandlers(EndpointHandlerOptions? options = null)
    {
        _options = options ?? new EndpointHandlerOptions();
        if (_options.MaxRequestBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), _options.MaxRequestBytes, "MaxRequestBytes is positive");
        }
    }

    /// <summary>The connection token endpoint: <c>GET</c> or <c>POST</c>, the body ignored; answers <c>{"token": ...}</c>.</summary>
    /// <param name="issue">Returns the connection token for the request's user.</param>
    /// <returns>The handler.</returns>
    public CentHandler ConnectionToken(Func<HandlerContext, Task<string>> issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return Create(async (request, context) =>
        {
            if (!Responses.IsPost(request.Method) && !request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                return Responses.MethodNotAllowed("GET, POST");
            }
            return Ok(new TokenAnswer { Token = await issue(context).ConfigureAwait(false) }, Wire.Info<TokenAnswer>());
        });
    }

    /// <summary>
    /// The subscription token endpoint: <c>POST {"channel": ...}</c> or <c>GET ?channel=...</c>,
    /// a channel required; answers <c>{"token": ...}</c>.
    /// </summary>
    /// <param name="issue">Returns the subscription token to the channel for the request's user.</param>
    /// <returns>The handler.</returns>
    public CentHandler SubscriptionToken(Func<string, HandlerContext, Task<string>> issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return Create(async (request, context) =>
        {
            string? channel;
            if (request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                channel = request.Query.GetValueOrDefault("channel");
            }
            else if (Responses.IsPost(request.Method))
            {
                channel = Decode(request, Wire.Info<ChannelRequest>(), "subscription token").Channel;
            }
            else
            {
                return Responses.MethodNotAllowed("GET, POST");
            }
            if (string.IsNullOrEmpty(channel))
            {
                throw Responses.Malformed("subscription token", new JsonException("no channel"));
            }
            return Ok(new TokenAnswer { Token = await issue(channel, context).ConfigureAwait(false) }, Wire.Info<TokenAnswer>());
        });
    }

    /// <summary>
    /// The shared poll signature endpoint: <c>POST {"channel": ..., "keys": [...]}</c>, a
    /// channel required; answers <c>{"signature": ..., "keys": [...]}</c>. Keys the function
    /// leaves out are revoked - the client stops tracking them - and a key the client did not
    /// ask for fails the request with 500.
    /// </summary>
    /// <param name="sign">Returns the keys the request's user may track, signed with <see cref="SharedPoll.Sign"/>.</param>
    /// <returns>The handler.</returns>
    public CentHandler SharedPollSignature(Func<string, IReadOnlyList<string>, HandlerContext, Task<SignedKeys>> sign)
    {
        ArgumentNullException.ThrowIfNull(sign);
        return Create(async (request, context) =>
        {
            if (!Responses.IsPost(request.Method))
            {
                return Responses.MethodNotAllowed("POST");
            }
            var asked = Decode(request, Wire.Info<SignatureRequest>(), "shared poll signature");
            if (string.IsNullOrEmpty(asked.Channel))
            {
                throw Responses.Malformed("shared poll signature", new JsonException("no channel"));
            }
            var signed = await sign(asked.Channel, asked.Keys, context).ConfigureAwait(false)
                ?? throw new InvalidOperationException("the shared poll signature function returned null");
            var allowed = asked.Keys.ToHashSet(StringComparer.Ordinal);
            if (signed.Keys.FirstOrDefault(k => !allowed.Contains(k)) is { } extra)
            {
                throw new InvalidOperationException($"the shared poll signature function signed the key \"{extra}\", which the client did not ask for");
            }
            return Ok(signed, Wire.Info<SignedKeys>());
        });
    }

    private CentHandler Create(Func<HandlerRequest, HandlerContext, Task<HandlerResponse>> handle)
    {
        var onError = _options.OnError;
        return new CentHandler(_options.MaxRequestBytes, async (request, cancellationToken) =>
        {
            try
            {
                return await handle(request, new HandlerContext(request, cancellationToken)).ConfigureAwait(false);
            }
            catch (Exception e) when (Responses.Find<EndpointUnauthorizedException>(e) is not null)
            {
                return Responses.Text(403, "forbidden");
            }
            catch (JsonException e)
            {
                return Responses.Fail(request, onError, 400, e);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return Responses.Fail(request, onError, 500, e);
            }
        });
    }

    private static readonly IReadOnlyDictionary<string, string> NoStore =
        Responses.Headers(("Content-Type", "application/json"), ("Cache-Control", "no-store"));

    private static HandlerResponse Ok<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        Responses.Json(JsonSerializer.SerializeToUtf8Bytes(value, info), NoStore);

    private static T Decode<T>(HandlerRequest request, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, string what)
    {
        try
        {
            return JsonSerializer.Deserialize(request.Body.Span, info) ?? throw Responses.Malformed(what);
        }
        catch (JsonException e)
        {
            throw Responses.Malformed(what, e);
        }
    }
}

/// <summary>The options of <see cref="EndpointHandlers"/>.</summary>
public sealed class EndpointHandlerOptions
{
    /// <summary>Told of every request answered with 500 or 400, with why. It must not throw.</summary>
    public Action<HandlerRequest, Exception>? OnError { get; set; }

    /// <summary>Bounds the body of a request: 1 MiB by default.</summary>
    public long MaxRequestBytes { get; set; } = 1 << 20;
}

/// <summary>
/// The request's user may not have what it asked for: the endpoint answers 403, and the
/// client SDK stops asking - it disconnects or unsubscribes. Distinct from the API client's
/// <see cref="CentUnauthorizedException"/>, which means Centrifugo refused the SDK's credentials.
/// </summary>
public sealed class EndpointUnauthorizedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public EndpointUnauthorizedException()
        : base("unauthorized")
    {
    }

    /// <summary>Creates the exception, saying why - for the application's logs; the client receives a plain 403.</summary>
    /// <param name="message">Why.</param>
    public EndpointUnauthorizedException(string message)
        : base(message)
    {
    }
}

/// <summary>A shared poll signature with the keys it covers: what the signature endpoint answers.</summary>
public sealed class SignedKeys
{
    /// <summary>Creates the answer.</summary>
    /// <param name="signature">The signature, from <see cref="SharedPoll.Sign"/>.</param>
    /// <param name="keys">The keys it covers, in the order signed.</param>
    [JsonConstructor]
    public SignedKeys(string signature, IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(keys);
        Signature = signature;
        Keys = keys;
    }

    /// <summary>The signature.</summary>
    [JsonPropertyName("signature")]
    public string Signature { get; }

    /// <summary>The keys it covers: always written, an empty array included.</summary>
    [JsonPropertyName("keys")]
    public IReadOnlyList<string> Keys { get; }
}

internal sealed class TokenAnswer
{
    [JsonPropertyName("token")]
    public string Token { get; init; } = "";
}

internal sealed class ChannelRequest
{
    [JsonPropertyName("channel")]
    public string? Channel { get; init; }
}

internal sealed class SignatureRequest
{
    [JsonPropertyName("channel")]
    public string? Channel { get; init; }

    [JsonPropertyName("keys")]
    public IReadOnlyList<string> Keys { get; init => field = value ?? []; } = [];
}
