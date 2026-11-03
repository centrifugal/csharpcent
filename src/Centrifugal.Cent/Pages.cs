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
    /// Each page is a call of <see cref="HistoryAsync"/>, with its own deadline: a mock of that
    /// method answers this one too.
    /// </para>
    /// </remarks>
    /// <example>
    /// Reading what came after a publication:
    /// <code>
    /// var published = await client.PublishAsync("news", data, ct);
    /// // ...
    /// await foreach (var publication in client.HistoryAllAsync(new HistoryRequest { Channel = "news", Limit = 100, Since = published.Position }, ct))
    /// {
    ///     Handle(publication);
    /// }
    /// </code>
    /// </example>
    /// <param name="request">The first page's request; it is not changed.</param>
    /// <param name="cancellationToken">Ends the iteration: it then throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>Every publication, in order.</returns>
    /// <exception cref="CentInvalidRequestException">The limit is below 1, or <c>Reverse</c> is set: nothing is sent.</exception>
    public IAsyncEnumerable<Publication> HistoryAllAsync(HistoryRequest request, CancellationToken cancellationToken = default)
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
        return HistoryPages(request.Copy(), cancellationToken);
    }

    private async IAsyncEnumerable<Publication> HistoryPages(HistoryRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await HistoryAsync(request, cancellationToken).ConfigureAwait(false);
            foreach (var publication in result.Publications)
            {
                yield return publication;
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
    /// Every device a request selects, a page at a time, each page read when the iteration
    /// reaches it: <see cref="DeviceListRequest.Limit"/> is the page size, the server's
    /// default when unset. The iteration ends with the exception of a call which failed.
    /// </summary>
    /// <remarks>
    /// A device registered or removed while iterating may or may not be seen. Each page is a
    /// call of <see cref="DeviceListAsync"/>. Centrifugo PRO only.
    /// </remarks>
    /// <param name="request">The first page's request; it is not changed.</param>
    /// <param name="cancellationToken">Ends the iteration: it then throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>Every device, in order.</returns>
    public IAsyncEnumerable<Device> DeviceListAllAsync(DeviceListRequest request, CancellationToken cancellationToken = default) =>
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
            cancellationToken);

    /// <summary>
    /// Every device topic a request selects, a page at a time, as
    /// <see cref="DeviceListAllAsync"/> reads devices.
    /// </summary>
    /// <remarks>
    /// A topic changed while iterating may or may not be seen. Each page is a call of
    /// <see cref="DeviceTopicListAsync"/>. Centrifugo PRO only.
    /// </remarks>
    /// <param name="request">The first page's request; it is not changed.</param>
    /// <param name="cancellationToken">Ends the iteration: it then throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>Every device topic, in order.</returns>
    public IAsyncEnumerable<DeviceTopic> DeviceTopicListAllAsync(DeviceTopicListRequest request, CancellationToken cancellationToken = default) =>
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
            cancellationToken);

    /// <summary>
    /// Every user topic a request selects, a page at a time, as
    /// <see cref="DeviceListAllAsync"/> reads devices.
    /// </summary>
    /// <remarks>
    /// A topic changed while iterating may or may not be seen. Each page is a call of
    /// <see cref="UserTopicListAsync"/>. Centrifugo PRO only.
    /// </remarks>
    /// <param name="request">The first page's request; it is not changed.</param>
    /// <param name="cancellationToken">Ends the iteration: it then throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>Every user topic, in order.</returns>
    public IAsyncEnumerable<UserTopic> UserTopicListAllAsync(UserTopicListRequest request, CancellationToken cancellationToken = default) =>
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
            cancellationToken);

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
    /// may be seen in either version; for a consistent view, read the changes since the
    /// first page's position with <see cref="MapReadStreamAsync"/>. Each page is a call of
    /// <see cref="MapReadStateAsync"/>.
    /// </remarks>
    /// <param name="request">The first page's request; it is not changed.</param>
    /// <param name="cancellationToken">Ends the iteration: it then throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>Every entry, in order.</returns>
    public IAsyncEnumerable<MapEntry> MapReadStateAllAsync(MapReadStateRequest request, CancellationToken cancellationToken = default) =>
        MapStatePages(request?.Copy() ?? throw new CentInvalidRequestException("map_read_state", "no request"), cancellationToken);

    private async IAsyncEnumerable<MapEntry> MapStatePages(MapReadStateRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pinned = request.RevisionOffset > 0 || !string.IsNullOrEmpty(request.RevisionEpoch);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await MapReadStateAsync(request, cancellationToken).ConfigureAwait(false);
            foreach (var entry in result.Entries)
            {
                yield return entry;
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
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (items, next) = page(await call(request, cancellationToken).ConfigureAwait(false));
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
