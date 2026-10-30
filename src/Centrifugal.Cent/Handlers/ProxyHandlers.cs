using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Centrifugal.Cent.Proxy;

/// <summary>
/// The backend side of Centrifugo's <see href="https://centrifugal.dev/docs/server/proxy">HTTP
/// proxy</see> (spec 14): a handler per proxy type, built from a function taking the decoded
/// request and returning the result.
/// </summary>
/// <remarks>
/// <para>
/// The function returns a result to accept the request; throws
/// <see cref="ProxyErrorException"/> to reject it with an error the client receives - a
/// code in [400, 1999]; or throws <see cref="ProxyDisconnectException"/> to close the
/// client's connection - a code in [4000, 4999], those in [4500, 4999] telling the client
/// not to reconnect, and a reason of at most 32 bytes. Wrapped ones count. Any other
/// exception fails the request with HTTP status 500 and goes to
/// <see cref="ProxyHandlerOptions.OnError"/>: Centrifugo answers the client with a temporary
/// internal error, and for refresh and sub refresh asks again a minute later.
/// </para>
/// <para>
/// Keep proxy endpoints reachable by Centrifugo only: whoever can call them can claim to be
/// any user.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var proxy = new ProxyHandlers(new ProxyHandlerOptions { OnError = (_, e) => logger.LogError(e, "proxy") });
/// var connect = proxy.Connect(async (request, context) =>
/// {
///     var user = await sessions.UserAsync(request.Data, context.CancellationToken)
///         ?? throw new ProxyDisconnectException(4501, "unauthorized");
///     return new ConnectResult { User = user.Id };
/// });
/// app.MapCentrifugo("/centrifugo/connect", connect); // Centrifugal.Cent.AspNetCore
/// </code>
/// </example>
public sealed partial class ProxyHandlers
{
    private readonly ProxyHandlerOptions _options;

    /// <summary>Creates handlers sharing their options.</summary>
    /// <param name="options">The options; defaults when null.</param>
    public ProxyHandlers(ProxyHandlerOptions? options = null)
    {
        _options = options ?? new ProxyHandlerOptions();
        if (_options.MaxRequestBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), _options.MaxRequestBytes, "MaxRequestBytes is positive");
        }
    }

    /// <summary>Which answers a proxy type's response carries: those Centrifugo acts on.</summary>
    private enum Answers
    {
        /// <summary>A result, an error or a disconnect.</summary>
        All,

        /// <summary>A result or an error.</summary>
        ErrorOrResult,

        /// <summary>Only a result: refresh and sub refresh end through the result's <c>Expired</c>.</summary>
        ResultOnly,
    }

    private CentHandler Create<TRequest, TResult>(
        string name,
        Answers answers,
        Func<TRequest, HandlerContext, Task<TResult>> handle,
        JsonTypeInfo<TRequest> requestInfo,
        JsonTypeInfo<TResult> resultInfo)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var onError = _options.OnError;
        return new CentHandler(_options.MaxRequestBytes, async (request, cancellationToken) =>
        {
            if (!Responses.IsPost(request.Method))
            {
                return Responses.MethodNotAllowed("POST");
            }
            TRequest decoded;
            try
            {
                decoded = JsonSerializer.Deserialize(request.Body.Span, requestInfo)
                    ?? throw Responses.Malformed(name);
            }
            catch (JsonException e)
            {
                return Responses.Fail(request, onError, 400, Responses.Malformed(name, e));
            }

            var body = new ArrayBufferWriter<byte>();
            try
            {
                TResult result;
                try
                {
                    result = await handle(decoded, new HandlerContext(request, cancellationToken)).ConfigureAwait(false);
                }
                catch (Exception e) when (Responses.Find<ProxyErrorException>(e) is { } rejected && answers != Answers.ResultOnly)
                {
                    Write(body, w => JsonSerializer.Serialize(w, rejected.Error, Wire.Info<ProxyError>()), "error");
                    return Responses.Json(body.WrittenSpan.ToArray());
                }
                catch (Exception e) when (Responses.Find<ProxyDisconnectException>(e) is { } disconnect && answers == Answers.All)
                {
                    Write(body, w => JsonSerializer.Serialize(w, disconnect.Disconnect, Wire.Info<Disconnect>()), "disconnect");
                    return Responses.Json(body.WrittenSpan.ToArray());
                }
                catch (Exception e) when (Responses.Find<ProxyErrorException>(e) is not null || Responses.Find<ProxyDisconnectException>(e) is not null)
                {
                    var what = Responses.Find<ProxyErrorException>(e) is not null ? "an error" : "a disconnect";
                    return Responses.Fail(request, onError, 500, new InvalidOperationException(
                        $"the {name} proxy cannot answer with {what} ({e.Message}): failing the request", e));
                }
                if (result is null)
                {
                    return Responses.Fail(request, onError, 500, new InvalidOperationException($"the {name} proxy function returned no result"));
                }
                Write(body, w => JsonSerializer.Serialize(w, result, resultInfo), "result");
                return Responses.Json(body.WrittenSpan.ToArray());
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return Responses.Fail(request, onError, 500, e);
            }
        });
    }

    /// <summary>Writes <c>{"name": ...}</c>: exactly one of result, error and disconnect.</summary>
    private static void Write(ArrayBufferWriter<byte> body, Action<Utf8JsonWriter> value, string name)
    {
        using var w = new Utf8JsonWriter(body);
        w.WriteStartObject();
        w.WritePropertyName(name);
        value(w);
        w.WriteEndObject();
    }
}

