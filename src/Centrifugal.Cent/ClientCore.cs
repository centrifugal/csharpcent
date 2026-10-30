using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Centrifugal.Cent;

/// <summary>
/// What every <see cref="CentClient"/> made from one configuration shares: the HTTP client,
/// credentials, automatic batching and the calls in progress.
/// </summary>
internal sealed class ClientCore : IDisposable
{
    /// <summary>The <c>User-Agent</c>: the SDK and its version - the minor one too before 1.0.</summary>
    public const string UserAgent = "csharpcent/0.1";

    private const int ErrorBodyRead = 4096;
    private const int ErrorBodyKept = 512;

    private static readonly MediaTypeHeaderValue Json = new("application/json");

    private readonly HttpClient _http;
    private readonly SocketsHttpHandler? _ownHandler;
    private readonly string? _endpoint;
    private readonly Func<CancellationToken, ValueTask<string>>? _endpointFunc;
    private readonly string? _apiKey;
    private readonly Func<CancellationToken, ValueTask<string>>? _bearerTokenFunc;
    private readonly List<KeyValuePair<string, string>> _headers = [];
    private readonly string _userAgent = UserAgent;
    private readonly string? _host;
    private readonly Action<RequestInfo>? _onRequest;
    private readonly Tracker _tracker = new();

    public ClientCore(CentClientOptions options)
    {
        if (options is null)
        {
            throw new CentInvalidConfigException("no options: set at least ApiEndpoint");
        }
        if ((options.ApiEndpoint is null) == (options.ApiEndpointFunc is null))
        {
            throw new CentInvalidConfigException(
                "set exactly one of ApiEndpoint and ApiEndpointFunc: the base URL of the API, such as http://localhost:8000/api");
        }
        if (options.ApiEndpoint is not null)
        {
            _endpoint = ValidateEndpoint(options.ApiEndpoint, "ApiEndpoint");
        }
        _endpointFunc = options.ApiEndpointFunc;
        if (options.ApiKey is not null && options.BearerTokenFunc is not null)
        {
            throw new CentInvalidConfigException(
                "set ApiKey or BearerTokenFunc, not both: Centrifugo PRO judges a request with a bearer token by the token alone");
        }
        if (options.ApiKey is not null)
        {
            if (HasControl(options.ApiKey))
            {
                throw new CentInvalidConfigException("ApiKey has control characters: check how it was read, a trailing newline say");
            }
            _apiKey = options.ApiKey.Length > 0 ? options.ApiKey : null;
        }
        _bearerTokenFunc = options.BearerTokenFunc;
        if (options.RequestTimeout < TimeSpan.Zero && options.RequestTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new CentInvalidConfigException("RequestTimeout is negative: give a positive TimeSpan, or Timeout.InfiniteTimeSpan for none");
        }
        RequestTimeout = options.RequestTimeout == TimeSpan.Zero ? TimeSpan.FromSeconds(10) : options.RequestTimeout;
        foreach (var (name, value) in options.Headers ?? new Dictionary<string, string>())
        {
            ValidateHeader(name, value);
            if (name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
            {
                _userAgent = value;
            }
            else if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Length == 0)
                {
                    throw new CentInvalidConfigException("Headers: Host is empty: leave it out to use the endpoint's host");
                }
                _host = value;
            }
            else
            {
                _headers.Add(new(name, value));
            }
        }
        Time = options.TimeProvider ?? TimeProvider.System;
        _onRequest = options.OnRequest;
        BatchContext = options.BatchContext;
        if (options.AutoBatch is { } ab)
        {
            if (ab.MaxInFlight < 0 || ab.MaxBatchSize < 0)
            {
                throw new CentInvalidConfigException("AutoBatch: MaxInFlight and MaxBatchSize cannot be negative");
            }
            if (ab.MaxBatchSize != 0 && ab.MaxInFlight == 0)
            {
                throw new CentInvalidConfigException("AutoBatch: MaxBatchSize without MaxInFlight: set MaxInFlight to enable automatic batching");
            }
            if (ab.MaxBatchSize == 1)
            {
                throw new CentInvalidConfigException("AutoBatch: a MaxBatchSize of 1 never batches: set MaxInFlight alone to limit the requests in flight");
            }
            if (ab.MaxInFlight > 0)
            {
                Batcher = new AutoBatcher(this, ab.MaxInFlight, ab.MaxBatchSize == 0 ? 1000 : ab.MaxBatchSize);
            }
        }
        if (options.HttpClient is { } http)
        {
            _http = http;
        }
        else
        {
            _ownHandler = CreateHandler();
            _http = new HttpClient(_ownHandler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        }
    }

