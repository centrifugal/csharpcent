using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Centrifugal.Cent.Tokens;

/// <summary>
/// When a token, a connection, a subscription or a signature expires: a time, or never -
/// chosen explicitly, since a token which never expires works forever once leaked
/// (spec 11.2).
/// </summary>
public abstract class Expiry
{
    private Expiry()
    {
    }

    /// <summary>Never.</summary>
    public static Expiry Never { get; } = new NeverExpiry();

    /// <summary>At a time.</summary>
    /// <param name="time">The time; not before 1970.</param>
    /// <returns>The expiry.</returns>
    public static Expiry At(DateTimeOffset time) => new AtExpiry(time);

    /// <summary>A duration from now - or never, for one past what a time can hold.</summary>
    /// <param name="duration">The duration.</param>
    /// <param name="timeProvider">The time; the system's by default.</param>
    /// <returns>The expiry.</returns>
    public static Expiry After(TimeSpan duration, TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return duration > DateTimeOffset.MaxValue - now ? Never : new AtExpiry(now + duration);
    }

    /// <summary>The Unix time in seconds; null for never.</summary>
    internal abstract long? UnixSeconds();

    /// <inheritdoc/>
    public abstract override string ToString();

    private sealed class NeverExpiry : Expiry
    {
        internal override long? UnixSeconds() => null;

        public override string ToString() => "never";
    }

    private sealed class AtExpiry(DateTimeOffset time) : Expiry
    {
        internal override long? UnixSeconds() => Token.Unix(time, "an expiry");

        public override string ToString() => time.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// The claims of a connection token: see
/// <see href="https://centrifugal.dev/docs/server/authentication"/>.
/// </summary>
/// <remarks>
/// Its <see cref="Expiry"/> is when the token stops being accepted - the <c>exp</c> claim.
/// The connection expires then too, unless <see cref="ConnectionExpiry"/> says otherwise:
/// Centrifugo asks the client for a new token, and closes the connection without one.
/// </remarks>
public sealed class ConnectionClaims
{
    /// <summary>The ID of the user: the <c>sub</c> claim. Empty for an anonymous connection.</summary>
    public string User { get; init; } = "";

    /// <summary>When the token stops being accepted: the <c>exp</c> claim.</summary>
    public required Expiry Expiry { get; init; }

    /// <summary>
    /// When the connection expires, if not at <see cref="Expiry"/>: the <c>expire_at</c> claim.
    /// <see cref="Expiry.Never"/> keeps it alive, the token's <c>exp</c> still bounding the token.
    /// </summary>
    public Expiry? ConnectionExpiry { get; init; }

    /// <summary>When the token was issued: the <c>iat</c> claim. Now by default.</summary>
    public DateTimeOffset? IssuedAt { get; init; }

    /// <summary>The token's ID: the <c>jti</c> claim, for Centrifugo PRO's token revocation.</summary>
    public string? Id { get; init; }

    /// <summary>The <c>aud</c> claim, which Centrifugo checks when configured to.</summary>
    public string? Audience { get; init; }

    /// <summary>The <c>iss</c> claim, which Centrifugo checks when configured to.</summary>
    public string? Issuer { get; init; }

    /// <summary>Information about the connection other clients see in presence, join and leave: any JSON value.</summary>
    public object? Info { get; init; }

    /// <summary>Binary info, base64-encoded, instead of <see cref="Info"/>.</summary>
    public string? B64Info { get; init; }

    /// <summary>Channels the connection is subscribed to on the server side. Not together with <see cref="Subs"/>.</summary>
    public IReadOnlyList<string>? Channels { get; init; }

    /// <summary>Server-side subscriptions with their options, by channel. Centrifugo ignores <see cref="Channels"/> when it is set.</summary>
    public IReadOnlyDictionary<string, SubscribeOptions>? Subs { get; init; }

    /// <summary>
    /// Data about the connection which only the server sees - in proxy requests - and the
    /// user owning the token, who can decode it: never put in it what that user must not see.
    /// </summary>
    public object? Meta { get; init; }

    /// <summary>The channel capabilities of the connection. Centrifugo PRO only.</summary>
    public IReadOnlyList<ChannelsCapability>? Caps { get; init; }

