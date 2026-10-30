using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Centrifugal.Cent;

/// <summary>
/// The base of every error of a call: catch it to handle them all, or the derived type
/// of one kind (spec 4.1). <see cref="Retry.IsRetryable"/> tells whether another
/// attempt may succeed.
/// </summary>
public abstract class CentException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What happened, for people.</param>
    /// <param name="innerException">The cause, if any.</param>
    protected CentException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Centrifugo refused the request or failed to carry it out. Match it by
/// <see cref="Code"/> alone - <see cref="ErrorCodes"/> - never by message: Centrifugo
/// adds codes, and an unknown code is still this error.
/// </summary>
/// <example>
/// <code>
/// catch (CentApiException e) when (e.Code == ErrorCodes.UnknownChannel) { ... }
/// </code>
/// </example>
public sealed class CentApiException : CentException
{
    /// <summary>Creates the error - for test doubles of applications.</summary>
    /// <param name="code">The Centrifugo error code.</param>
    /// <param name="message">Centrifugo's message.</param>
    /// <param name="temporary">Whether the same request may succeed later.</param>
    public CentApiException(int code, string message, bool temporary = false)
        : this(code, message, temporary, null)
    {
    }

    internal CentApiException(int code, string message, bool temporary, string? hint)
        : base(hint is null ? $"centrifugo error {code}: {message}" : $"centrifugo error {code}: {message} ({hint})")
    {
        Code = code;
        ApiMessage = message;
        Temporary = temporary;
    }

    /// <summary>The error code: stable, what to match.</summary>
    public int Code { get; }

    /// <summary>Centrifugo's message: for people, not to compare.</summary>
    public string ApiMessage { get; }

    /// <summary>Whether the same request may succeed if retried later.</summary>
    public bool Temporary { get; }
}

/// <summary>The codes of Centrifugo's errors (spec 4.1). Centrifugo adds codes: nothing may switch over them exhaustively.</summary>
public static class ErrorCodes
{
    /// <summary>An internal error: temporary.</summary>
    public const int Internal = 100;

    /// <summary>The channel's namespace does not exist.</summary>
    public const int UnknownChannel = 102;

    /// <summary>Not found - also a Centrifugo PRO method in a batch sent to Centrifugo OSS.</summary>
    public const int NotFound = 104;

    /// <summary>The request is malformed.</summary>
    public const int BadRequest = 107;

    /// <summary>The feature is not available, such as history in a channel without it.</summary>
    public const int NotAvailable = 108;

    /// <summary>A limit was reached: temporary.</summary>
    public const int TooManyRequests = 111;

    /// <summary>The stream position cannot be recovered from.</summary>
    public const int UnrecoverablePosition = 112;

    /// <summary>The request conflicts with the current state, such as a stale version.</summary>
    public const int Conflict = 113;
}

/// <summary>The request did not reach the API method: an HTTP status other than 200.</summary>
public class CentHttpException : CentException
{
    /// <summary>Creates the error - for test doubles of applications.</summary>
    /// <param name="statusCode">The HTTP status.</param>
    /// <param name="body">The start of the response body.</param>
    /// <param name="headers">The response headers.</param>
    public CentHttpException(int statusCode, string body, IReadOnlyDictionary<string, IReadOnlyList<string>>? headers = null)
        : this(statusCode, body, headers, null)
    {
    }

