using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Centrifugal.Cent;

// Stream positions and pages (spec 3.8).

public sealed partial class PublishResult
{
    /// <summary>
    /// Where the publication went in the channel's stream: the <see cref="HistoryRequest.Since"/>
    /// of a history call reading what came after it.
    /// </summary>
    [JsonIgnore]
    public StreamPosition Position => new() { Offset = Offset, Epoch = Epoch };
}

public sealed partial class HistoryResult
{
    /// <summary>The channel's stream position when the history was read.</summary>
    [JsonIgnore]
    public StreamPosition Position => new() { Offset = Offset, Epoch = Epoch };
}

public sealed partial class MapReadStateResult
{
    /// <summary>
    /// The map channel's stream position when its state was read: with
    /// <see cref="MapReadStreamRequest"/>'s <c>SinceOffset</c> and <c>SinceEpoch</c>, it reads
    /// the changes since.
    /// </summary>
    [JsonIgnore]
    public StreamPosition Position => new() { Offset = Offset, Epoch = Epoch };
}

public sealed partial class MapReadStreamResult
{
    /// <summary>The map channel's stream position when its stream was read.</summary>
    [JsonIgnore]
    public StreamPosition Position => new() { Offset = Offset, Epoch = Epoch };
}

public sealed partial class MapPublishResult
{
    /// <summary>
    /// The map channel's stream position after the publish: with
    /// <see cref="MapReadStreamRequest"/>'s <c>SinceOffset</c> and <c>SinceEpoch</c>, it reads
    /// the changes after it.
    /// </summary>
    [JsonIgnore]
    public StreamPosition Position => new() { Offset = Offset, Epoch = Epoch };
}

public sealed partial class MapRemoveResult
{
    /// <summary>
    /// The map channel's stream position after the removal: with
    /// <see cref="MapReadStreamRequest"/>'s <c>SinceOffset</c> and <c>SinceEpoch</c>, it reads
    /// the changes after it.
    /// </summary>
    [JsonIgnore]
    public StreamPosition Position => new() { Offset = Offset, Epoch = Epoch };
}

/// <summary>
/// A publication read by <see cref="CentClient.HistoryAllAsync(HistoryRequest, CancellationToken)"/>, with the epoch of the
/// stream it was read from: its <see cref="Position"/> is where to resume reading, or the
/// <c>RecoverSince</c> of a subscription, with no fields copied by hand.
/// </summary>
/// <param name="Publication">The publication.</param>
/// <param name="Epoch">The epoch of the channel's stream the publication is in.</param>
public sealed record HistoryItem(Publication Publication, string Epoch)
{
    /// <summary>Where the publication is in the channel's stream.</summary>
    public StreamPosition Position => new() { Offset = Publication.Offset, Epoch = Epoch };
}

/// <summary>
/// An entry read by <see cref="CentClient.MapReadStateAllAsync(MapReadStateRequest, CancellationToken)"/>, with the position of the
/// state it was read at: following the state with <see cref="CentClient.MapReadStreamAsync"/>
/// from <see cref="State"/> gets every change made since.
/// </summary>
/// <param name="Entry">The entry.</param>
/// <param name="State">
/// The position of the map's state the entry was read at: the first page's, every later page
/// being read at it - or the revision the request set.
/// </param>
public sealed record MapStateItem(MapEntry Entry, StreamPosition State);

public sealed partial class HistoryRequest
{
    internal HistoryRequest Copy() => (HistoryRequest)MemberwiseClone();
}

public sealed partial class DeviceListRequest
{
    internal DeviceListRequest Copy() => (DeviceListRequest)MemberwiseClone();
}

public sealed partial class DeviceTopicListRequest
{
    internal DeviceTopicListRequest Copy() => (DeviceTopicListRequest)MemberwiseClone();
}

public sealed partial class UserTopicListRequest
{
    internal UserTopicListRequest Copy() => (UserTopicListRequest)MemberwiseClone();
}

public sealed partial class MapReadStateRequest
{
    internal MapReadStateRequest Copy() => (MapReadStateRequest)MemberwiseClone();
}

public partial class CentClient
{
    /// <summary>
    /// Every publication of a channel's history, oldest first, with its position,
    /// each page bounded by <see cref="CentClientOptions.RequestTimeout"/>: see
    /// <see cref="HistoryAllAsync(HistoryRequest, TimeSpan, CancellationToken)"/>.
    /// </summary>
    /// <param name="request">The first page's request; it is not changed.</param>
    /// <param name="cancellationToken">Ends the iteration: it then throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>Every publication, in order, with its position.</returns>
    public IAsyncEnumerable<HistoryItem> HistoryAllAsync(HistoryRequest request, CancellationToken cancellationToken = default) =>
        HistoryAll(request, null, cancellationToken);

