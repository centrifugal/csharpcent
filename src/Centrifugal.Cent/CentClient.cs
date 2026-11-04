using System.Text.Json;

namespace Centrifugal.Cent;

/// <summary>
/// The client of Centrifugo's HTTP server API: a method per API method, taking its request
/// and returning its result. Create one per process and share it: it is safe for
/// concurrent use, and keeps its connections alive between calls.
/// </summary>
/// <remarks>
/// <para>
/// Every call returns its result only when it did everything asked, and throws a
/// <see cref="CentException"/> otherwise - a broadcast in which some channel failed too
/// (<see cref="BroadcastFailedChannelsException"/>). <see cref="Retry.IsRetryable"/> tells
/// whether another attempt may succeed.
/// </para>
/// <para>
/// Every call ends at its deadline: <see cref="CentClientOptions.RequestTimeout"/>, or the
/// timeout of <see cref="WithTimeout"/>, which replaces it - connecting, sending and
/// reading the reply included - and throws <see cref="CentTimeoutException"/> then. Its
/// <see cref="CancellationToken"/> only shortens it, never extends it: a call made with the
/// token of a request which lives for minutes still gives up on a stuck Centrifugo at its
/// timeout, throwing <see cref="OperationCanceledException"/> when the token is cancelled
/// first.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var client = new CentClient(new CentClientOptions
/// {
///     ApiEndpoint = "http://localhost:8000/api",
///     ApiKey = apiKey,
/// });
/// var result = await client.PublishAsync(new PublishRequest { Channel = "news", Data = new { text = "hello" } });
/// </code>
/// </example>
/// <remarks>
/// Its methods are virtual, so that application code using it is tested with a mocking
/// library - <c>Substitute.For&lt;CentClient&gt;()</c> in NSubstitute,
/// <c>new Mock&lt;CentClient&gt;()</c> in Moq - or a class of its own deriving from it.
/// </remarks>
public partial class CentClient : IAsyncDisposable, IDisposable
{
    private readonly ClientCore _core;
    private readonly TimeSpan _timeout;

    // Whether the timeout is a view's own, from WithTimeout, rather than RequestTimeout.
    private readonly bool _ownTimeout;

    /// <summary>Creates a client.</summary>
    /// <param name="options">The configuration, validated now.</param>
    /// <exception cref="CentInvalidConfigException">The configuration is not valid: the message names the option and the fix.</exception>
    public CentClient(CentClientOptions options)
    {
        _core = new ClientCore(options);
        _timeout = _core.RequestTimeout;
    }

    /// <summary>Creates a client of an API endpoint, with an API key.</summary>
    /// <param name="apiEndpoint">The base URL of the API, such as <c>http://localhost:8000/api</c>.</param>
    /// <param name="apiKey">The API key; none for Centrifugo without one.</param>
    /// <exception cref="CentInvalidConfigException">The endpoint or key is not valid.</exception>
    public CentClient(string apiEndpoint, string? apiKey = null)
        : this(new CentClientOptions { ApiEndpoint = apiEndpoint, ApiKey = apiKey })
    {
    }

    /// <summary>
    /// For mocking only: a client derived from it sends nothing unless its methods are
    /// overridden, and calls to the others fail.
    /// </summary>
    protected CentClient()
    {
        _core = null!;
    }

    private CentClient(ClientCore core, TimeSpan timeout, bool ownTimeout)
    {
        _core = core;
        _timeout = timeout;
        _ownTimeout = ownTimeout;
    }

    /// <summary>The timeout of this client's calls: <see cref="CentClientOptions.RequestTimeout"/>, or <see cref="WithTimeout"/>'s.</summary>
    public virtual TimeSpan Timeout => _timeout;