    internal CentHttpException(int statusCode, string body, IReadOnlyDictionary<string, IReadOnlyList<string>>? headers, string? hint)
        : base(Describe(statusCode, body, hint))
    {
        StatusCode = statusCode;
        Body = body;
        Headers = headers ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The HTTP status.</summary>
    public int StatusCode { get; }

    /// <summary>The start of the response body: at most 512 bytes.</summary>
    public string Body { get; }

    /// <summary>The response headers, matched case-insensitively: request IDs, <c>Retry-After</c>.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; }

    private static string Describe(int status, string body, string? hint)
    {
        var text = $"HTTP status {status}";
        if (body.Length > 0)
        {
            text += $": {body}";
        }
        return hint is null ? text : $"{text} ({hint})";
    }
}

/// <summary>Centrifugo refused the SDK's credentials: HTTP status 401 - check the API key or bearer token.</summary>
public sealed class CentUnauthorizedException : CentHttpException
{
    /// <summary>Creates the error - for test doubles of applications.</summary>
    /// <param name="body">The start of the response body.</param>
    /// <param name="headers">The response headers.</param>
    public CentUnauthorizedException(string body = "", IReadOnlyDictionary<string, IReadOnlyList<string>>? headers = null)
        : base(401, body, headers, "check the credentials: the API key or the bearer token")
    {
    }
}

/// <summary>The reply is not Centrifugo's - usually a wrong endpoint.</summary>
public sealed class CentDecodeException : CentException
{
    /// <summary>Creates the error - for test doubles of applications.</summary>
    /// <param name="method">The API method.</param>
    /// <param name="message">What is wrong with the reply.</param>
    /// <param name="innerException">The cause, if any.</param>
    public CentDecodeException(string method, string message, Exception? innerException = null)
        : base($"{method}: the reply is not Centrifugo's - is the endpoint the API's? {message}", innerException)
    {
    }
}

/// <summary>The request was not sent: the client can tell it is wrong (spec 3.4, 3.5).</summary>
public sealed class CentInvalidRequestException : CentException
{
    /// <summary>Creates the error - for test doubles of applications.</summary>
    /// <param name="method">The API method.</param>
    /// <param name="message">What is wrong with the request.</param>
    /// <param name="innerException">The cause, if any.</param>
    public CentInvalidRequestException(string method, string message, Exception? innerException = null)
        : base($"{method}: {message}", innerException)
    {
    }
}

/// <summary>The client was not created: the message names the option and the fix.</summary>
public sealed class CentInvalidConfigException : CentException
{
    /// <summary>Creates the error - for test doubles of applications.</summary>
    /// <param name="message">The option and the fix.</param>
    public CentInvalidConfigException(string message)
        : base(message)
    {
    }
}

/// <summary>A call after the client was closed.</summary>
public sealed class CentClosedException : CentException
{
    /// <summary>Creates the error - for test doubles of applications.</summary>
    public CentClosedException()
        : base("the client is closed")
    {
    }
}

/// <summary>The request failed on its way: the connection was refused or reset, or the name did not resolve.</summary>
public sealed class CentNetworkException : CentException
{
    /// <summary>Creates the error - for test doubles of applications.</summary>
    /// <param name="method">The API method.</param>
    /// <param name="innerException">The failure, such as an <see cref="HttpRequestException"/>.</param>
    public CentNetworkException(string method, Exception innerException)
        : base($"{method}: {innerException.Message}", innerException)
    {
    }
}

/// <summary>
/// The call's deadline ended: the client's <c>RequestTimeout</c>, <see cref="CentClient.WithTimeout"/>,
/// or the timeout of an <see cref="HttpClient"/> of the application's. A call cancelled by its
/// <see cref="CancellationToken"/> throws <see cref="OperationCanceledException"/> instead.
/// </summary>
public sealed class CentTimeoutException : CentException
{
    /// <summary>Creates the error - for test doubles of applications.</summary>
    /// <param name="method">The API method.</param>
    /// <param name="timeout">The call's timeout.</param>
    /// <param name="cause">What the deadline ended, if anything.</param>
    public CentTimeoutException(string method, TimeSpan timeout, Exception? cause = null)
        : base(Describe(method, timeout), new TimeoutException(Describe(method, timeout), cause))
    {
    }

    private static string Describe(string method, TimeSpan timeout) =>
        timeout == Timeout.InfiniteTimeSpan
            ? $"{method}: timed out"
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{method}: no reply within {timeout.TotalSeconds:0.###}s");
}

/// <summary>A batch was sent again, or a command added after it was sent: a batch is sent once.</summary>
public sealed class CentBatchSentException : CentException
{
    /// <summary>Creates the error - for test doubles of applications.</summary>
    /// <param name="message">What was done to the sent batch.</param>
    public CentBatchSentException(string message)
        : base(message)
    {
    }
}

/// <summary>A command's outcome was read before its batch was sent.</summary>
public sealed class CentBatchNotSentException : CentException
{
    /// <summary>Creates the error - for test doubles of applications.</summary>
    public CentBatchNotSentException()
        : base("the batch was not sent: send it before reading its commands' outcomes")
    {
    }
}

/// <summary>
/// The retries of <see cref="CentClient.BroadcastWithRetryAsync"/> were stopped by its
/// <see cref="BroadcastRetryOptions.OnAttemptError"/>: <see cref="Exception.InnerException"/>
/// is what it threw, <see cref="BroadcastError"/> the broadcast's error then.
/// </summary>
public sealed class CentRetryStoppedException : CentException
{
    /// <summary>Creates the error - for test doubles of applications.</summary>
    /// <param name="reason">What <see cref="BroadcastRetryOptions.OnAttemptError"/> threw.</param>
    /// <param name="broadcastError">The broadcast's error then.</param>
    public CentRetryStoppedException(Exception reason, Exception broadcastError)
        : base($"broadcast retries stopped: {reason.Message}; {broadcastError.Message}", reason)
    {
        BroadcastError = broadcastError;
    }