    /// <summary>
    /// The publications of a channel's history, oldest first, a page of
    /// <see cref="HistoryRequest.Limit"/> at a time: from <see cref="HistoryRequest.Since"/>
    /// when it is set, from the start of what the channel keeps otherwise. A page is read when
    /// the iteration reaches it, and the iteration ends after a page shorter than the limit,
    /// or with the exception of a call which failed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The limit is the page size, so it must be 1 or more, and <see cref="HistoryRequest.Reverse"/>
    /// is refused: <c>Since</c> reads forward only. A publication made while iterating may or
    /// may not be seen. An epoch which changed since a page was read - the history was lost,
    /// with a Redis restart say - fails with code 112,
    /// <see cref="ErrorCodes.UnrecoverablePosition"/>: the publications after the last one
    /// seen are gone, and reading again from the start is the only way on.
    /// </para>
    /// <para>
    /// Each page is a call of <see cref="HistoryAsync"/>, bounded by the page timeout: a mock
    /// of that method answers this one too.
    /// </para>
    /// </remarks>
    /// <example>
    /// Reading what came after a publication:
    /// <code>
    /// var published = await client.PublishAsync("news", data, ct);
    /// // ...
    /// await foreach (var item in client.HistoryAllAsync(new HistoryRequest { Channel = "news", Limit = 100, Since = published.Position }, ct))
    /// {
    ///     Handle(item.Publication.Data);
    ///     last = item.Position; // to resume from later
    /// }
    /// </code>
    /// </example>
    /// <param name="request">The first page's request; it is not changed.</param>
    /// <param name="pageTimeout">
    /// Each page's own timeout, replacing <see cref="CentClientOptions.RequestTimeout"/> for its
    /// request, longer or shorter: positive, at most 24 days. Without it each page gets
    /// <c>RequestTimeout</c>; the timeout of a client from <see cref="WithTimeout"/> never bounds
    /// the pages - an iteration lasts as long as its caller's loop.
    /// </param>
    /// <param name="cancellationToken">Ends the iteration: it then throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>Every publication, in order, with its position.</returns>
    /// <exception cref="CentInvalidRequestException">The limit is below 1, <c>Reverse</c> is set, or the page timeout is not positive: nothing is sent.</exception>
    public IAsyncEnumerable<HistoryItem> HistoryAllAsync(HistoryRequest request, TimeSpan pageTimeout, CancellationToken cancellationToken = default) =>
        HistoryAll(request, Paging.Timeout("history", pageTimeout), cancellationToken);