    /// <summary>Labels of the connection, for Centrifugo PRO's connection filters.</summary>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }
}

/// <summary>The options of a server-side subscription in <see cref="ConnectionClaims.Subs"/>.</summary>
public sealed class SubscribeOptions
{
    /// <summary>Channel-specific information about the connection: any JSON value.</summary>
    public object? Info { get; init; }

    /// <summary>Binary info, base64-encoded, instead of <see cref="Info"/>.</summary>
    public string? B64Info { get; init; }

    /// <summary>Sent to the client with the subscription: any JSON value.</summary>
    public object? Data { get; init; }

    /// <summary>Binary data, base64-encoded, instead of <see cref="Data"/>.</summary>
    public string? B64Data { get; init; }

    /// <summary>Overrides channel options of the namespace for this subscription.</summary>
    public SubscribeOptionOverride? Override { get; init; }

    /// <summary>Makes the subscription receive only publications whose tags match it. Centrifugo PRO only.</summary>
    public FilterNode? ServerTagsFilter { get; init; }
}

/// <summary>
/// Grants operations in channels: a connection may do an operation in a channel when any of
/// its capabilities both allows the operation and matches the channel. Centrifugo PRO only.
/// </summary>
public sealed class ChannelsCapability
{
    /// <summary>The channels, or channel patterns, it applies to.</summary>
    public IReadOnlyList<string> Channels { get; init; } = [];

    /// <summary>The operations it grants: <c>sub</c> (subscribe), <c>pub</c> (publish), <c>hst</c> (history), <c>prs</c> (presence).</summary>
    public IReadOnlyList<string> Allow { get; init; } = [];

    /// <summary>How <see cref="Channels"/> are matched: null for exact names, <c>wildcard</c> or <c>regex</c>.</summary>
    public string? Match { get; init; }
}

/// <summary>
/// The claims of a subscription token: see
/// <see href="https://centrifugal.dev/docs/server/channel_token_auth"/>.
/// </summary>
public sealed class SubscriptionClaims
{
    /// <summary>The ID of the user the connection belongs to: the <c>sub</c> claim. Empty for an anonymous connection.</summary>
    public string User { get; init; } = "";

    /// <summary>The channel: required.</summary>
    public required string Channel { get; init; }

    /// <summary>When the token stops being accepted: the <c>exp</c> claim.</summary>
    public required Expiry Expiry { get; init; }

    /// <summary>When the subscription expires, if not at <see cref="Expiry"/>: the <c>expire_at</c> claim.</summary>
    public Expiry? SubscriptionExpiry { get; init; }

    /// <summary>When the token was issued: the <c>iat</c> claim. Now by default.</summary>
    public DateTimeOffset? IssuedAt { get; init; }

    /// <summary>The token's ID: the <c>jti</c> claim.</summary>
    public string? Id { get; init; }

    /// <summary>The <c>aud</c> claim.</summary>
    public string? Audience { get; init; }

    /// <summary>The <c>iss</c> claim.</summary>
    public string? Issuer { get; init; }

    /// <summary>Channel-specific information about the connection: any JSON value.</summary>
    public object? Info { get; init; }

    /// <summary>Binary info, base64-encoded, instead of <see cref="Info"/>.</summary>
    public string? B64Info { get; init; }

    /// <summary>Sent to the client with the subscription: any JSON value.</summary>
    public object? Data { get; init; }

    /// <summary>Binary data, base64-encoded, instead of <see cref="Data"/>.</summary>
    public string? B64Data { get; init; }

    /// <summary>Overrides channel options of the namespace for this subscription.</summary>
    public SubscribeOptionOverride? Override { get; init; }

    /// <summary>Operations allowed in the channel besides subscribing: <c>pub</c>, <c>hst</c>, <c>prs</c>. Centrifugo PRO only.</summary>
    public IReadOnlyList<string>? Allow { get; init; }

