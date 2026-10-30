using System.Buffers;
using System.Text.Json;

namespace Centrifugal.Cent.Tokens;

/// <summary>
/// The claims of a Centrifugo PRO API token (spec 11.4).
/// </summary>
/// <remarks>
/// Centrifugo PRO accepts a token signed with any key of its JWKS endpoint, and checks
/// audience, issuer and scope only when configured to. So sign API tokens with keys of
/// their own, publish them at a JWKS endpoint other than the client tokens' one, and
/// configure Centrifugo PRO to require an audience or scope only API tokens carry:
/// otherwise any user can call the server API with their connection token.
/// </remarks>
public sealed class ApiTokenClaims
{
    /// <summary>The <c>aud</c> claim: what Centrifugo PRO's <c>http_api.jwks.audience</c> requires.</summary>
    public string? Audience { get; init; }

    /// <summary>The scopes, joined by spaces into the <c>scope</c> claim: what <c>http_api.jwks.scope</c> requires. None empty or with a space.</summary>
    public IReadOnlyList<string>? Scopes { get; init; }

    /// <summary>The <c>iss</c> claim.</summary>
    public string? Issuer { get; init; }

    /// <summary>Who calls, for Centrifugo's logs: the <c>sub</c> claim.</summary>
    public string? Subject { get; init; }

    /// <summary>When the token stops being accepted: required by <see cref="Token.Api"/>; set by <see cref="ApiTokenSource"/> itself.</summary>
    public Expiry? Expiry { get; init; }

    /// <summary>When the token was issued: now by default.</summary>
    public DateTimeOffset? IssuedAt { get; init; }

    /// <summary>The token's ID: the <c>jti</c> claim.</summary>
    public string? Id { get; init; }

    internal static string? JoinScopes(IReadOnlyList<string>? scopes)
    {
        if (scopes is not { Count: > 0 })
        {
            return null;
        }
        foreach (var s in scopes)
        {
            if (string.IsNullOrEmpty(s) || s.Any(char.IsWhiteSpace))
            {
                throw new CentTokenException($"the scope \"{s}\" is empty or has a space: scopes are joined by spaces");
            }
        }
        return string.Join(' ', scopes);
    }
}

/// <summary>
/// Centrifugo PRO API tokens for a client's <see cref="CentClientOptions.BearerTokenFunc"/>:
/// it signs one, and reuses it for half its lifetime. Safe for concurrent use.
/// </summary>
/// <example>
/// <code>
/// var tokens = new ApiTokenSource(key, new ApiTokenClaims { Audience = "centrifugo-api", Subject = "billing" });
/// var client = new CentClient(new CentClientOptions { ApiEndpoint = endpoint, BearerTokenFunc = tokens.GetTokenAsync });
/// </code>
/// </example>
public sealed class ApiTokenSource
{
    private readonly Func<SigningKey> _key;
    private readonly ApiTokenClaims _claims;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private string? _token;
    private DateTimeOffset _renewAt;
    private SigningKey? _signedWith;

    /// <summary>Signs with a key.</summary>
    /// <param name="key">An RSA, ECDSA or Ed25519 key with an ID.</param>
    /// <param name="claims">The claims, without an expiry or issue time: the source sets them.</param>
    /// <param name="ttl">The lifetime of each token: 5 minutes by default.</param>
    /// <param name="timeProvider">The time; the system's by default.</param>
    public ApiTokenSource(SigningKey key, ApiTokenClaims claims, TimeSpan? ttl = null, TimeProvider? timeProvider = null)
        : this(Constant(key), claims, ttl, timeProvider)
    {
    }

    /// <summary>Signs with the key a function returns, for rotation: a new key is used from the next token.</summary>
    /// <param name="key">Returns the current key.</param>
    /// <param name="claims">The claims, without an expiry or issue time: the source sets them.</param>
    /// <param name="ttl">The lifetime of each token: 5 minutes by default.</param>
    /// <param name="timeProvider">The time; the system's by default.</param>
    public ApiTokenSource(Func<SigningKey> key, ApiTokenClaims claims, TimeSpan? ttl = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(claims);
        if (claims.Expiry is not null || claims.IssuedAt is not null)
        {
            throw new CentTokenException("an ApiTokenSource sets each token's expiry and issue time: leave them out of its claims, and give its ttl");
        }
        if (ttl is { } t && t <= TimeSpan.Zero)
        {
            throw new CentTokenException("an ApiTokenSource's ttl is positive");
        }
        _key = key;
        _claims = claims;
        _ttl = ttl ?? TimeSpan.FromMinutes(5);
        _time = timeProvider ?? TimeProvider.System;
        // Fails now for a key or claims no token could be signed with.
        Token.Api(key(), Claims(_time.GetUtcNow()));
    }