/// <summary>The options of <see cref="ProxyHandlers"/>.</summary>
public sealed class ProxyHandlerOptions
{
    /// <summary>
    /// Told of every request a handler could not answer as its function meant: one failed with
    /// HTTP status 500, and a malformed one, rejected with 400. Centrifugo only logs the status,
    /// so this is where the cause is seen. It must not throw.
    /// </summary>
    public Action<HandlerRequest, Exception>? OnError { get; set; }

    /// <summary>
    /// Bounds the body of a request: 16 MiB by default, far above what Centrifugo's own limits
    /// on client messages let through - it only stops a request which is not Centrifugo's from
    /// taking the memory it likes.
    /// </summary>
    public long MaxRequestBytes { get; set; } = 16 << 20;
}

/// <summary>Rejects a proxy request with an error the client receives.</summary>
public sealed class ProxyErrorException : Exception
{
    /// <summary>Rejects a request.</summary>
    /// <param name="code">A code in [400, 1999]: the range Centrifugo leaves to applications.</param>
    /// <param name="message">The message the client receives.</param>
    /// <param name="temporary">Whether the client may retry.</param>
    /// <exception cref="ArgumentOutOfRangeException">The code is outside [400, 1999].</exception>
    public ProxyErrorException(int code, string message, bool temporary = false)
        : base($"proxy error {code}: {message}")
    {
        if (code is < 400 or > 1999)
        {
            throw new ArgumentOutOfRangeException(nameof(code), code, "a proxy error code is in [400, 1999]");
        }
        Error = new ProxyError { Code = (uint)code, Message = message, Temporary = temporary };
    }

    /// <summary>The error the client receives.</summary>
    public ProxyError Error { get; }
}

/// <summary>Closes the connection of the client whose request the proxy got.</summary>
public sealed class ProxyDisconnectException : Exception
{
    /// <summary>Closes a connection.</summary>
    /// <param name="code">A code in [4000, 4999]; those in [4500, 4999] tell the client not to reconnect.</param>
    /// <param name="reason">At most 32 bytes, UTF-8 encoded.</param>
    /// <exception cref="ArgumentOutOfRangeException">The code is outside [4000, 4999], or the reason longer than 32 bytes.</exception>
    public ProxyDisconnectException(int code, string reason)
        : base($"disconnect {code}: {reason}")
    {
        if (code is < 4000 or > 4999)
        {
            throw new ArgumentOutOfRangeException(nameof(code), code, "a disconnect code is in [4000, 4999]");
        }
        if (Encoding.UTF8.GetByteCount(reason) > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "a disconnect reason is at most 32 bytes");
        }
        Disconnect = new Disconnect { Code = (uint)code, Reason = reason };
    }

    /// <summary>The disconnect.</summary>
    public Disconnect Disconnect { get; }
}
