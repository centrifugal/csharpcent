using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Centrifugal.Cent.Tokens;

namespace Centrifugal.Cent;

/// <summary>
/// Signatures for shared poll subscriptions (spec 13): they tell Centrifugo which keys of a
/// shared poll channel a connection may track.
/// </summary>
public static class SharedPoll
{
    /// <summary>
    /// Signs the keys a connection may track in a channel: <c>{iat}:{exp}:{mac}</c>, covering
    /// exactly <paramref name="keys"/>, in order.
    /// </summary>
    /// <param name="secret">Centrifugo's <c>shared_poll.hmac_secret_key</c>.</param>
    /// <param name="user">The connection's user; empty for an anonymous connection.</param>
    /// <param name="channel">The channel.</param>
    /// <param name="keys">The keys.</param>
    /// <param name="expiresAt">When the signature expires: a time, or <see cref="Expiry.Never"/>.</param>
    /// <param name="timeProvider">The time of <c>iat</c>; the system's by default.</param>
    /// <returns>The signature.</returns>
    public static string Sign(string secret, string user, string channel, IReadOnlyList<string> keys, Expiry expiresAt, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(expiresAt);
        var iat = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
        return SignAt(secret, user, channel, keys, expiresAt, iat);
    }

    internal static string SignAt(string secret, string user, string channel, IReadOnlyList<string> keys, Expiry expiresAt, long iat)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(keys);
        var exp = expiresAt.UnixSeconds() ?? 0;
        var keysHash = Hex(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', keys))));
        var message = string.Create(CultureInfo.InvariantCulture, $"{iat}\0{exp}\0{user}\0{channel}\0{keysHash}");
        var mac = Hex(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(message)));
        return string.Create(CultureInfo.InvariantCulture, $"{iat}:{exp}:{mac}");
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