    private static Func<SigningKey> Constant(SigningKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return () => key;
    }

    /// <summary>A token: the current one until half its lifetime has passed, then a new one.</summary>
    /// <param name="cancellationToken">Unused: signing does not wait.</param>
    /// <returns>The token.</returns>
    public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default) => new(GetToken());

    /// <summary>A token: the current one until half its lifetime has passed, then a new one.</summary>
    /// <returns>The token.</returns>
    public string GetToken()
    {
        var key = _key();
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (_token is not null && now < _renewAt && ReferenceEquals(key, _signedWith))
            {
                return _token;
            }
            _token = Token.Api(key, Claims(now));
            _renewAt = now + _ttl / 2;
            _signedWith = key;
            return _token;
        }
    }

    private ApiTokenClaims Claims(DateTimeOffset now) => new()
    {
        Audience = _claims.Audience,
        Scopes = _claims.Scopes,
        Issuer = _claims.Issuer,
        Subject = _claims.Subject,
        Id = _claims.Id,
        IssuedAt = now,
        Expiry = Expiry.At(now + _ttl),
    };
}

/// <summary>
/// The public keys as a JSON Web Key Set (spec 11.3), for Centrifugo's
/// <c>client.token.jwks_public_endpoint</c> and Centrifugo PRO's <c>http_api.jwks.endpoint</c>.
/// </summary>
/// <remarks>
/// <para>
/// Rotate keys so: publish a new key next to the old one; sign with it only once every
/// instance of the application publishes it - from an activation time set ahead, or in a
/// second deploy; drop the old key once its tokens have expired. Never give a key ID to
/// another key: Centrifugo caches keys by ID, five minutes since v6.10.2.
/// </para>
/// </remarks>
public static class Jwks
{
    /// <summary>The JSON Web Key Set of keys.</summary>
    /// <param name="keys">RSA, ECDSA and Ed25519 keys, each with an ID of its own.</param>
    /// <returns><c>{"keys": [...]}</c>.</returns>
    /// <exception cref="CentTokenException">No keys, an HMAC key, a key without an ID, or an ID used twice.</exception>
    public static string Create(IEnumerable<SigningKey> keys) => System.Text.Encoding.UTF8.GetString(Encode(keys));

    /// <summary>
    /// A JWKS endpoint: answers <c>GET</c> and <c>HEAD</c> with the keys, letting caches keep
    /// them five minutes. It fails now, when built, for keys it refuses.
    /// </summary>
    /// <param name="keys">RSA, ECDSA and Ed25519 keys, each with an ID of its own.</param>
    /// <returns>The handler.</returns>
    /// <exception cref="CentTokenException">No keys, an HMAC key, a key without an ID, or an ID used twice.</exception>
    public static CentHandler Endpoint(IEnumerable<SigningKey> keys)
    {
        var body = Encode(keys);
        var headers = Responses.Headers(("Content-Type", "application/json"), ("Cache-Control", "public, max-age=300"));
        return new CentHandler(0, (request, _) =>
        {
            if (request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HandlerResponse(200, headers, ReadOnlyMemory<byte>.Empty));
            }
            if (!request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Responses.MethodNotAllowed("GET, HEAD"));
            }
            return Task.FromResult(new HandlerResponse(200, headers, body));
        });
    }

    private static byte[] Encode(IEnumerable<SigningKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var list = keys.ToList();
        if (list.Count == 0)
        {
            throw new CentTokenException("a JWKS without keys");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in list)
        {
            if (k.IsHmac)
            {
                throw new CentTokenException("an HMAC key in a JWKS: a secret is never published");
            }
            if (k.KeyId is null)
            {
                throw new CentTokenException($"an {k.Algorithm} key without an ID in a JWKS: Centrifugo finds keys by kid");
            }
            if (!ids.Add(k.KeyId))
            {
                throw new CentTokenException($"the key ID {k.KeyId} is used twice: Centrifugo caches keys by ID");
            }
        }
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteStartArray("keys");
            foreach (var k in list)
            {
                k.WriteJwk(w);
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
}