    public TimeSpan RequestTimeout { get; }

    /// <summary>
    /// The default HTTP client's handler (spec 3.2): 256 connections to a host, all kept
    /// while idle, idle ones closed after 50 seconds, reply bodies drained up to 64 KiB, no
    /// redirects, no proxy, no cookies.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        MaxConnectionsPerServer = 256,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(50),
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        MaxResponseDrainSize = 64 * 1024,
        AutomaticDecompression = DecompressionMethods.None,
    };

    public TimeProvider Time { get; }

    public AutoBatcher? Batcher { get; }

    public Func<IReadOnlyList<ExecutionContext>, IDisposable?>? BatchContext { get; }

    /// <summary>Counts a call in progress until it is disposed, or throws <see cref="CentClosedException"/>.</summary>
    public Tracker.Scope Enter() => _tracker.Enter();

    /// <summary>Counts work of the client's own - a batch whose callers may have left - which runs even after close.</summary>
    public Tracker.Scope EnterBackground() => _tracker.EnterBackground();

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        await _tracker.CloseAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        // Closes the idle connections: the calls using the others have all ended.
        Dispose();
    }

    /// <summary>Disposes the client's own HTTP client; one of the application's stays its.</summary>
    public void Dispose()
    {
        if (_ownHandler is not null)
        {
            _http.Dispose();
            _ownHandler.Dispose();
        }
    }

    /// <summary>
    /// A call of one method: encoded, then sent through automatic batching or alone.
    /// Returns its result, or throws its error.
    /// </summary>
    public async Task<JsonElement> CallAsync(string method, bool proOnly, Func<byte[]> encode, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var scope = Enter();
        var start = Time.GetTimestamp();
        byte[] body;
        try
        {
            body = encode();
        }
        catch (CentInvalidRequestException e)
        {
            Report(new RequestInfo(method, 1, 0, false, Time.GetElapsedTime(start), 0, e));
            throw;
        }
        var deadline = Deadline.After(Time, timeout);
        if (Batcher is not null)
        {
            return await Batcher.CallAsync(method, proOnly, body, deadline, cancellationToken).ConfigureAwait(false);
        }
        return await SendAloneAsync(method, proOnly, body, deadline, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends one method in a request of its own.</summary>
    public Task<JsonElement> SendAloneAsync(string method, bool proOnly, byte[] body, Deadline deadline, CancellationToken cancellationToken) =>
        PostAsync(method, proOnly, new ReadOnlyMemory<byte>(body), 1, false, deadline, root =>
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new CentDecodeException(method, $"the reply is a JSON {Kind(root)}, not an object");
            }
            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            {
                return new Outcome<JsonElement>(default, ApiError(method, proOnly, error, inBatch: false), 1);
            }
            return new Outcome<JsonElement>(ResultElement(method, root, "result"), null, 0);
        }, cancellationToken);

    /// <summary>
    /// Sends a request and turns its reply into a value with <paramref name="interpret"/>:
    /// every status, timeout and network failure becomes the SDK's error, and
    /// <see cref="CentClientOptions.OnRequest"/> is told of it.
    /// </summary>
    public async Task<T> PostAsync<T>(
        string method,
        bool proOnly,
        ReadOnlyMemory<byte> body,
        int commands,
        bool autoBatch,
        Deadline deadline,
        Func<JsonElement, Outcome<T>> interpret,
        CancellationToken cancellationToken)
    {
        var start = Time.GetTimestamp();
        var status = 0;
        var failed = 0;
        var replyRead = false;
        Exception? requestError = null;
        Outcome<T> outcome;
        using var timeoutCts = deadline.CreateTimeoutSource(Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var token = linked.Token;
        try
        {
            var endpoint = _endpoint ?? ValidateEndpoint(await _endpointFunc!(token).ConfigureAwait(false), "ApiEndpointFunc");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "/" + method)
            {
                Content = new ReadOnlyMemoryContent(body) { Headers = { ContentType = Json } },
            };
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            if (_apiKey is not null)
            {
                request.Headers.TryAddWithoutValidation("X-API-Key", _apiKey);
            }
            if (_bearerTokenFunc is not null)
            {
                var bearer = await _bearerTokenFunc(token).ConfigureAwait(false);
                if (string.IsNullOrEmpty(bearer) || HasControl(bearer))
                {
                    throw new CentInvalidConfigException(
                        string.IsNullOrEmpty(bearer) ? "BearerTokenFunc returned an empty token" : "BearerTokenFunc returned a token with control characters");
                }
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
            }
            foreach (var (name, value) in _headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
            if (_host is not null)
            {
                request.Headers.Host = _host;
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw await StatusError(method, proOnly, response, token).ConfigureAwait(false);
            }
            JsonElement root;
            var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                try
                {
                    using var doc = await JsonDocument.ParseAsync(stream, default, token).ConfigureAwait(false);
                    root = doc.RootElement.Clone();
                }
                catch (JsonException e)
                {
                    throw new CentDecodeException(method, $"the reply is not JSON: {e.Message}", e);
                }
            }
            outcome = interpret(root);
            replyRead = true;
            failed = outcome.Failed;
        }
        catch (Exception e)
        {
            var mapped = Map(method, e, timeoutCts, deadline, cancellationToken);
            // A command's error in a 200 reply is not the request's: the reply was read.
            if (!replyRead)
            {
                requestError = mapped;
            }
            Report(new RequestInfo(method, commands, failed, autoBatch, Time.GetElapsedTime(start), status, requestError));
            if (ReferenceEquals(mapped, e))
            {
                throw;
            }
            throw mapped;
        }
        Report(new RequestInfo(method, commands, failed, autoBatch, Time.GetElapsedTime(start), status, null));
        // A command's error in a 200 reply: the request's reply was read.
        return outcome.Error is null ? outcome.Value : throw outcome.Error;
    }

    /// <summary>The SDK's error for an exception of the HTTP stack or of the application's functions.</summary>
    private static Exception Map(string method, Exception e, CancellationTokenSource timeout, Deadline deadline, CancellationToken caller)
    {
        if (e is OperationCanceledException && caller.IsCancellationRequested)
        {
            return e;
        }
        if (e is OperationCanceledException && timeout.IsCancellationRequested)
        {
            return new CentTimeoutException(method, deadline.Timeout, e);
        }
        if (e is TaskCanceledException { InnerException: TimeoutException })
        {
            // An HttpClient of the application's, with its own Timeout.
            return new CentTimeoutException(method, deadline.Timeout, e);
        }
        if (e is CentException)
        {
            return e;
        }
        if (Errors.IsNetwork(e))
        {
            return new CentNetworkException(method, e);
        }
        return e;
    }

    private static async Task<Exception> StatusError(string method, bool proOnly, HttpResponseMessage response, CancellationToken token)
    {
        var status = (int)response.StatusCode;
        var headers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            headers[name] = values.ToList();
        }
        var buffer = ArrayPool<byte>.Shared.Rent(ErrorBodyRead);
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            int read;
            await using (stream.ConfigureAwait(false))
            {
                read = await stream.ReadAtLeastAsync(buffer.AsMemory(0, ErrorBodyRead), ErrorBodyRead, throwOnEndOfStream: false, token).ConfigureAwait(false);
            }
            var bytes = buffer.AsSpan(0, read);
            if (TransportModeError(status, bytes) is { } apiError)
            {
                return ApiError(method, proOnly, apiError, inBatch: false);
            }
            var kept = Encoding.UTF8.GetString(bytes[..Math.Min(read, ErrorBodyKept)]).TrimEnd();
            return status switch
            {
                401 => new CentUnauthorizedException(kept, headers),
                >= 300 and < 400 => new CentHttpException(status, kept, headers, "Centrifugo's API never redirects: check http or https in the endpoint"),
                404 when proOnly => new CentHttpException(status, kept, headers, $"{method} is Centrifugo PRO only: the server is likely Centrifugo OSS"),
                404 => new CentHttpException(status, kept, headers, "is the endpoint the API's base URL, such as http://localhost:8000/api?"),
                _ => new CentHttpException(status, kept, headers),
            };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// The error of <c>http_api.error_mode: transport</c> (spec 4.2): a status Centrifugo maps
    /// errors to, with <c>{"code": N, ...}</c> as the body - N a Centrifugo code, not an HTTP
    /// status a gateway would write.
    /// </summary>
    private static JsonElement? TransportModeError(int status, ReadOnlySpan<byte> body)
    {
        if (status is not (400 or 404 or 409 or 416 or 429 or 500))
        {
            return null;
        }
        try
        {
            var reader = new Utf8JsonReader(body);
            if (!JsonDocument.TryParseValue(ref reader, out var doc))
            {
                return null;
            }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("code", out var code)
                    && code.TryGetUInt32(out var n)
                    && n >= 100
                    && n is < 400 or > 599)
                {
                    return root.Clone();
                }
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    /// <summary>A Centrifugo error from its JSON, with the hint of a PRO method's 104 in a batch (spec 4.1).</summary>
    public static CentApiException ApiError(string method, bool proOnly, JsonElement error, bool inBatch)
    {
        ApiErrorWire wire;
        try
        {
            wire = error.Deserialize(Wire.Info<ApiErrorWire>()) ?? new ApiErrorWire();
        }
        catch (JsonException e)
        {
            throw new CentDecodeException(method, $"malformed error: {e.Message}", e);
        }
        var hint = inBatch && proOnly && wire.Code == ErrorCodes.NotFound
            ? $"{method} is Centrifugo PRO only: the server may be Centrifugo OSS"
            : null;
        return new CentApiException(wire.Code, wire.Message, wire.Temporary, hint);
    }

    /// <summary>A reply's result: an object - absent or <c>null</c> is an empty one.</summary>
    public static JsonElement ResultElement(string method, JsonElement reply, string name)
    {
        if (!reply.TryGetProperty(name, out var result) || result.ValueKind == JsonValueKind.Null)
        {
            return EmptyObject;
        }
        if (result.ValueKind != JsonValueKind.Object)
        {
            throw new CentDecodeException(method, $"the result is a JSON {Kind(result)}, not an object");
        }
        return result;
    }

    public static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    public static string Kind(JsonElement e) => e.ValueKind.ToString().ToLowerInvariant();

    public void Report(RequestInfo info) => _onRequest?.Invoke(info);

    private static string ValidateEndpoint(string endpoint, string field)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.Host.Length == 0)
        {
            throw new CentInvalidConfigException($"{field} \"{endpoint}\" is not an absolute http or https URL with a host, such as http://localhost:8000/api");
        }
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
        {
            throw new CentInvalidConfigException($"{field} \"{endpoint}\" has a query, fragment or credentials: give the API's base URL, and credentials as ApiKey");
        }
        var trimmed = endpoint.TrimEnd('/');
        var last = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        if (ApiMethods.ProOnly.ContainsKey(last))
        {
            throw new CentInvalidConfigException(
                $"{field} \"{endpoint}\" ends in the method {last}: give the API's base URL, such as http://localhost:8000/api - the SDK adds the method");
        }
        return trimmed;
    }

    private static void ValidateHeader(string name, string value)
    {
        if (name.Length == 0 || name.Any(c => c > 126 || c <= 32 || "\"(),/:;<=>?@[\\]{}".Contains(c, StringComparison.Ordinal)))
        {
            throw new CentInvalidConfigException($"Headers: \"{name}\" is not a valid header name");
        }
        if (name.Equals("X-API-Key", StringComparison.OrdinalIgnoreCase) || name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
        {
            throw new CentInvalidConfigException($"Headers: {name} is a credential: set ApiKey or BearerTokenFunc");
        }
        if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
        {
            throw new CentInvalidConfigException("Headers: Content-Type is the SDK's: requests are always JSON");
        }
        if (value is null || HasControl(value))
        {
            throw new CentInvalidConfigException($"Headers: the value of {name} has control characters");
        }
    }

    /// <summary>Control characters but tab: they would end a header line.</summary>
    private static bool HasControl(string s) => s.Any(c => (c < 32 && c != '\t') || c == 127);
}