    private IAsyncEnumerable<HistoryItem> HistoryAll(HistoryRequest request, TimeSpan? pageTimeout, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new CentInvalidRequestException("history", "no request");
        }
        if (request.Limit < 1)
        {
            throw new CentInvalidRequestException("history", "HistoryAllAsync needs a Limit of 1 or more: the page size");
        }
        if (request.Reverse)
        {
            throw new CentInvalidRequestException("history", "HistoryAllAsync reads forward only: Reverse is not supported");
        }
        return HistoryPages(request.Copy(), pageTimeout, cancellationToken);
    }

    private async IAsyncEnumerable<HistoryItem> HistoryPages(HistoryRequest request, TimeSpan? pageTimeout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await Paging.Page(pageTimeout, () => HistoryAsync(request, cancellationToken)).ConfigureAwait(false);
            foreach (var publication in result.Publications)
            {
                yield return new HistoryItem(publication, result.Epoch);
            }
            var n = result.Publications.Count;
            if (n < request.Limit)
            {
                yield break;
            }
            request = request.Copy();
            request.Since = new StreamPosition { Offset = result.Publications[n - 1].Offset, Epoch = result.Epoch };
        }
    }

    /// <summary>
    /// Every device a request selects,
    /// each page bounded by <see cref="CentClientOptions.RequestTimeout"/>: see
    /// <see cref="DeviceListAllAsync(DeviceListRequest, TimeSpan, CancellationToken)"/>.
    /// </summary>
    /// <param name="request">The first page's request; it is not changed.</param>
    /// <param name="cancellationToken">Ends the iteration: it then throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>Every device, in order.</returns>
    public IAsyncEnumerable<Device> DeviceListAllAsync(DeviceListRequest request, CancellationToken cancellationToken = default) =>
        DeviceListAll(request, null, cancellationToken);

    /// <summary>
    /// Every device a request selects, a page at a time, each page read when the iteration
    /// reaches it: <see cref="DeviceListRequest.Limit"/> is the page size, the server's
    /// default when unset. The iteration ends with the exception of a call which failed.
    /// </summary>
    /// <remarks>
    /// A device registered or removed while iterating may or may not be seen. Each page is a
    /// call of <see cref="DeviceListAsync"/>. Centrifugo PRO only.
    /// </remarks>
    /// <param name="request">The first page's request; it is not changed.</param>
    /// <param name="pageTimeout">
    /// Each page's own timeout, replacing <see cref="CentClientOptions.RequestTimeout"/> for its
    /// request, longer or shorter: positive, at most 24 days. Without it each page gets
    /// <c>RequestTimeout</c>; the timeout of a client from <see cref="WithTimeout"/> never bounds
    /// the pages - an iteration lasts as long as its caller's loop.
    /// </param>
    /// <param name="cancellationToken">Ends the iteration: it then throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>Every device, in order.</returns>
    /// <exception cref="CentInvalidRequestException">The page timeout is not positive, or longer than 24 days: nothing is sent.</exception>
    public IAsyncEnumerable<Device> DeviceListAllAsync(DeviceListRequest request, TimeSpan pageTimeout, CancellationToken cancellationToken = default) =>
        DeviceListAll(request, Paging.Timeout("device_list", pageTimeout), cancellationToken);

    private IAsyncEnumerable<Device> DeviceListAll(DeviceListRequest request, TimeSpan? pageTimeout, CancellationToken cancellationToken) =>
        ByCursor(
            request?.Copy() ?? throw new CentInvalidRequestException("device_list", "no request"),
            DeviceListAsync,
            r => (r.Items, r.NextCursor),
            (r, cursor) =>
            {
                var next = r.Copy();
                next.Cursor = cursor;
                return next;
            },
            pageTimeout,
            cancellationToken);

    /// <summary>
    /// Every device topic a request selects,
    /// each page bounded by <see cref="CentClientOptions.RequestTimeout"/>: see
    /// <see cref="DeviceTopicListAllAsync(DeviceTopicListRequest, TimeSpan, CancellationToken)"/>.
    /// </summary>
    /// <param name="request">The first page's request; it is not changed.</param>
    /// <param name="cancellationToken">Ends the iteration: it then throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>Every device topic, in order.</returns>
    public IAsyncEnumerable<DeviceTopic> DeviceTopicListAllAsync(DeviceTopicListRequest request, CancellationToken cancellationToken = default) =>
        DeviceTopicListAll(request, null, cancellationToken);

    /// <summary>
    /// Every device topic a request selects, a page at a time, as
    /// <see cref="DeviceListAllAsync(DeviceListRequest, TimeSpan, CancellationToken)"/> reads devices.
    /// </summary>
    /// <remarks>
    /// A topic changed while iterating may or may not be seen. Each page is a call of
    /// <see cref="DeviceTopicListAsync"/>. Centrifugo PRO only.
    /// </remarks>
    /// <param name="request">The first page's request; it is not changed.</param>
    /// <param name="pageTimeout">
    /// Each page's own timeout, replacing <see cref="CentClientOptions.RequestTimeout"/> for its
    /// request, longer or shorter: positive, at most 24 days. Without it each page gets
    /// <c>RequestTimeout</c>; the timeout of a client from <see cref="WithTimeout"/> never bounds
    /// the pages - an iteration lasts as long as its caller's loop.
    /// </param>
    /// <param name="cancellationToken">Ends the iteration: it then throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>Every device topic, in order.</returns>
    /// <exception cref="CentInvalidRequestException">The page timeout is not positive, or longer than 24 days: nothing is sent.</exception>
    public IAsyncEnumerable<DeviceTopic> DeviceTopicListAllAsync(DeviceTopicListRequest request, TimeSpan pageTimeout, CancellationToken cancellationToken = default) =>
        DeviceTopicListAll(request, Paging.Timeout("device_topic_list", pageTimeout), cancellationToken);

    private IAsyncEnumerable<DeviceTopic> DeviceTopicListAll(DeviceTopicListRequest request, TimeSpan? pageTimeout, CancellationToken cancellationToken) =>
        ByCursor(
            request?.Copy() ?? throw new CentInvalidRequestException("device_topic_list", "no request"),
            DeviceTopicListAsync,
            r => (r.Items, r.NextCursor),
            (r, cursor) =>
            {
                var next = r.Copy();
                next.Cursor = cursor;
                return next;
            },
            pageTimeout,
            cancellationToken);

    /// <summary>
    /// Every user topic a request selects,
    /// each page bounded by <see cref="CentClientOptions.RequestTimeout"/>: see
    /// <see cref="UserTopicListAllAsync(UserTopicListRequest, TimeSpan, CancellationToken)"/>.
    /// </summary>
    /// <param name="request">The first page's request; it is not changed.</param>
    /// <param name="cancellationToken">Ends the iteration: it then throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>Every user topic, in order.</returns>
    public IAsyncEnumerable<UserTopic> UserTopicListAllAsync(UserTopicListRequest request, CancellationToken cancellationToken = default) =>
        UserTopicListAll(request, null, cancellationToken);

    /// <summary>
    /// Every user topic a request selects, a page at a time, as
    /// <see cref="DeviceListAllAsync(DeviceListRequest, TimeSpan, CancellationToken)"/> reads devices.
    /// </summary>
    /// <remarks>
    /// A topic changed while iterating may or may not be seen. Each page is a call of
    /// <see cref="UserTopicListAsync"/>. Centrifugo PRO only.
    /// </remarks>
    /// <param name="request">The first page's request; it is not changed.</param>
    /// <param name="pageTimeout">
    /// Each page's own timeout, replacing <see cref="CentClientOptions.RequestTimeout"/> for its
    /// request, longer or shorter: positive, at most 24 days. Without it each page gets
    /// <c>RequestTimeout</c>; the timeout of a client from <see cref="WithTimeout"/> never bounds
    /// the pages - an iteration lasts as long as its caller's loop.
    /// </param>
    /// <param name="cancellationToken">Ends the iteration: it then throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>Every user topic, in order.</returns>
    /// <exception cref="CentInvalidRequestException">The page timeout is not positive, or longer than 24 days: nothing is sent.</exception>
    public IAsyncEnumerable<UserTopic> UserTopicListAllAsync(UserTopicListRequest request, TimeSpan pageTimeout, CancellationToken cancellationToken = default) =>
        UserTopicListAll(request, Paging.Timeout("user_topic_list", pageTimeout), cancellationToken);

    private IAsyncEnumerable<UserTopic> UserTopicListAll(UserTopicListRequest request, TimeSpan? pageTimeout, CancellationToken cancellationToken) =>
        ByCursor(
            request?.Copy() ?? throw new CentInvalidRequestException("user_topic_list", "no request"),
            UserTopicListAsync,
            r => (r.Items, r.NextCursor),
            (r, cursor) =>
            {
                var next = r.Copy();
                next.Cursor = cursor;
                return next;
            },
            pageTimeout,
            cancellationToken);

    /// <summary>
    /// Every entry of a map channel's state, with the position of the state it was read at,
    /// each page bounded by <see cref="CentClientOptions.RequestTimeout"/>: see
    /// <see cref="MapReadStateAllAsync(MapReadStateRequest, TimeSpan, CancellationToken)"/>.
    /// </summary>
    /// <param name="request">The first page's request; it is not changed.</param>
    /// <param name="cancellationToken">Ends the iteration: it then throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>Every entry, in order, with the position of the state it was read at.</returns>
    public IAsyncEnumerable<MapStateItem> MapReadStateAllAsync(MapReadStateRequest request, CancellationToken cancellationToken = default) =>
        MapReadStateAll(request, null, cancellationToken);

    /// <summary>
    /// Every entry of a map channel's state, a page at a time, each page read when the
    /// iteration reaches it: <see cref="MapReadStateRequest.Limit"/> is the page size. The
    /// iteration ends with the exception of a call which failed.
    /// </summary>
    /// <remarks>
    /// Every page after the first is read at the first one's revision - its
    /// <see cref="MapReadStateResult.Position"/>, as <c>RevisionOffset</c> and
    /// <c>RevisionEpoch</c>, unless the request sets them - so that a state reset between
    /// pages fails with code 112, <see cref="ErrorCodes.UnrecoverablePosition"/>, instead of
    /// mixing two states: read again from the start then. Entries changed while iterating
    /// may be seen in either version; for a consistent view, read the changes since with
    /// <see cref="MapReadStreamAsync"/>, from the <see cref="MapStateItem.State"/> every item
    /// carries. Each page is a call of
    /// <see cref="MapReadStateAsync"/>.
    /// </remarks>
    /// <param name="request">The first page's request; it is not changed.</param>
    /// <param name="pageTimeout">
    /// Each page's own timeout, replacing <see cref="CentClientOptions.RequestTimeout"/> for its
    /// request, longer or shorter: positive, at most 24 days. Without it each page gets
    /// <c>RequestTimeout</c>; the timeout of a client from <see cref="WithTimeout"/> never bounds
    /// the pages - an iteration lasts as long as its caller's loop.
    /// </param>
    /// <param name="cancellationToken">Ends the iteration: it then throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>Every entry, in order, with the position of the state it was read at.</returns>
    /// <exception cref="CentInvalidRequestException">The page timeout is not positive, or longer than 24 days: nothing is sent.</exception>
    public IAsyncEnumerable<MapStateItem> MapReadStateAllAsync(MapReadStateRequest request, TimeSpan pageTimeout, CancellationToken cancellationToken = default) =>
        MapReadStateAll(request, Paging.Timeout("map_read_state", pageTimeout), cancellationToken);

    private IAsyncEnumerable<MapStateItem> MapReadStateAll(MapReadStateRequest request, TimeSpan? pageTimeout, CancellationToken cancellationToken) =>
        MapStatePages(request?.Copy() ?? throw new CentInvalidRequestException("map_read_state", "no request"), pageTimeout, cancellationToken);

    private async IAsyncEnumerable<MapStateItem> MapStatePages(MapReadStateRequest request, TimeSpan? pageTimeout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pinned = request.RevisionOffset > 0 || !string.IsNullOrEmpty(request.RevisionEpoch);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await Paging.Page(pageTimeout, () => MapReadStateAsync(request, cancellationToken)).ConfigureAwait(false);
            var (offset, epoch) = pinned ? (request.RevisionOffset, request.RevisionEpoch ?? "") : (result.Offset, result.Epoch);
            foreach (var entry in result.Entries)
            {
                yield return new MapStateItem(entry, new StreamPosition { Offset = offset, Epoch = epoch });
            }
            if (string.IsNullOrEmpty(result.Cursor))
            {
                yield break;
            }
            request = request.Copy();
            request.Cursor = result.Cursor;
            if (!pinned)
            {
                request.RevisionOffset = result.Offset;
                request.RevisionEpoch = result.Epoch;
                pinned = true;
            }
        }
    }

    /// <summary>The items of a method paged by a cursor: the request, then the request with each reply's cursor, until a reply has none.</summary>
    private static async IAsyncEnumerable<TItem> ByCursor<TRequest, TResult, TItem>(
        TRequest request,
        Func<TRequest, CancellationToken, Task<TResult>> call,
        Func<TResult, (IReadOnlyList<TItem> Items, string Next)> page,
        Func<TRequest, string, TRequest> withCursor,
        TimeSpan? pageTimeout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (items, next) = page(await Paging.Page(pageTimeout, () => call(request, cancellationToken)).ConfigureAwait(false));
            foreach (var item in items)
            {
                yield return item;
            }
            if (string.IsNullOrEmpty(next))
            {
                yield break;
            }
            request = withCursor(request, next);
        }
    }
}