    /// <summary>
    /// Returns a client whose calls have another timeout, sharing everything else with this
    /// one: its connections, automatic batching, and <see cref="CloseAsync"/>. The timeout
    /// replaces <see cref="CentClientOptions.RequestTimeout"/> for those calls, longer or
    /// shorter; a call's <see cref="CancellationToken"/> still ends it sooner.
    /// </summary>
    /// <param name="timeout">The timeout of each call: positive, at most 24 days. A call cannot be left unbounded.</param>
    /// <returns>The client.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is zero, negative, infinite or longer than 24 days.</exception>
    /// <example>
    /// <code>
    /// var history = await client.WithTimeout(TimeSpan.FromSeconds(30)).HistoryAsync(request, ct);
    /// </code>
    /// </example>
    public virtual CentClient WithTimeout(TimeSpan timeout)
    {
        if (!Deadline.IsValid(timeout))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "a timeout is positive and at most 24 days: a call cannot be left unbounded");
        }
        return new CentClient(_core, timeout, ownTimeout: true);
    }

    /// <summary>
    /// Returns a batch: commands sent together in one request (spec 7.2). A batch is sent
    /// once, and is not safe for concurrent use.
    /// </summary>
    /// <param name="parallel">
    /// Whether Centrifugo runs the commands concurrently - with no order between them -
    /// rather than one after another. Centrifugo PRO sends a sequential batch's
    /// publications to its broker together: each channel's take effect in order, while
    /// publications into different channels may not, unless the namespace sets
    /// <c>publication_grouping_disabled</c>.
    /// </param>
    /// <returns>An empty batch.</returns>
    public virtual CentBatch NewBatch(bool parallel = false) => new(_core, _timeout, parallel);

    /// <summary>
    /// Refuses new calls with <see cref="CentClosedException"/> and waits for those in
    /// progress - batches whose callers left included - then closes the idle connections of
    /// the client's own HTTP client. It may be called again.
    /// </summary>
    /// <param name="cancellationToken">Ends the wait, leaving the calls in progress to go on.</param>
    /// <returns>A task completed when every call has ended.</returns>
    public virtual Task CloseAsync(CancellationToken cancellationToken = default) => _core.CloseAsync(cancellationToken);

    /// <summary>Closes the client: see <see cref="CloseAsync"/>.</summary>
    /// <returns>A task completed when every call has ended.</returns>
    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>Closes the client, blocking until every call has ended: prefer <see cref="DisposeAsync"/>.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Closes the client: what <see cref="DisposeAsync"/> does, for a derived class to extend.</summary>
    /// <returns>A task completed when every call has ended.</returns>
    protected virtual async ValueTask DisposeAsyncCore()
    {
        if (_core is not null)
        {
            await CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Closes the client: what <see cref="Dispose()"/> does, for a derived class to extend.</summary>
    /// <param name="disposing">Whether it is called by <see cref="Dispose()"/>, not a finalizer.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing && _core is not null)
        {
            CloseAsync().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Publishes data into a channel: the short form of <see cref="PublishAsync(PublishRequest, CancellationToken)"/>,
    /// which it calls - a mock of that one answers this one too.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="data">The payload: any JSON value - see <see cref="PublishRequest.Data"/>.</param>
    /// <param name="cancellationToken">Cancels the call: it then throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>The publication's position in the channel's stream.</returns>
    /// <exception cref="CentException">The call did not succeed: see the exception's type.</exception>
    public Task<PublishResult> PublishAsync(string channel, object data, CancellationToken cancellationToken = default) =>
        PublishAsync(new PublishRequest { Channel = channel, Data = data }, cancellationToken);

    /// <summary>
    /// Publishes the same data into many channels: the short form of
    /// <see cref="BroadcastAsync(BroadcastRequest, CancellationToken)"/>, which it calls.
    /// </summary>
    /// <param name="channels">The channels.</param>
    /// <param name="data">The payload: any JSON value - see <see cref="BroadcastRequest.Data"/>.</param>
    /// <param name="cancellationToken">Cancels the call: it then throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>Every channel's publication.</returns>
    /// <exception cref="BroadcastFailedChannelsException">Some channels failed.</exception>
    /// <exception cref="CentException">The broadcast failed as a whole.</exception>
    public Task<BroadcastResult> BroadcastAsync(IEnumerable<string> channels, object data, CancellationToken cancellationToken = default) =>
        BroadcastAsync(new BroadcastRequest { Channels = channels?.ToList() ?? [], Data = data }, cancellationToken);

    internal async Task<TResult> CallAsync<TRequest, TResult>(ApiMethod<TRequest, TResult> method, TRequest request, CancellationToken cancellationToken)
    {
        // A page of an iterator has the page timeout, or RequestTimeout: never a view's (spec 3.8).
        var timeout = Paging.InPage(out var page) ? page ?? _core.RequestTimeout : _timeout;
        var element = await _core.CallAsync(method.Name, method.ProOnly, () => method.Encode(request), timeout, cancellationToken).ConfigureAwait(false);
        return method.Decode(element);
    }

    internal ClientCore Core => _core;
}
