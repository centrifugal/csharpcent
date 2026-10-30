using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Centrifugal.Cent.Tokens;
using Microsoft.Extensions.Time.Testing;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Centrifugal.Cent.Tests;

public sealed class TokenTests
{
    private static readonly DateTimeOffset Iat = DateTimeOffset.FromUnixTimeSeconds(1700000000);
    private static readonly DateTimeOffset Exp = DateTimeOffset.FromUnixTimeSeconds(1700003600);

    internal static string[] Parts(string jwt) => jwt.Split('.');

    internal static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
    }

    internal static JsonElement Claims(string jwt) => JsonDocument.Parse(FromBase64Url(Parts(jwt)[1])).RootElement.Clone();

    internal static JsonElement Header(string jwt) => JsonDocument.Parse(FromBase64Url(Parts(jwt)[0])).RootElement.Clone();

    private static byte[] SigningInput(string jwt) => Encoding.ASCII.GetBytes(Parts(jwt)[0] + "." + Parts(jwt)[1]);

    /// <summary>An Ed25519 key through BouncyCastle, as the docs show; and its private key, for verifying.</summary>
    internal static (SigningKey Key, Ed25519PrivateKeyParameters Private) Ed25519(string? keyId = null, byte[]? seed = null)
    {
        var privateKey = new Ed25519PrivateKeyParameters(seed ?? RandomNumberGenerator.GetBytes(32));
        var key = SigningKey.Ed25519(privateKey.GeneratePublicKey().GetEncoded(), data =>
        {
            var signer = new Ed25519Signer();
            signer.Init(true, privateKey);
            signer.BlockUpdate(data, 0, data.Length);
            return signer.GenerateSignature();
        }, keyId);
        return (key, privateKey);
    }

    [Fact]
    public void ConnectionVector()
    {
        var token = Token.Connection(SigningKey.Hmac("client_secret"), new ConnectionClaims { User = "42", Expiry = Expiry.At(Exp), IssuedAt = Iat });
        Assert.Equal(
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiI0MiIsImV4cCI6MTcwMDAwMzYwMCwiaWF0IjoxNzAwMDAwMDAwfQ.HKZwmcYmHVbRESuYd4swVgAH7gg41giR57F_K5FEmEY",
            token);
    }

    [Fact]
    public void SubscriptionVector()
    {
        var token = Token.Subscription(SigningKey.Hmac("client_secret"), new SubscriptionClaims { User = "42", Channel = "news", Expiry = Expiry.At(Exp), IssuedAt = Iat });
        Assert.Equal(
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiI0MiIsImV4cCI6MTcwMDAwMzYwMCwiaWF0IjoxNzAwMDAwMDAwLCJjaGFubmVsIjoibmV3cyJ9.v_Quw-azNV1qFjm9ZQnkp6QBtK6LmtFxY74YcUHlJcA",
            token);
    }

    [Fact]
    public void SharedPollVectors()
    {
        Assert.Equal(
            "1700000000:1700003600:c61dbf52085a6973537e71644fa30a3bbfc29a52d7ce9f1b7618531596ce587d",
            SharedPoll.Sign("shared_poll_secret", "42", "poll:prices", ["AAPL", "GOOG"], Expiry.At(Exp), new FakeTimeProvider(Iat)));
        Assert.Equal(
            "1700000000:0:92dd64af5612afde06cad03bb63435d951e037cb55a087af05c2a1399abade0e",
            SharedPoll.Sign("shared_poll_secret", "", "poll:prices", [], Expiry.Never, new FakeTimeProvider(Iat)));
    }

    [Fact]
    public void ClaimsAreWrittenWithTheirNamesInOrder()
    {
        var token = Token.Connection(SigningKey.Hmac("s", "k1"), new ConnectionClaims
        {
            User = "42",
            Expiry = Expiry.At(Exp),
            ConnectionExpiry = Expiry.Never,
            IssuedAt = Iat,
            Id = "t1",
            Audience = "app",
            Issuer = "me",
            Info = new { name = "Alex" },
            Subs = new Dictionary<string, SubscribeOptions>
            {
                ["b"] = new() { Data = RawJson.Parse("1.0"), Override = new SubscribeOptionOverride { Presence = false } },
                ["a"] = new(),
            },
            Meta = RawJson.Parse("""{"plan":"pro"}"""),
            Caps = [new ChannelsCapability { Channels = ["news:*"], Allow = ["sub", "hst"], Match = "wildcard" }],
            Labels = new Dictionary<string, string> { ["z"] = "1", ["a"] = "2" },
        });
        Assert.Equal("""{"alg":"HS256","typ":"JWT","kid":"k1"}""", Header(token).GetRawText());
        Assert.Equal(
            """{"sub":"42","exp":1700003600,"iat":1700000000,"jti":"t1","aud":"app","iss":"me","expire_at":0,"info":{"name":"Alex"},"subs":{"a":{},"b":{"data":1.0,"override":{"presence":{"value":false}}}},"meta":{"plan":"pro"},"caps":[{"channels":["news:*"],"allow":["sub","hst"],"match":"wildcard"}],"labels":{"a":"2","z":"1"}}""",
            Claims(token).GetRawText());

        var sub = Token.Subscription(SigningKey.Hmac("s"), new SubscriptionClaims
        {
            Channel = "c",
            Expiry = Expiry.Never,
            SubscriptionExpiry = Expiry.At(Exp),
            IssuedAt = Iat,
            Data = "x",
            B64Info = "AQ==",
            Allow = ["pub"],
        });
        Assert.Equal("""{"sub":"","iat":1700000000,"channel":"c","expire_at":1700003600,"b64info":"AQ==","data":"x","allow":["pub"]}""", Claims(sub).GetRawText());
    }

    [Fact]
    public void ClaimsWhichCannotBeSignedAreRefused()
    {
        var key = SigningKey.Hmac("s");
        Assert.Throws<CentTokenException>(() => Token.Connection(key, new ConnectionClaims { Expiry = null! }));
        Assert.Throws<CentTokenException>(() => Token.Connection(key, new ConnectionClaims { Expiry = Expiry.Never, Channels = ["a"], Subs = new Dictionary<string, SubscribeOptions> { ["b"] = new() } }));
        Assert.Throws<CentTokenException>(() => Token.Subscription(key, new SubscriptionClaims { Channel = "", Expiry = Expiry.Never }));
        var nan = Assert.Throws<CentTokenException>(() => Token.Connection(key, new ConnectionClaims { Expiry = Expiry.Never, Info = double.NaN }));
        Assert.StartsWith("info:", nan.Message, StringComparison.Ordinal);
        Assert.Throws<CentTokenException>(() => Token.Connection(key, new ConnectionClaims { Expiry = Expiry.At(DateTimeOffset.UnixEpoch.AddSeconds(-1)) }));
        Assert.Throws<ArgumentException>(() => SigningKey.Hmac(""));
    }

    [Fact]
    public void ExpiryAfterIsFromNowOrNever()
    {
        var time = new FakeTimeProvider(Iat);
        var token = Token.Connection(SigningKey.Hmac("s"), new ConnectionClaims { Expiry = Expiry.After(TimeSpan.FromHours(1), time), IssuedAt = Iat });
        Assert.Equal(1700003600, Claims(token).GetProperty("exp").GetInt64());
        Assert.Same(Expiry.Never, Expiry.After(TimeSpan.MaxValue));
        var never = Token.Connection(SigningKey.Hmac("s"), new ConnectionClaims { Expiry = Expiry.Never });
        Assert.False(Claims(never).TryGetProperty("exp", out _));
    }

    [Fact]
    public void RsaTokensVerify()
    {
        using var rsa = RSA.Create(2048);
        var token = Token.Connection(SigningKey.Rsa(rsa, "r1"), new ConnectionClaims { User = "1", Expiry = Expiry.Never });
        Assert.Equal("RS256", Header(token).GetProperty("alg").GetString());
        Assert.True(rsa.VerifyData(SigningInput(token), FromBase64Url(Parts(token)[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Theory]
    [InlineData("nistP256", "ES256", 64)]
    [InlineData("nistP384", "ES384", 96)]
    [InlineData("nistP521", "ES512", 132)]
    public void EcdsaTokensAreROrSPaddedAndVerify(string curve, string algorithm, int signatureBytes)
    {
        using var ec = ECDsa.Create(ECCurve.CreateFromFriendlyName(curve));
        var hash = algorithm switch { "ES256" => HashAlgorithmName.SHA256, "ES384" => HashAlgorithmName.SHA384, _ => HashAlgorithmName.SHA512 };
        for (var i = 0; i < 20; i++)
        {
            var token = Token.Connection(SigningKey.ECDsa(ec), new ConnectionClaims { User = $"{i}", Expiry = Expiry.Never });
            Assert.Equal(algorithm, Header(token).GetProperty("alg").GetString());
            var signature = FromBase64Url(Parts(token)[2]);
            Assert.Equal(signatureBytes, signature.Length);
            Assert.True(ec.VerifyData(SigningInput(token), signature, hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }
    }

    [Fact]
    public void OtherCurvesArePublicKeysAndUnknownPemsAreRefused()
    {
        ECDsa? other = null;
        try
        {
            // Not every platform has another curve: macOS has only the NIST ones.
            other = ECDsa.Create(ECCurve.CreateFromFriendlyName("brainpoolP256r1"));
        }
        catch (PlatformNotSupportedException)
        {
        }
        if (other is not null)
        {
            using (other)
            {
                Assert.Throws<ArgumentException>(() => SigningKey.ECDsa(other));
            }
        }
        using var rsa = RSA.Create(2048);
        using var publicOnly = RSA.Create();
        publicOnly.ImportRSAPublicKey(rsa.ExportRSAPublicKey(), out _);
        Assert.Throws<ArgumentException>(() => SigningKey.Rsa(publicOnly));
        Assert.Throws<ArgumentException>(() => SigningKey.FromPem("-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----"));
        var ed = "-----BEGIN PRIVATE KEY-----\nMC4CAQAwBQYDK2VwBCIEIDHCsUZh4Y0lj7j0tdLqjBdbGP7wdW1tJlgAX7jRWx5i\n-----END PRIVATE KEY-----";
        Assert.Contains("SigningKey.Ed25519", Assert.Throws<ArgumentException>(() => SigningKey.FromPem(ed)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KeysComeFromPem()
    {
        using var rsa = RSA.Create(2048);
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Equal("RS256", SigningKey.FromPem(rsa.ExportPkcs8PrivateKeyPem()).Algorithm);
        Assert.Equal("RS256", SigningKey.FromPem(rsa.ExportRSAPrivateKeyPem()).Algorithm);
        Assert.Equal("ES256", SigningKey.FromPem(ec.ExportPkcs8PrivateKeyPem(), "e1").Algorithm);
        Assert.Equal("ES256", SigningKey.FromPem(ec.ExportECPrivateKeyPem()).Algorithm);
    }

    [Fact]
    public void Ed25519TokensVerify()
    {
        var (key, privateKey) = Ed25519("ed1");
        var token = Token.Subscription(key, new SubscriptionClaims { Channel = "c", Expiry = Expiry.Never });
        Assert.Equal("""{"alg":"EdDSA","typ":"JWT","kid":"ed1"}""", Header(token).GetRawText());
        var verifier = new Ed25519Signer();
        verifier.Init(false, privateKey.GeneratePublicKey());
        var input = SigningInput(token);
        verifier.BlockUpdate(input, 0, input.Length);
        Assert.True(verifier.VerifySignature(FromBase64Url(Parts(token)[2])));
        Assert.Throws<ArgumentException>(() => SigningKey.Ed25519(new byte[31], d => d));
        var bad = SigningKey.Ed25519(new byte[32], _ => new byte[10]);
        Assert.Throws<CentTokenException>(() => Token.Connection(bad, new ConnectionClaims { Expiry = Expiry.Never }));
    }

    [Fact]
    public void JwksPublishesThePublicKeys()
    {
        using var rsa = RSA.Create(2048);
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP521);
        var (ed, edPrivate) = Ed25519("ed");
        var jwks = JsonDocument.Parse(Jwks.Create([SigningKey.Rsa(rsa, "rsa"), SigningKey.ECDsa(ec, "ec"), ed])).RootElement;
        var keys = jwks.GetProperty("keys").EnumerateArray().ToList();
        var p = rsa.ExportParameters(false);
        Assert.Equal(
            $$"""{"kty":"RSA","kid":"rsa","use":"sig","alg":"RS256","n":"{{B64(p.Modulus!)}}","e":"{{B64(p.Exponent!)}}"}""",
            keys[0].GetRawText());
        Assert.Equal("P-521", keys[1].GetProperty("crv").GetString());
        Assert.Equal(66, FromBase64Url(keys[1].GetProperty("x").GetString()!).Length);
        Assert.Equal(66, FromBase64Url(keys[1].GetProperty("y").GetString()!).Length);
        Assert.Equal(
            $$"""{"kty":"OKP","kid":"ed","use":"sig","alg":"EdDSA","crv":"Ed25519","x":"{{B64(edPrivate.GeneratePublicKey().GetEncoded())}}"}""",
            keys[2].GetRawText());

        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    [Fact]
    public void JwksRefusesWhatCannotBePublished()
    {
        using var rsa = RSA.Create(2048);
        Assert.Throws<CentTokenException>(() => Jwks.Create([]));
        Assert.Throws<CentTokenException>(() => Jwks.Create([SigningKey.Hmac("s", "h")]));
        Assert.Throws<CentTokenException>(() => Jwks.Create([SigningKey.Rsa(rsa)]));
        Assert.Throws<CentTokenException>(() => Jwks.Endpoint([SigningKey.Rsa(rsa, "a"), SigningKey.Rsa(rsa, "a")]));
    }

    [Fact]
    public async Task JwksEndpointAnswersGetAndHead()
    {
        using var rsa = RSA.Create(2048);
        var endpoint = Jwks.Endpoint([SigningKey.Rsa(rsa, "a")]);
        var get = await endpoint.HandleAsync(new HandlerRequest("GET", ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken);
        Assert.Equal(200, get.StatusCode);
        Assert.Equal("application/json", get.Headers["Content-Type"]);
        Assert.Equal("public, max-age=300", get.Headers["cache-control"]);
        Assert.Equal(Jwks.Create([SigningKey.Rsa(rsa, "a")]), Encoding.UTF8.GetString(get.Body.Span));
        var head = await endpoint.HandleAsync(new HandlerRequest("HEAD", ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken);
        Assert.Equal(200, head.StatusCode);
        Assert.True(head.Body.IsEmpty);
        var post = await endpoint.HandleAsync(new HandlerRequest("POST", ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken);
        Assert.Equal(405, post.StatusCode);
        Assert.Equal("GET, HEAD", post.Headers["Allow"]);
    }

    [Fact]
    public void ApiTokensCarryTheirClaimsAndRefuseWhatPROWouldNotVerify()
    {
        var (key, _) = Ed25519("api-1");
        var token = Token.Api(key, new ApiTokenClaims
        {
            Audience = "centrifugo-api",
            Scopes = ["centrifugo:api", "read"],
            Issuer = "me",
            Subject = "billing",
            Expiry = Expiry.At(Exp),
            IssuedAt = Iat,
            Id = "j",
        });
        Assert.Equal(
            """{"aud":"centrifugo-api","scope":"centrifugo:api read","iss":"me","sub":"billing","exp":1700003600,"iat":1700000000,"jti":"j"}""",
            Claims(token).GetRawText());
        Assert.Equal("api-1", Header(token).GetProperty("kid").GetString());

        var claims = new ApiTokenClaims { Audience = "a", Expiry = Expiry.At(Exp) };
        Assert.Throws<CentTokenException>(() => Token.Api(SigningKey.Hmac("s", "h"), claims));
        Assert.Throws<CentTokenException>(() => Token.Api(Ed25519().Key, claims));
        Assert.Throws<CentTokenException>(() => Token.Api(key, new ApiTokenClaims { Expiry = Expiry.At(Exp) }));
        Assert.Throws<CentTokenException>(() => Token.Api(key, new ApiTokenClaims { Audience = "a" }));
        Assert.Throws<CentTokenException>(() => Token.Api(key, new ApiTokenClaims { Scopes = ["a b"], Expiry = Expiry.At(Exp) }));
        Assert.Throws<CentTokenException>(() => Token.Api(key, new ApiTokenClaims { Scopes = [""], Expiry = Expiry.At(Exp) }));
    }

    [Fact]
    public async Task ApiTokenSourceReusesATokenForHalfItsLifetime()
    {
        var time = new FakeTimeProvider(Iat);
        var (key, _) = Ed25519("api-1");
        var source = new ApiTokenSource(key, new ApiTokenClaims { Audience = "centrifugo-api" }, TimeSpan.FromMinutes(10), time);
        var first = await source.GetTokenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1700000600, Claims(first).GetProperty("exp").GetInt64());
        time.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(first, source.GetToken());
        time.Advance(TimeSpan.FromMinutes(1));
        var second = source.GetToken();
        Assert.NotEqual(first, second);
        Assert.Equal(1700000300, Claims(second).GetProperty("iat").GetInt64());

        var tokens = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(source.GetToken)));
        Assert.Single(tokens.Distinct());
    }

    [Fact]
    public void ApiTokenSourceRefusesWhatItSetsItself()
    {
        var (key, _) = Ed25519("api-1");
        Assert.Throws<CentTokenException>(() => new ApiTokenSource(key, new ApiTokenClaims { Audience = "a", Expiry = Expiry.Never }));
        Assert.Throws<CentTokenException>(() => new ApiTokenSource(key, new ApiTokenClaims { Audience = "a", IssuedAt = Iat }));
        Assert.Throws<CentTokenException>(() => new ApiTokenSource(key, new ApiTokenClaims { Audience = "a" }, TimeSpan.FromSeconds(-1)));
        Assert.Throws<CentTokenException>(() => new ApiTokenSource(SigningKey.Hmac("s", "h"), new ApiTokenClaims { Audience = "a" }));
    }

    [Fact]
    public void ApiTokenSourceSignsWithARotatedKeyFromTheNextToken()
    {
        var current = Ed25519("one").Key;
        var source = new ApiTokenSource(() => current, new ApiTokenClaims { Audience = "a" });
        Assert.Equal("one", Header(source.GetToken()).GetProperty("kid").GetString());
        current = Ed25519("two").Key;
        Assert.Equal("two", Header(source.GetToken()).GetProperty("kid").GetString());
    }
}