/// <summary>
/// The timeout of the iterators' pages (spec 3.8): each page's request is bounded by the
/// iterator's page timeout, or <c>RequestTimeout</c> - never by the timeout of a client from
/// <c>WithTimeout</c>, which bounds whole calls. A page's call reads it as it starts, through
/// the page's execution context, so that it goes through the virtual method a mock answers.
/// </summary>
internal static class Paging
{
    private static readonly AsyncLocal<Scope?> Current = new();

    /// <summary>The timeout of the page being read, when a call is one: its own, or null for <c>RequestTimeout</c>.</summary>
    public static bool InPage(out TimeSpan? timeout)
    {
        timeout = Current.Value?.Timeout;
        return Current.Value is not null;
    }

    /// <summary>Reads a page: the call made inside is bounded by <paramref name="timeout"/>, or <c>RequestTimeout</c>.</summary>
    public static async Task<T> Page<T>(TimeSpan? timeout, Func<Task<T>> call)
    {
        // Set in this method's own execution context: restored when it returns.
        Current.Value = new Scope(timeout);
        return await call().ConfigureAwait(false);
    }

    /// <summary>Checks an iterator's page timeout before anything is sent.</summary>
    public static TimeSpan Timeout(string method, TimeSpan timeout) =>
        Deadline.IsValid(timeout)
            ? timeout
            : throw new CentInvalidRequestException(method, $"PageTimeout is {timeout}: give a positive TimeSpan of at most 24 days, or none for RequestTimeout");

    private sealed record Scope(TimeSpan? Timeout);
}
