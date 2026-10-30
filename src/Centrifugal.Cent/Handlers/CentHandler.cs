using System.Text;
using System.Text.Json;

namespace Centrifugal.Cent;

/// <summary>An HTTP request as a <see cref="CentHandler"/> takes it, from whatever framework received it.</summary>
public sealed class HandlerRequest
{
    /// <summary>Creates a request.</summary>
    /// <param name="method">The HTTP method, such as <c>POST</c>.</param>
    /// <param name="body">The body, read up to the handler's <see cref="CentHandler.MaxBodyBytes"/> and one byte more.</param>
    public HandlerRequest(string method, ReadOnlyMemory<byte> body)
    {
        Method = method;
        Body = body;
    }

    /// <summary>The HTTP method.</summary>
    public string Method { get; }

    /// <summary>The body, as read.</summary>
    public ReadOnlyMemory<byte> Body { get; }

    /// <summary>The query string's parameters; the first value of each.</summary>
    public IReadOnlyDictionary<string, string> Query { get; init; } = new Dictionary<string, string>();

    /// <summary>The headers, matched case-insensitively; values of a header given twice joined by <c>, </c>.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The framework's own request object - ASP.NET Core's <c>HttpContext</c> - for what the handler's function needs of it: cookies, the authenticated user.</summary>
    public object? FrameworkContext { get; init; }
}

/// <summary>An HTTP response of a <see cref="CentHandler"/>, for the framework to write.</summary>
/// <param name="StatusCode">The HTTP status.</param>
/// <param name="Headers">The headers.</param>
/// <param name="Body">The body.</param>
public sealed record HandlerResponse(int StatusCode, IReadOnlyDictionary<string, string> Headers, ReadOnlyMemory<byte> Body)
{
    /// <summary>
    /// Why the handler failed the request - a malformed request (400), or a failure of its
    /// function (500) - for the framework's logs; null otherwise. The response says nothing
    /// of it to the caller.
    /// </summary>
    public Exception? Error { get; init; }
}

/// <summary>What a handler's function gets with its request.</summary>
public sealed class HandlerContext
{
    internal HandlerContext(HandlerRequest request, CancellationToken cancellationToken)
    {
        Request = request;
        CancellationToken = cancellationToken;
    }

    /// <summary>The HTTP request: the headers Centrifugo passed on, the framework's context.</summary>
    public HandlerRequest Request { get; }

    /// <summary>The request's headers, matched case-insensitively.</summary>
    public IReadOnlyDictionary<string, string> Headers => Request.Headers;

    /// <summary>Cancelled when the caller gives up - for a proxy, at the proxy's timeout.</summary>
    public CancellationToken CancellationToken { get; }
}

/// <summary>
/// An HTTP handler of the SDK - a proxy handler, an endpoint for client SDKs, a JWKS
/// endpoint - free of any framework: it takes a <see cref="HandlerRequest"/> and returns a
/// <see cref="HandlerResponse"/>. The <c>Centrifugal.Cent.AspNetCore</c> package serves it
/// with ASP.NET Core; any other framework calls <see cref="HandleAsync"/>.
/// </summary>
public sealed class CentHandler
{
    private readonly Func<HandlerRequest, CancellationToken, Task<HandlerResponse>> _handle;

    internal CentHandler(long maxBodyBytes, Func<HandlerRequest, CancellationToken, Task<HandlerResponse>> handle)
    {
        MaxBodyBytes = maxBodyBytes;
        _handle = handle;
    }

    /// <summary>The longest body the handler takes: an adapter reads at most this and one byte more.</summary>
    public long MaxBodyBytes { get; }

    /// <summary>Answers a request.</summary>
    /// <param name="request">The request, its body read.</param>
    /// <param name="cancellationToken">Cancelled when the caller gives up.</param>
    /// <returns>The response to write.</returns>
    public Task<HandlerResponse> HandleAsync(HandlerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Body.Length > MaxBodyBytes)
        {
            return Task.FromResult(Responses.Text(400, $"the body is longer than {MaxBodyBytes} bytes"));
        }
        return _handle(request, cancellationToken);
    }
}

/// <summary>The responses the handlers share.</summary>
internal static class Responses
{
    private static readonly IReadOnlyDictionary<string, string> JsonHeaders = Headers(("Content-Type", "application/json"));

    public static IReadOnlyDictionary<string, string> Headers(params (string Name, string Value)[] headers) =>
        headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase);

    public static HandlerResponse Json(byte[] body, IReadOnlyDictionary<string, string>? headers = null) => new(200, headers ?? JsonHeaders, body);

    public static HandlerResponse Text(int status, string text) =>
        new(status, Headers(("Content-Type", "text/plain; charset=utf-8")), Encoding.UTF8.GetBytes(text + "\n"));

    public static HandlerResponse MethodNotAllowed(string allow) =>
        new(405, Headers(("Allow", allow), ("Content-Type", "text/plain; charset=utf-8")), "method not allowed\n"u8.ToArray());

    /// <summary>Fails a request with a status, telling <paramref name="onError"/> why.</summary>
    public static HandlerResponse Fail(HandlerRequest request, Action<HandlerRequest, Exception>? onError, int status, Exception error)
    {
        onError?.Invoke(request, error);
        return Text(status, status == 400 ? "bad request" : "internal server error") with { Error = error };
    }

    public static bool IsPost(string method) => method.Equals("POST", StringComparison.OrdinalIgnoreCase);

    public static T? Find<T>(Exception? e)
        where T : Exception
    {
        for (; e is not null; e = e.InnerException)
        {
            if (e is T found)
            {
                return found;
            }
            if (e is AggregateException { InnerExceptions.Count: 1 } agg && Find<T>(agg.InnerExceptions[0]) is { } inner)
            {
                return inner;
            }
        }
        return null;
    }

    public static JsonException Malformed(string what, Exception? cause = null) => new($"malformed {what} request{(cause is null ? "" : ": " + cause.Message)}", cause);
}