/// <summary>A reply turned into a value, or the call's error, and how many commands failed.</summary>
internal readonly record struct Outcome<T>(T Value, Exception? Error, int Failed);

/// <summary>When a call ends: <see cref="Timeout"/> from its start, or never.</summary>
internal readonly record struct Deadline(long Timestamp, TimeSpan Timeout)
{
    /// <summary>Timeouts longer than this are no timeout: a timer cannot hold them.</summary>
    private static readonly TimeSpan Longest = TimeSpan.FromDays(24);

    public bool IsInfinite => Timestamp == long.MaxValue;

    public static Deadline After(TimeProvider time, TimeSpan timeout)
    {
        if (timeout == System.Threading.Timeout.InfiniteTimeSpan || timeout > Longest)
        {
            return new Deadline(long.MaxValue, timeout);
        }
        return new Deadline(time.GetTimestamp() + (long)(timeout.TotalSeconds * time.TimestampFrequency), timeout);
    }

    /// <summary>The time left, never negative; <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> without a deadline.</summary>
    public TimeSpan Remaining(TimeProvider time)
    {
        if (IsInfinite)
        {
            return System.Threading.Timeout.InfiniteTimeSpan;
        }
        var left = time.GetElapsedTime(time.GetTimestamp(), Timestamp);
        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }

    public bool Passed(TimeProvider time) => !IsInfinite && time.GetTimestamp() >= Timestamp;

    /// <summary>A source cancelled when the deadline passes.</summary>
    public CancellationTokenSource CreateTimeoutSource(TimeProvider time) => new(Remaining(time), time);

    /// <summary>The later of two deadlines.</summary>
    public static Deadline Max(Deadline a, Deadline b) => a.Timestamp >= b.Timestamp ? a : b;

    public static Deadline Min(Deadline a, Deadline b) => a.Timestamp <= b.Timestamp ? a : b;
}

/// <summary>The calls in progress, for <see cref="CentClient.CloseAsync"/>.</summary>
internal sealed class Tracker
{
    private readonly object _lock = new();
    private int _count;
    private bool _closed;
    private TaskCompletionSource? _idle;

    public Scope Enter()
    {
        lock (_lock)
        {
            if (_closed)
            {
                throw new CentClosedException();
            }
            _count++;
        }
        return new Scope(this);
    }

    public Scope EnterBackground()
    {
        lock (_lock)
        {
            _count++;
        }
        return new Scope(this);
    }

    public Task CloseAsync()
    {
        lock (_lock)
        {
            _closed = true;
            if (_count == 0)
            {
                return Task.CompletedTask;
            }
            _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _idle.Task;
        }
    }

    private void Exit()
    {
        lock (_lock)
        {
            _count--;
            if (_closed && _count == 0)
            {
                _idle?.TrySetResult();
            }
        }
    }

    public readonly struct Scope(Tracker tracker) : IDisposable
    {
        public void Dispose() => tracker.Exit();
    }
}