    /// <summary>The broadcast's error when the retries stopped: a <see cref="BroadcastFailedChannelsException"/> holds every channel's outcome.</summary>
    public Exception BroadcastError { get; }
}

/// <summary>Whether errors are retryable (spec 4.4), and the retry helpers (spec 6).</summary>
public static class Retry
{
    /// <summary>
    /// Whether a new attempt, with time left, may succeed: the error came from a temporary
    /// condition, not from the request (spec 4.4).
    /// </summary>
    /// <remarks>
    /// A retryable error does not mean nothing was done: a publication may have been
    /// published before the reply was lost. Retry a publication only with the same
    /// <c>IdempotencyKey</c>. A broadcast is not retried as a whole: retry only
    /// <see cref="BroadcastResult.RetryableChannels"/>, or use
    /// <see cref="CentClient.BroadcastWithRetryAsync"/>.
    /// </remarks>
    /// <param name="exception">An error of a call.</param>
    /// <returns>Whether to retry.</returns>
    public static bool IsRetryable(Exception exception) => exception switch
    {
        CentApiException e => e.Temporary || e.Code == ErrorCodes.Internal,
        CentHttpException e => e.StatusCode == 429 || e.StatusCode >= 500,
        CentTimeoutException => true,
        CentNetworkException e => !IsCertificateError(e.InnerException),
        BroadcastFailedChannelsException e => e.Failed.Any(c => c.Error is { } err && IsRetryable(err)),
        BatchFailedCommandsException e => e.Failed.Any(c => IsRetryable(c.Error)),
        // Stopped by the call's deadline, not by the application: another call has time.
        CentRetryStoppedException e => e.InnerException is CentTimeoutException && IsRetryable(e.BroadcastError),
        // The errors of an endpoint or bearer token function of the application's.
        TimeoutException => true,
        HttpRequestException e => !IsCertificateError(e),
        SocketException => true,
        _ => false,
    };

    private static bool IsCertificateError(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
        {
            if (e is AuthenticationException)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The delays between attempts: <paramref name="baseDelay"/> after the first failure,
    /// doubling up to <paramref name="limit"/>, each a uniformly random time in
    /// <c>[d/2, d]</c> (spec 6).
    /// </summary>
    /// <param name="baseDelay">The first delay; a negative one counts as zero.</param>
    /// <param name="limit">The longest delay; one below <paramref name="baseDelay"/> counts as it.</param>
    /// <returns>The delay after a failed attempt, from 1; an attempt below 1 counts as 1.</returns>
    /// <example>
    /// Retrying a publish - with the same idempotency key on every attempt:
    /// <code>
    /// var delay = Retry.ExponentialBackoff(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5));
    /// var request = new PublishRequest { Channel = "news", Data = data, IdempotencyKey = $"news-{id}" };
    /// for (var attempt = 1; ; attempt++)
    /// {
    ///     try
    ///     {
    ///         return await client.PublishAsync(request, ct);
    ///     }
    ///     catch (CentException e) when (attempt &lt; 3 &amp;&amp; Retry.IsRetryable(e))
    ///     {
    ///         await Task.Delay(delay(attempt), ct);
    ///     }
    /// }
    /// </code>
    /// </example>
    public static Func<int, TimeSpan> ExponentialBackoff(TimeSpan baseDelay, TimeSpan limit)
    {
        var b = baseDelay < TimeSpan.Zero ? TimeSpan.Zero : baseDelay;
        var l = limit < b ? b : limit;
        return attempt => Jitter(Step(b, l, attempt));
    }

    /// <summary>The delay before jitter: doubling, capped, never overflowing.</summary>
    internal static TimeSpan Step(TimeSpan baseDelay, TimeSpan limit, int attempt)
    {
        var d = baseDelay;
        for (var i = 1; i < attempt && d < limit; i++)
        {
            d = d.Ticks > limit.Ticks / 2 ? limit : TimeSpan.FromTicks(d.Ticks * 2);
        }
        return d > limit ? limit : d;
    }

    private static TimeSpan Jitter(TimeSpan d) =>
        d <= TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromTicks(d.Ticks / 2 + Random.Shared.NextInt64((d.Ticks - d.Ticks / 2) + 1));
}

/// <summary>Turns the exceptions of the HTTP stack into the SDK's.</summary>
internal static class Errors
{
    public static bool IsNetwork(Exception e) => e is HttpRequestException or IOException or SocketException or WebException;
}
