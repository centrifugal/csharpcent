namespace Centrifugal.Cent;

/// <summary>The configuration of a <see cref="CentClient"/>, validated when the client is created (spec 3.1).</summary>
public sealed class CentClientOptions
{
    /// <summary>
    /// The base URL of the API, such as <c>http://localhost:8000/api</c>: every method is a
    /// <c>POST</c> to it followed by the method's name. Exactly one of this and
    /// <see cref="ApiEndpointFunc"/> is set.
    /// </summary>
    public string? ApiEndpoint { get; set; }

    /// <summary>
    /// Returns the base URL of the API per request, for addresses discovered at run time.
    /// Its result is validated as <see cref="ApiEndpoint"/> is, per request.
    /// </summary>
    public Func<CancellationToken, ValueTask<string>>? ApiEndpointFunc { get; set; }

    /// <summary>The API key, sent as <c>X-API-Key</c>. Not together with <see cref="BearerTokenFunc"/>.</summary>
    /// <remarks>
    /// No credentials at all is valid - mutual TLS, a proxy, or <c>http_api.insecure</c> - so a
    /// missing key shows as <see cref="CentUnauthorizedException"/> on the first call.
    /// </remarks>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Returns a token sent as <c>Authorization: Bearer</c>, per request: Centrifugo PRO's API
    /// tokens - see <see cref="Tokens.ApiTokenSource"/>. Not together with <see cref="ApiKey"/>:
    /// Centrifugo PRO judges a request with a bearer token by the token alone.
    /// </summary>
    public Func<CancellationToken, ValueTask<string>>? BearerTokenFunc { get; set; }

    /// <summary>
    /// The HTTP client, such as one of <c>IHttpClientFactory</c>. The SDK never changes it -
    /// credentials and headers go on each request - and leaves its redirect policy to it.
    /// </summary>
    /// <remarks>
    /// By default the client keeps up to 256 connections alive, all of them while idle,
    /// closes idle ones after 50 seconds, follows no redirect and uses no proxy. Its
    /// requests then have the call's deadline as their only timeout.
    /// </remarks>
    public HttpClient? HttpClient { get; set; }

    /// <summary>Bounds every call: 10 seconds by default. <see cref="CentClient.WithTimeout"/> sets another for some calls.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Headers sent with every request. <c>User-Agent</c> replaces the SDK's own and
    /// <c>Host</c> sets the request's host; credentials and <c>Content-Type</c> are refused.
    /// </summary>
    public IDictionary<string, string>? Headers { get; set; }

    /// <summary>Sends concurrent calls together under load: see <see cref="AutoBatchOptions"/>.</summary>
    public AutoBatchOptions? AutoBatch { get; set; }

    /// <summary>
    /// Called after every request, synchronously - the call waits for it - with what it
    /// did: for logs and metrics. It must not throw: what it throws is the call's error.
    /// </summary>
    public Action<RequestInfo>? OnRequest { get; set; }

    /// <summary>
    /// Makes the context of an automatic batch's request from its callers' contexts: a batch
    /// runs in an empty one, never a caller's. For a tracing span linking every caller's:
    /// read each caller's <c>Activity.Current</c> with <c>ExecutionContext.Run</c>, start an
    /// activity with links to them, and return it to be disposed after the request.
    /// </summary>
    public Func<IReadOnlyList<ExecutionContext>, IDisposable?>? BatchContext { get; set; }

    /// <summary>The time, for tests: deadlines, retry waits.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>A copy, with another HTTP client.</summary>
    internal CentClientOptions WithHttpClient(HttpClient httpClient)
    {
        var copy = (CentClientOptions)MemberwiseClone();
        copy.HttpClient = httpClient;
        return copy;
    }
}

/// <summary>
/// Automatic batching: concurrent calls sent together, cutting the requests Centrifugo
/// handles under load (spec 8). For processes making many calls at once - a web
/// application publishing on every request, a worker draining a queue.
/// </summary>
/// <remarks>
/// <para>
/// Below <see cref="MaxInFlight"/> requests in flight every call is sent at once, alone:
/// nothing ever waits for a batch to fill. At the cap, calls queue, and when a request
/// finishes all those queued go out as one batch - at most <see cref="MaxBatchSize"/> -
/// to <c>/api/batch</c> with <c>parallel: true</c>. Every call gets exactly its own reply;
/// a batch failing as a whole fails every call in it.
/// </para>
/// <para>
/// Choose <see cref="MaxInFlight"/> at about half the calls the process makes at once:
/// above that it never batches, and just below it batches calls one at a time, only
/// limiting them. A proxy in front of Centrifugo may limit a request's size where
/// Centrifugo does not - nginx: 1 MB by default - failing a whole batch with a 413 its
/// calls would not get alone: lower <see cref="MaxBatchSize"/>, or raise the proxy's limit.
/// </para>
/// </remarks>
public sealed class AutoBatchOptions
{
    /// <summary>The requests in flight at most; 0 disables automatic batching.</summary>
    public int MaxInFlight { get; set; }

    /// <summary>The calls in one batch at most: 1000 when 0.</summary>
    public int MaxBatchSize { get; set; }
}

/// <summary>What a request did, for <see cref="CentClientOptions.OnRequest"/>.</summary>
/// <param name="Method">The API method; <c>batch</c> for batches.</param>
/// <param name="Commands">The calls it carried: 1 for a method, the commands of a batch.</param>
/// <param name="Failed">The commands answered with an error. A broadcast failing in some channels is not counted.</param>
/// <param name="AutoBatch">Whether it was an automatic batch.</param>
/// <param name="Duration">From before getting the endpoint and credentials to the reply read.</param>
/// <param name="StatusCode">The HTTP status; 0 without a reply.</param>
/// <param name="Error">
/// The request's own error; null when Centrifugo's reply was read. In the transport error
/// mode, the error of its one command, which <paramref name="Failed"/> then does not count.
/// </param>
public sealed record RequestInfo(string Method, int Commands, int Failed, bool AutoBatch, TimeSpan Duration, int StatusCode, Exception? Error);
