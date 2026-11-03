using System.Text.Json;
using System.Text.Json.Nodes;

namespace Centrifugal.Cent.Tests;

/// <summary>Pages and stream positions (spec 3.8).</summary>
public sealed class PagesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const int PageSize = 3;

    /// <summary>
    /// A fake keeping <paramref name="count"/> items, 1 to count: by cursor - the index of the
    /// next item - or for history by the offset of the last one seen. A request at
    /// <paramref name="failAt"/> fails.
    /// </summary>
    private static FakeCentrifugo Fake(int count, int failAt = -1)
    {
        var calls = 0;
        return new FakeCentrifugo(r =>
        {
            if (Interlocked.Increment(ref calls) - 1 == failAt)
            {
                return Answer.Ok("""{"error":{"code":100,"message":"internal server error"}}""");
            }
            var body = r.Json;
            var limit = body.TryGetProperty("limit", out var l) ? l.GetInt32() : PageSize;
            if (r.ApiMethod == "history")
            {
                var since = body.TryGetProperty("since", out var s) && s.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;
                var pubs = new JsonArray();
                for (var i = since + 1; i <= count && pubs.Count < limit; i++)
                {
                    pubs.Add(new JsonObject { ["offset"] = i, ["data"] = i });
                }
                return Answer.Ok(new JsonObject { ["result"] = new JsonObject { ["publications"] = pubs, ["offset"] = count, ["epoch"] = "e" } }.ToJsonString());
            }
            var start = body.TryGetProperty("cursor", out var c) ? int.Parse(c.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : 0;
            var items = new JsonArray();
            for (var i = start; i < count && items.Count < limit; i++)
            {
                items.Add(r.ApiMethod == "map_read_state" ? new JsonObject { ["key"] = $"{i + 1}" } : new JsonObject { ["id"] = $"{i + 1}" });
            }
            var next = start + items.Count < count ? $"{start + items.Count}" : "";
            var result = r.ApiMethod == "map_read_state"
                ? new JsonObject { ["entries"] = items, ["cursor"] = next, ["offset"] = 7, ["epoch"] = "e" }
                : new JsonObject { ["items"] = items, ["next_cursor"] = next };
            return Answer.Ok(new JsonObject { ["result"] = result }.ToJsonString());
        });
    }

    public static TheoryData<string, Func<CentClient, IAsyncEnumerable<string>>> Methods => new()
    {
        { "history", c => c.HistoryAllAsync(new HistoryRequest { Channel = "c", Limit = PageSize }, Ct).Select(p => p.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture)) },
        { "device_list", c => c.DeviceListAllAsync(new DeviceListRequest { Limit = PageSize }, Ct).Select(d => d.Id) },
        { "device_topic_list", c => c.DeviceTopicListAllAsync(new DeviceTopicListRequest { Limit = PageSize }, Ct).Select(d => d.Id) },
        { "user_topic_list", c => c.UserTopicListAllAsync(new UserTopicListRequest { Limit = PageSize }, Ct).Select(d => d.Id) },
        { "map_read_state", c => c.MapReadStateAllAsync(new MapReadStateRequest { Channel = "c", Limit = PageSize }, Ct).Select(e => e.Key) },
    };

    private static async Task<List<string>> All(IAsyncEnumerable<string> items)
    {
        var list = new List<string>();
        await foreach (var item in items)
        {
            list.Add(item);
        }
        return list;
    }

    // The request after each page asks from where the page before ended: the cursor of its
    // reply, or for history the last publication's offset and the epoch.
    private static string? From(Recorded r) => r.ApiMethod == "history"
        ? r.Json.TryGetProperty("since", out var s) ? $"{s.GetProperty("offset").GetInt32()}/{s.GetProperty("epoch").GetString()}" : null
        : r.Json.TryGetProperty("cursor", out var c) ? c.GetString() : null;

    [Theory]
    [MemberData(nameof(Methods))]
    public async Task EveryItemOnceAndInOrder(string method, Func<CentClient, IAsyncEnumerable<string>> all)
    {
        foreach (var count in new[] { 0, 1, PageSize, 2 * PageSize + 1 })
        {
            var fake = Fake(count);
            await using var client = fake.CreateClient();
            var items = await All(all(client));
            Assert.Equal(Enumerable.Range(1, count).Select(i => $"{i}"), items);
            Assert.All(fake.Requests, r => Assert.Equal(method, r.ApiMethod));
            // History reads a page more after a full one; a cursor says there is none.
            var pages = method == "history" ? count / PageSize + 1 : Math.Max(1, (count + PageSize - 1) / PageSize);
            Assert.Equal(pages, fake.Requests.Count);
            var expected = Enumerable.Range(0, pages).Select(p => p == 0 ? null : method == "history" ? $"{p * PageSize}/e" : $"{p * PageSize}");
            Assert.Equal(expected, fake.Requests.Select(From));
        }
    }

    [Theory]
    [MemberData(nameof(Methods))]
    public async Task LeavingTheLoopReadsNoFurtherPage(string method, Func<CentClient, IAsyncEnumerable<string>> all)
    {
        var fake = Fake(10);
        await using var client = fake.CreateClient();
        await foreach (var item in all(client))
        {
            if (item == $"{PageSize}")
            {
                break;
            }
        }
        Assert.Equal(method, Assert.Single(fake.Requests).ApiMethod);
    }

    [Theory]
    [MemberData(nameof(Methods))]
    public async Task AnErrorEndsTheIterationAfterTheItemsBeforeIt(string method, Func<CentClient, IAsyncEnumerable<string>> all)
    {
        var fake = Fake(10, failAt: 1);
        await using var client = fake.CreateClient();
        var seen = new List<string>();
        var e = await Assert.ThrowsAsync<CentApiException>(async () =>
        {
            await foreach (var item in all(client))
            {
                seen.Add(item);
            }
        });
        Assert.Equal(ErrorCodes.Internal, e.Code);
        Assert.Equal(["1", "2", "3"], seen);
        Assert.Equal(2, fake.Requests.Count);
        Assert.All(fake.Requests, r => Assert.Equal(method, r.ApiMethod));
    }

    [Fact]
    public async Task APageIsReadWhenReached()
    {
        var fake = Fake(10);
        await using var client = fake.CreateClient();
        var items = client.DeviceListAllAsync(new DeviceListRequest { Limit = PageSize }, Ct);
        Assert.Empty(fake.Requests);
        await using var e = items.GetAsyncEnumerator(Ct);
        for (var i = 0; i < PageSize; i++)
        {
            Assert.True(await e.MoveNextAsync());
        }
        Assert.Single(fake.Requests);
        Assert.True(await e.MoveNextAsync());
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task CancellationEndsTheIteration()
    {
        var fake = Fake(10);
        await using var client = fake.CreateClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            // The token given to WithCancellation, as await foreach passes it.
#pragma warning disable xUnit1051
            await foreach (var _ in client.DeviceListAllAsync(new DeviceListRequest { Limit = PageSize }).WithCancellation(cts.Token))
#pragma warning restore xUnit1051
            {
                await cts.CancelAsync();
            }
        });
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task TheRequestIsNotChanged()
    {
        var fake = Fake(10);
        await using var client = fake.CreateClient();
        var request = new DeviceListRequest { Limit = PageSize };
        var history = new HistoryRequest { Channel = "c", Limit = PageSize };
        await All(client.DeviceListAllAsync(request, Ct).Select(d => d.Id));
        await All(client.HistoryAllAsync(history, Ct).Select(p => p.Id()));
        Assert.Null(request.Cursor);
        Assert.Null(history.Since);
    }

    [Theory]
    [InlineData(0, false, "Limit of 1 or more")]
    [InlineData(-1, false, "Limit of 1 or more")]
    [InlineData(10, true, "Reverse is not supported")]
    public async Task HistoryAllRefusesBeforeSending(int limit, bool reverse, string message)
    {
        var fake = Fake(10);
        await using var client = fake.CreateClient();
        var e = Assert.Throws<CentInvalidRequestException>(() => client.HistoryAllAsync(new HistoryRequest { Channel = "c", Limit = limit, Reverse = reverse }, Ct));
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
        await Task.Yield();
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public void PositionsAreTheResultsOffsetAndEpoch()
    {
        static void Check(StreamPosition p)
        {
            Assert.Equal(7UL, p.Offset);
            Assert.Equal("e", p.Epoch);
        }
        Check(new PublishResult { Offset = 7, Epoch = "e" }.Position);
        Check(new HistoryResult { Offset = 7, Epoch = "e" }.Position);
        Check(new MapReadStateResult { Offset = 7, Epoch = "e" }.Position);
        Check(new MapReadStreamResult { Offset = 7, Epoch = "e" }.Position);
        // A result encoded as JSON gives its wire fields only.
        Assert.Equal("""{"offset":7,"epoch":"e"}""", JsonSerializer.Serialize(new PublishResult { Offset = 7, Epoch = "e" }, Wire.Info<PublishResult>()));
    }

    [Fact]
    public async Task APublicationsPositionIsWhereHistoryReadsOnFrom()
    {
        var fake = Fake(5);
        await using var client = fake.CreateClient();
        var published = new PublishResult { Offset = 2, Epoch = "e" };
        var after = await All(client.HistoryAllAsync(new HistoryRequest { Channel = "c", Limit = PageSize, Since = published.Position }, Ct).Select(p => p.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(["3", "4", "5"], after);
        Assert.Equal("""{"offset":2,"epoch":"e"}""", fake.Requests[0].Json.GetProperty("since").GetRawText());
    }
}

internal static class PageExtensions
{
    public static string Id(this Publication p) => p.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static async IAsyncEnumerable<TOut> Select<TIn, TOut>(this IAsyncEnumerable<TIn> items, Func<TIn, TOut> f)
    {
        await foreach (var item in items)
        {
            yield return f(item);
        }
    }
}
