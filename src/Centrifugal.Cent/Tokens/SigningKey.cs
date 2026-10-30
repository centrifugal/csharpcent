using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Centrifugal.Cent.Tokens;

/// <summary>
/// A key signing tokens (spec 11.1): an HMAC secret (HS256), an RSA private key (RS256),
/// an ECDSA private key on P-256, P-384 or P-521 (ES256, ES384, ES512), or an Ed25519
/// private key (EdDSA). A key may carry an ID, written as the token header's <c>kid</c>:
/// required for JWKS and for Centrifugo PRO's API tokens.
/// </summary>
/// <remarks>
/// .NET has no Ed25519, so an Ed25519 key takes the signing function of a library of the
/// application's choice - BouncyCastle, NSec - and the SDK depends on none:
/// <code>
/// // dotnet add package BouncyCastle.Cryptography
/// var privateKey = new Ed25519PrivateKeyParameters(seed); // the 32-byte seed
/// var key = SigningKey.Ed25519(privateKey.GeneratePublicKey().GetEncoded(), data =>
/// {
///     var signer = new Ed25519Signer();
///     signer.Init(true, privateKey);
///     signer.BlockUpdate(data, 0, data.Length);
///     return signer.GenerateSignature();
/// }, keyId: "2026-10");
/// </code>
/// </remarks>
public sealed class SigningKey
{
    private readonly byte[]? _secret;
    private readonly RSA? _rsa;
    private readonly ECDsa? _ecdsa;
    private readonly byte[]? _edPublic;
    private readonly Func<byte[], byte[]>? _edSign;

    private SigningKey(string algorithm, string? keyId, byte[]? secret = null, RSA? rsa = null, ECDsa? ecdsa = null, byte[]? edPublic = null, Func<byte[], byte[]>? edSign = null)
    {
        if (keyId is { Length: 0 })
        {
            throw new ArgumentException("an empty key ID: leave it null for a key without one", nameof(keyId));
        }
        Algorithm = algorithm;
        KeyId = keyId;
        _secret = secret;
        _rsa = rsa;
        _ecdsa = ecdsa;
        _edPublic = edPublic;
        _edSign = edSign;
    }

    /// <summary>The JWT algorithm: <c>HS256</c>, <c>RS256</c>, <c>ES256</c>, <c>ES384</c>, <c>ES512</c> or <c>EdDSA</c>.</summary>
    public string Algorithm { get; }

    /// <summary>The key's ID, written as <c>kid</c>; null for none.</summary>
    public string? KeyId { get; }

    /// <summary>Whether it is an HMAC secret, which has no public key to publish.</summary>
    public bool IsHmac => _secret is not null;