    /// <summary>Makes the subscription receive only publications whose tags match it. Centrifugo PRO only.</summary>
    public FilterNode? ServerTagsFilter { get; init; }
}

/// <summary>Signs connection and subscription tokens, and Centrifugo PRO's API tokens (spec 11).</summary>
/// <example>
/// <code>
/// var key = SigningKey.Hmac(secret);
/// var token = Token.Connection(key, new ConnectionClaims { User = "42", Expiry = Expiry.After(TimeSpan.FromHours(1)) });
/// </code>
/// </example>
public static class Token
{
    /// <summary>A connection token with <paramref name="claims"/>, signed with <paramref name="key"/>.</summary>
    /// <param name="key">The signing key.</param>
    /// <param name="claims">The claims.</param>
    /// <returns>The JWT.</returns>
    /// <exception cref="CentTokenException">A claim cannot be signed: the message says which.</exception>
    public static string Connection(SigningKey key, ConnectionClaims claims)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(claims);
        if (claims.Expiry is null)
        {
            throw new CentTokenException("a connection token without an expiry: give Expiry.At(...), or Expiry.Never explicitly");
        }
        if (claims.Channels is { Count: > 0 } && claims.Subs is { Count: > 0 })
        {
            throw new CentTokenException("set Channels or Subs, not both: Centrifugo ignores Channels when Subs is set");
        }
        return Sign(key, w =>
        {
            Registered(w, claims.User, claims.Expiry, claims.IssuedAt, claims.Id, claims.Audience, claims.Issuer);
            ExpireAt(w, claims.ConnectionExpiry);
            Payload(w, "info", claims.Info);
            String(w, "b64info", claims.B64Info);
            Strings(w, "channels", claims.Channels);
            if (claims.Subs is { Count: > 0 } subs)
            {
                w.WriteStartObject("subs");
                foreach (var (channel, o) in subs.OrderBy(s => s.Key, StringComparer.Ordinal))
                {
                    w.WriteStartObject(channel);
                    if (o is not null)
                    {
                        Payload(w, "info", o.Info);
                        String(w, "b64info", o.B64Info);
                        Payload(w, "data", o.Data);
                        String(w, "b64data", o.B64Data);
                        Message(w, "override", o.Override);
                        Message(w, "server_tags_filter", o.ServerTagsFilter);
                    }
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            Payload(w, "meta", claims.Meta);
            if (claims.Caps is { Count: > 0 } caps)
            {
                w.WriteStartArray("caps");
                foreach (var c in caps)
                {
                    w.WriteStartObject();
                    Strings(w, "channels", c.Channels);
                    Strings(w, "allow", c.Allow);
                    String(w, "match", c.Match);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            if (claims.Labels is { Count: > 0 } labels)
            {
                w.WriteStartObject("labels");
                foreach (var (name, value) in labels.OrderBy(l => l.Key, StringComparer.Ordinal))
                {
                    w.WriteString(name, value);
                }
                w.WriteEndObject();
            }
        });
    }

    /// <summary>A subscription token with <paramref name="claims"/>, signed with <paramref name="key"/>.</summary>
    /// <param name="key">The signing key.</param>
    /// <param name="claims">The claims.</param>
    /// <returns>The JWT.</returns>
    /// <exception cref="CentTokenException">A claim cannot be signed: the message says which.</exception>
    public static string Subscription(SigningKey key, SubscriptionClaims claims)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(claims);
        if (string.IsNullOrEmpty(claims.Channel))
        {
            throw new CentTokenException("a subscription token without a channel");
        }
        if (claims.Expiry is null)
        {
            throw new CentTokenException("a subscription token without an expiry: give Expiry.At(...), or Expiry.Never explicitly");
        }
        return Sign(key, w =>
        {
            Registered(w, claims.User, claims.Expiry, claims.IssuedAt, claims.Id, claims.Audience, claims.Issuer);
            w.WriteString("channel", claims.Channel);
            ExpireAt(w, claims.SubscriptionExpiry);
            Payload(w, "info", claims.Info);
            String(w, "b64info", claims.B64Info);
            Payload(w, "data", claims.Data);
            String(w, "b64data", claims.B64Data);
            Message(w, "override", claims.Override);
            Strings(w, "allow", claims.Allow);
            Message(w, "server_tags_filter", claims.ServerTagsFilter);
        });
    }

    /// <summary>
    /// A Centrifugo PRO API token (spec 11.4), for its <c>http_api.jwks</c>: sign with a key
    /// of its own, published at a JWKS endpoint other than the client tokens' one - see
    /// <see cref="ApiTokenClaims"/>. <see cref="ApiTokenSource"/> signs and reuses them for a
    /// client's <c>BearerTokenFunc</c>.
    /// </summary>
    /// <param name="key">An RSA, ECDSA or Ed25519 key with an ID: Centrifugo PRO verifies through JWKS, by <c>kid</c>.</param>
    /// <param name="claims">The claims.</param>
    /// <returns>The JWT.</returns>
    /// <exception cref="CentTokenException">The key or a claim cannot be signed: the message says which.</exception>
    public static string Api(SigningKey key, ApiTokenClaims claims)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(claims);
        if (key.IsHmac)
        {
            throw new CentTokenException("an HMAC key cannot sign API tokens: Centrifugo PRO verifies them through JWKS, which has no secrets");
        }
        if (key.KeyId is null)
        {
            throw new CentTokenException("an API token's key needs an ID: Centrifugo PRO finds the key in its JWKS by kid");
        }
        if (string.IsNullOrEmpty(claims.Audience) && claims.Scopes is not { Count: > 0 })
        {
            throw new CentTokenException("an API token with neither audience nor scope: Centrifugo PRO must be configured to require one only API tokens carry");
        }
        if (claims.Expiry is null)
        {
            throw new CentTokenException("an API token without an expiry: give Expiry.At(...)");
        }
        var scope = ApiTokenClaims.JoinScopes(claims.Scopes);
        return Sign(key, w =>
        {
            String(w, "aud", claims.Audience);
            String(w, "scope", scope);
            String(w, "iss", claims.Issuer);
            String(w, "sub", claims.Subject);
            if (claims.Expiry.UnixSeconds() is { } exp)
            {
                w.WriteNumber("exp", exp);
            }
            w.WriteNumber("iat", Unix(claims.IssuedAt ?? DateTimeOffset.UtcNow, "the issue time"));
            String(w, "jti", claims.Id);
        });
    }

    internal static long Unix(DateTimeOffset time, string what) =>
        time < DateTimeOffset.UnixEpoch ? throw new CentTokenException($"{what} before 1970") : time.ToUnixTimeSeconds();

    private static void Registered(Utf8JsonWriter w, string? user, Expiry expiry, DateTimeOffset? issuedAt, string? id, string? audience, string? issuer)
    {
        w.WriteString("sub", user ?? "");
        if (expiry.UnixSeconds() is { } exp)
        {
            w.WriteNumber("exp", exp);
        }
        w.WriteNumber("iat", Unix(issuedAt ?? DateTimeOffset.UtcNow, "the issue time"));
        String(w, "jti", id);
        String(w, "aud", audience);
        String(w, "iss", issuer);
    }

    /// <summary>The <c>expire_at</c> claim: left out, zero for never, or a time.</summary>
    private static void ExpireAt(Utf8JsonWriter w, Expiry? expiry)
    {
        if (expiry is not null)
        {
            w.WriteNumber("expire_at", expiry.UnixSeconds() ?? 0);
        }
    }

    private static void String(Utf8JsonWriter w, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            StrictStringConverter.Check(value);
            w.WriteString(name, value);
        }
    }

