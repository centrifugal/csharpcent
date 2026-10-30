using System.Buffers;
using Centrifugal.Cent;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Serves the handlers of Centrifugal.Cent - proxy handlers, endpoints for client SDKs, JWKS - with ASP.NET Core.</summary>
public static partial class CentrifugoEndpointRouteBuilderExtensions
{
    /// <summary>Serves a handler at a route, for every HTTP method: the handler answers a wrong one with 405.</summary>
    /// <param name="endpoints">The application's routes.</param>
    /// <param name="pattern">The route, such as <c>/centrifugo/connect</c>.</param>
    /// <param name="handler">The handler.</param>
    /// <returns>The endpoint, for its conventions: authorization, a host filter.</returns>
    /// <example>
    /// <code>
    /// var proxy = new ProxyHandlers();
    /// app.MapCentrifugo("/centrifugo/connect", proxy.Connect(Connect));
    /// app.MapCentrifugo("/.well-known/jwks.json", Jwks.Endpoint([key]));
    /// </code>
    /// </example>
    public static IEndpointConventionBuilder MapCentrifugo(this IEndpointRouteBuilder endpoints, string pattern, CentHandler handler)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(handler);
        return endpoints.Map(pattern, handler.ToRequestDelegate());
    }

    /// <summary>
    /// The handler as an ASP.NET Core <see cref="RequestDelegate"/>, for any routing or
    /// middleware. A request the handler fails is logged - a malformed one (400) as a
    /// warning, a failure of its function (500) as an error - under the category
    /// <c>Centrifugal.Cent</c>, besides going to the handler's <c>OnError</c>.
    /// </summary>
    /// <param name="handler">The handler.</param>
    /// <returns>The request delegate.</returns>
    public static RequestDelegate ToRequestDelegate(this CentHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return async context =>
        {
            var request = context.Request;
            var aborted = context.RequestAborted;
            var body = await ReadBodyAsync(request, handler.MaxBodyBytes, aborted).ConfigureAwait(false);
            var query = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, values) in request.Query)
            {
                query[name] = values.Count > 0 ? values[0] ?? "" : "";
            }
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, values) in request.Headers)
            {
                headers[name] = values.ToString();
            }
            var response = await handler.HandleAsync(
                new HandlerRequest(request.Method, body) { Query = query, Headers = headers, FrameworkContext = context },
                aborted).ConfigureAwait(false);
            if (response.Error is { } error)
            {
                var logger = context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("Centrifugal.Cent");
                if (logger is not null)
                {
                    if (response.StatusCode >= 500)
                    {
                        LogFailed(logger, request.Method, request.Path, response.StatusCode, error);
                    }
                    else
                    {
                        LogRefused(logger, request.Method, request.Path, response.StatusCode, error);
                    }
                }
            }
            context.Response.StatusCode = response.StatusCode;
            foreach (var (name, value) in response.Headers)
            {
                context.Response.Headers[name] = value;
            }
            if (!HttpMethods.IsHead(request.Method) && response.Body.Length > 0)
            {
                context.Response.ContentLength = response.Body.Length;
                await context.Response.Body.WriteAsync(response.Body, aborted).ConfigureAwait(false);
            }
        };
    }

    /// <summary>Reads the body up to the limit and one byte more, so the handler tells a body over it.</summary>
    private static async Task<ReadOnlyMemory<byte>> ReadBodyAsync(HttpRequest request, long max, CancellationToken cancellationToken)
    {
        if (request.ContentLength == 0)
        {
            return ReadOnlyMemory<byte>.Empty;
        }
        var limit = max + 1;
        var buffer = new ArrayBufferWriter<byte>();
        while (buffer.WrittenCount < limit)
        {
            var span = buffer.GetMemory((int)Math.Min(16 * 1024, limit - buffer.WrittenCount));
            var read = await request.Body.ReadAsync(span, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            buffer.Advance(read);
        }
        return buffer.WrittenMemory;
    }

    /// <summary>The ASP.NET Core context of a handler's request: its user, cookies, services.</summary>
    /// <param name="request">The request a handler's function got.</param>
    /// <returns>The context.</returns>
    /// <exception cref="InvalidOperationException">The request was not served by ASP.NET Core.</exception>
    public static HttpContext GetHttpContext(this HandlerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.FrameworkContext as HttpContext
            ?? throw new InvalidOperationException("the request was not served by ASP.NET Core");
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Centrifugo handler failed {Method} {Path} with status {Status}")]
    private static partial void LogFailed(ILogger logger, string method, PathString path, int status, Exception error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Centrifugo handler refused {Method} {Path} with status {Status}")]
    private static partial void LogRefused(ILogger logger, string method, PathString path, int status, Exception error);
}