    /// <summary>An HMAC secret: Centrifugo's <c>client.token.hmac_secret_key</c>.</summary>
    /// <param name="secret">The secret, not empty.</param>
    /// <param name="keyId">The key's ID, if any.</param>
    /// <returns>The key.</returns>
    public static SigningKey Hmac(string secret, string? keyId = null)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return Hmac(Encoding.UTF8.GetBytes(secret), keyId);
    }

    /// <summary>An HMAC secret, as bytes.</summary>
    /// <param name="secret">The secret, not empty.</param>
    /// <param name="keyId">The key's ID, if any.</param>
    /// <returns>The key.</returns>
    public static SigningKey Hmac(ReadOnlySpan<byte> secret, string? keyId = null)
    {
        if (secret.IsEmpty)
        {
            throw new ArgumentException("an empty HMAC secret", nameof(secret));
        }
        return new SigningKey("HS256", keyId, secret: secret.ToArray());
    }

    /// <summary>An RSA private key: Centrifugo verifies with <c>client.token.rsa_public_key</c>, or JWKS.</summary>
    /// <param name="key">The private key; the caller keeps owning it.</param>
    /// <param name="keyId">The key's ID, if any.</param>
    /// <returns>The key.</returns>
    public static SigningKey Rsa(RSA key, string? keyId = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!HasPrivate(() => key.ExportParameters(true)))
        {
            throw new ArgumentException("an RSA public key: signing needs the private key", nameof(key));
        }
        return new SigningKey("RS256", keyId, rsa: key);
    }

    /// <summary>
    /// An ECDSA private key on P-256, P-384 or P-521 - ES256, ES384 or ES512: Centrifugo
    /// verifies with <c>client.token.ecdsa_public_key</c>, or JWKS. Other curves are refused.
    /// </summary>
    /// <param name="key">The private key; the caller keeps owning it.</param>
    /// <param name="keyId">The key's ID, if any.</param>
    /// <returns>The key.</returns>
    public static SigningKey ECDsa(ECDsa key, string? keyId = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!HasPrivate(() => key.ExportParameters(true)))
        {
            throw new ArgumentException("an ECDSA public key: signing needs the private key", nameof(key));
        }
        var curve = key.ExportParameters(false).Curve;
        var algorithm = CurveName(curve) switch
        {
            "P-256" => "ES256",
            "P-384" => "ES384",
            "P-521" => "ES512",
            _ => throw new ArgumentException("an ECDSA key on a curve other than P-256, P-384 and P-521: JWT has no algorithm for it", nameof(key)),
        };
        return new SigningKey(algorithm, keyId, ecdsa: key);
    }

    /// <summary>
    /// An Ed25519 private key, through a signing function of the application's - see the
    /// remarks of <see cref="SigningKey"/>. Centrifugo v6.10.2+ verifies with
    /// <c>client.token.ed25519_public_key</c>, or JWKS.
    /// </summary>
    /// <param name="publicKey">The 32-byte public key, for JWKS.</param>
    /// <param name="sign">Signs a message with the private key, returning the 64-byte signature.</param>
    /// <param name="keyId">The key's ID, if any.</param>
    /// <returns>The key.</returns>
    public static SigningKey Ed25519(ReadOnlySpan<byte> publicKey, Func<byte[], byte[]> sign, string? keyId = null)
    {
        ArgumentNullException.ThrowIfNull(sign);
        if (publicKey.Length != 32)
        {
            throw new ArgumentException($"an Ed25519 public key is 32 bytes, not {publicKey.Length}", nameof(publicKey));
        }
        return new SigningKey("EdDSA", keyId, edPublic: publicKey.ToArray(), edSign: sign);
    }

    /// <summary>
    /// An RSA or ECDSA private key from PEM: PKCS#8 (<c>BEGIN PRIVATE KEY</c>), PKCS#1
    /// (<c>BEGIN RSA PRIVATE KEY</c>) or SEC1 (<c>BEGIN EC PRIVATE KEY</c>). For Ed25519, use
    /// <see cref="Ed25519"/>.
    /// </summary>
    /// <param name="pem">The PEM text.</param>
    /// <param name="keyId">The key's ID, if any.</param>
    /// <returns>The key.</returns>
    public static SigningKey FromPem(string pem, string? keyId = null)
    {
        ArgumentNullException.ThrowIfNull(pem);
        if (pem.Contains("BEGIN RSA PRIVATE KEY", StringComparison.Ordinal))
        {
            return Rsa(ImportRsa(pem), keyId);
        }
        if (pem.Contains("BEGIN EC PRIVATE KEY", StringComparison.Ordinal))
        {
            return ECDsa(ImportEc(pem), keyId);
        }
        if (!pem.Contains("BEGIN PRIVATE KEY", StringComparison.Ordinal))
        {
            throw new ArgumentException("not a PEM private key: expected BEGIN PRIVATE KEY, BEGIN RSA PRIVATE KEY or BEGIN EC PRIVATE KEY", nameof(pem));
        }
        try
        {
            return Rsa(ImportRsa(pem), keyId);
        }
        catch (CryptographicException)
        {
        }
        try
        {
            return ECDsa(ImportEc(pem), keyId);
        }
        catch (CryptographicException)
        {
        }
        throw new ArgumentException(
            "a PKCS#8 key which is neither RSA nor ECDSA: for Ed25519, .NET has no implementation - use SigningKey.Ed25519 with a library's signing function",
            nameof(pem));
    }

    private static RSA ImportRsa(string pem)
    {
        var rsa = System.Security.Cryptography.RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
            return rsa;
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    private static ECDsa ImportEc(string pem)
    {
        var ec = System.Security.Cryptography.ECDsa.Create();
        try
        {
            ec.ImportFromPem(pem);
            return ec;
        }
        catch
        {
            ec.Dispose();
            throw;
        }
    }

    private static bool HasPrivate(Action export)
    {
        try
        {
            export();
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static string? CurveName(ECCurve curve)
    {
        var oid = curve.Oid?.Value;
        var name = curve.Oid?.FriendlyName;
        return (oid, name) switch
        {
            ("1.2.840.10045.3.1.7", _) or (_, "nistP256" or "ECDSA_P256" or "secp256r1") => "P-256",
            ("1.3.132.0.34", _) or (_, "nistP384" or "ECDSA_P384" or "secp384r1") => "P-384",
            ("1.3.132.0.35", _) or (_, "nistP521" or "ECDSA_P521" or "secp521r1") => "P-521",
            _ => null,
        };
    }

    /// <summary>Signs a JWT's signing input.</summary>
    internal byte[] Sign(byte[] data)
    {
        if (_secret is not null)
        {
            return HMACSHA256.HashData(_secret, data);
        }
        if (_rsa is not null)
        {
            return _rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        if (_ecdsa is not null)
        {
            var hash = Algorithm switch
            {
                "ES256" => HashAlgorithmName.SHA256,
                "ES384" => HashAlgorithmName.SHA384,
                _ => HashAlgorithmName.SHA512,
            };
            // r || s, each padded to the curve's size: IEEE P1363, as JWT has it.
            return _ecdsa.SignData(data, hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        var signature = _edSign!(data);
        if (signature is not { Length: 64 })
        {
            throw new CentTokenException($"the Ed25519 signing function returned {signature?.Length ?? 0} bytes, not a 64-byte signature");
        }
        return signature;
    }

    /// <summary>Writes the public key as a JSON Web Key (spec 11.3).</summary>
    internal void WriteJwk(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        if (_rsa is not null)
        {
            var p = _rsa.ExportParameters(false);
            w.WriteString("kty", "RSA");
            WriteCommon(w);
            w.WriteString("n", Base64Url.Encode(p.Modulus!));
            w.WriteString("e", Base64Url.Encode(p.Exponent!));
        }
        else if (_ecdsa is not null)
        {
            var p = _ecdsa.ExportParameters(false);
            var size = Algorithm switch { "ES256" => 32, "ES384" => 48, _ => 66 };
            w.WriteString("kty", "EC");
            WriteCommon(w);
            w.WriteString("crv", Algorithm switch { "ES256" => "P-256", "ES384" => "P-384", _ => "P-521" });
            w.WriteString("x", Base64Url.Encode(Pad(p.Q.X!, size)));
            w.WriteString("y", Base64Url.Encode(Pad(p.Q.Y!, size)));
        }
        else
        {
            w.WriteString("kty", "OKP");
            WriteCommon(w);
            w.WriteString("crv", "Ed25519");
            w.WriteString("x", Base64Url.Encode(_edPublic!));
        }
        w.WriteEndObject();
    }

    private void WriteCommon(Utf8JsonWriter w)
    {
        w.WriteString("kid", KeyId);
        w.WriteString("use", "sig");
        w.WriteString("alg", Algorithm);
    }

    private static byte[] Pad(byte[] b, int size)
    {
        if (b.Length >= size)
        {
            return b;
        }
        var padded = new byte[size];
        b.CopyTo(padded, size - b.Length);
        return padded;
    }
}

/// <summary>Unpadded base64url, as JWTs and JWKs write binary.</summary>
internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>A token could not be signed: the message says which claim or key, and why.</summary>
public sealed class CentTokenException : CentException
{
    /// <summary>Creates the error - for test doubles of applications.</summary>
    /// <param name="message">Which claim or key, and why.</param>
    /// <param name="innerException">The cause, if any.</param>
    public CentTokenException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