    private static void Strings(Utf8JsonWriter w, string name, IReadOnlyList<string>? values)
    {
        if (values is { Count: > 0 })
        {
            w.WriteStartArray(name);
            foreach (var v in values)
            {
                StrictStringConverter.Check(v);
                w.WriteStringValue(v);
            }
            w.WriteEndArray();
        }
    }

    private static void Payload(Utf8JsonWriter w, string name, object? value)
    {
        if (value is null)
        {
            return;
        }
        w.WritePropertyName(name);
        try
        {
            Cent.Payload.Write(w, value);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            throw new CentTokenException($"{name}: {e.Message}", e);
        }
    }

    private static void Message<T>(Utf8JsonWriter w, string name, T? value)
        where T : class
    {
        if (value is not null)
        {
            w.WritePropertyName(name);
            JsonSerializer.Serialize(w, value, Wire.Info<T>());
        }
    }

    /// <summary>Writes the JWT: base64url of the header, the claims and the signature, unpadded.</summary>
    internal static string Sign(SigningKey key, Action<Utf8JsonWriter> claims)
    {
        var header = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(header))
        {
            w.WriteStartObject();
            w.WriteString("alg", key.Algorithm);
            w.WriteString("typ", "JWT");
            if (key.KeyId is not null)
            {
                w.WriteString("kid", key.KeyId);
            }
            w.WriteEndObject();
        }
        var body = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(body, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            claims(w);
            w.WriteEndObject();
        }
        var input = Base64Url.Encode(header.WrittenSpan) + "." + Base64Url.Encode(body.WrittenSpan);
        var signature = key.Sign(Encoding.ASCII.GetBytes(input));
        return input + "." + Base64Url.Encode(signature);
    }
}
