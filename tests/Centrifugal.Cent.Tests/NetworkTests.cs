using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Centrifugal.Cent.Tests;

/// <summary>A plain HTTP/1.1 server on a local port, answering as the test says, counting connections.</summary>
public sealed class TestServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<string, string, NetworkStream, Task> _handle;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;
    private int _connections;
    private int _closed;

    /// <param name="handle">Answers a request: its request line, its body, and the connection's stream.</param>
    public TestServer(Func<string, string, NetworkStream, Task> handle)
    {
        _handle = handle;
        _listener.Start();
        _accept = AcceptAsync();
    }

    public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/api";

    public int Connections => Volatile.Read(ref _connections);

    public int Closed => Volatile.Read(ref _closed);

    public static async Task Respond(NetworkStream s, int status, string body, string extraHeaders = "")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\n{extraHeaders}\r\n"));
        await s.WriteAsync(bytes);
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            Interlocked.Increment(ref _connections);
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var line = await ReadLineAsync(stream);
                    if (line is null)
                    {
                        break;
                    }
                    var length = 0;
                    string? header;
                    while (!string.IsNullOrEmpty(header = await ReadLineAsync(stream)))
                    {
                        if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        {
                            length = int.Parse(header[15..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                        }
                    }
                    var body = new byte[length];
                    await stream.ReadExactlyAsync(body, _stop.Token);
                    await _handle(line, Encoding.UTF8.GetString(body), stream);
                }
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
            finally
            {
                Interlocked.Increment(ref _closed);
            }
        }
    }

    private async Task<string?> ReadLineAsync(NetworkStream s)
    {
        var sb = new StringBuilder();
        var b = new byte[1];
        while (true)
        {
            if (await s.ReadAsync(b, _stop.Token) == 0)
            {
                return sb.Length == 0 ? null : sb.ToString();
            }
            if (b[0] == '\n')
            {
                return sb.ToString().TrimEnd('\r');
            }
            sb.Append((char)b[0]);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accept;
        _stop.Dispose();
    }
}

/// <summary>The default HTTP client against real servers: the transport rules of spec 3.2.</summary>
public sealed class NetworkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task Ok(NetworkStream s, string body = """{"result":{}}""") => TestServer.Respond(s, 200, body);

    [Fact]
    public async Task ConnectionsAreKeptAlive()
    {
        await using var server = new TestServer((_, _, s) => Ok(s));
        await using var client = new CentClient(server.Url, "key");
        for (var i = 0; i < 5; i++)
        {
            await client.InfoAsync(new InfoRequest(), Ct);
        }
        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public async Task ConcurrentCallsUseConnectionsOfTheirOwn()
    {
        var gate = new TaskCompletionSource();
        await using var server = new TestServer(async (_, _, s) =>
        {
            await gate.Task;
            await Ok(s);
        });
        await using var client = new CentClient(server.Url);
        var calls = Enumerable.Range(0, 8).Select(_ => client.InfoAsync(new InfoRequest(), Ct)).ToList();
        while (server.Connections < 8)
        {
            await Task.Delay(5, Ct);
        }
        gate.SetResult();
        await Task.WhenAll(calls);
        Assert.Equal(8, server.Connections);
    }

    [Fact]
    public async Task RedirectsAreNotFollowed()
    {
        var requests = 0;
        await using var server = new TestServer((_, _, s) =>
        {
            Interlocked.Increment(ref requests);
            return TestServer.Respond(s, 307, "", "Location: http://127.0.0.1:1/api/info\r\n");
        });
        await using var client = new CentClient(server.Url, "key");
        var e = await Assert.ThrowsAsync<CentHttpException>(() => client.InfoAsync(new InfoRequest(), Ct));
        Assert.Equal(307, e.StatusCode);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task TheDeadlineBoundsASilentServer()
    {
        await using var server = new TestServer((_, _, _) => Task.Delay(Timeout.Infinite, Ct));
        await using var client = new CentClient(new CentClientOptions { ApiEndpoint = server.Url, RequestTimeout = TimeSpan.FromMilliseconds(300) });
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<CentTimeoutException>(() => client.InfoAsync(new InfoRequest(), Ct));
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task TheDeadlineBoundsTricklingHeaders()
    {
        await using var server = new TestServer(async (_, _, s) =>
        {
            await s.WriteAsync("HTTP/1.1 200 OK\r\n"u8.ToArray());
            for (var i = 0; i < 100; i++)
            {
                await s.WriteAsync(Encoding.ASCII.GetBytes($"X-Slow-{i}: 1\r\n"));
                await Task.Delay(50);
            }
        });
        await using var client = new CentClient(new CentClientOptions { ApiEndpoint = server.Url, RequestTimeout = TimeSpan.FromMilliseconds(400) });
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<CentTimeoutException>(() => client.InfoAsync(new InfoRequest(), Ct));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), watch.Elapsed.ToString());
    }

    [Fact]
    public async Task TheDeadlineBoundsATricklingBody()
    {
        await using var server = new TestServer(async (_, _, s) =>
        {
            await s.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\n"u8.ToArray());
            for (var i = 0; i < 100; i++)
            {
                await s.WriteAsync(" "u8.ToArray());
                await Task.Delay(50);
            }
        });
        await using var client = new CentClient(new CentClientOptions { ApiEndpoint = server.Url, RequestTimeout = TimeSpan.FromMilliseconds(400) });
        var watch = Stopwatch.StartNew();
        var e = await Assert.ThrowsAsync<CentTimeoutException>(() => client.InfoAsync(new InfoRequest(), Ct));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), watch.Elapsed.ToString());
        Assert.True(Retry.IsRetryable(e));
    }

    [Fact]
    public async Task ARefusedConnectionIsARetryableNetworkError()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var client = new CentClient($"http://127.0.0.1:{port}/api");
        var e = await Assert.ThrowsAsync<CentNetworkException>(() => client.InfoAsync(new InfoRequest(), Ct));
        Assert.True(Retry.IsRetryable(e));
    }

    [Fact]
    public async Task ErrorBodiesAreDrainedSoConnectionsAreReused()
    {
        var size = 10_000;
        await using var server = new TestServer((_, _, s) => TestServer.Respond(s, 500, new string('e', size)));
        await using var client = new CentClient(server.Url);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(512, (await Assert.ThrowsAsync<CentHttpException>(() => client.InfoAsync(new InfoRequest(), Ct))).Body.Length);
        }
        Assert.Equal(1, server.Connections);
        // Too much to drain: each connection is dropped, the next call opens another.
        size = 1 << 20;
        await Assert.ThrowsAsync<CentHttpException>(() => client.InfoAsync(new InfoRequest(), Ct));
        await Assert.ThrowsAsync<CentHttpException>(() => client.InfoAsync(new InfoRequest(), Ct));
        Assert.Equal(2, server.Connections);
        var watch = Stopwatch.StartNew();
        while (server.Closed < 2 && watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(10, Ct);
        }
        Assert.Equal(2, server.Closed);
    }

    [Fact]
    public async Task ALargeReplyIsReadWhole()
    {
        var pad = new string('x', 100);
        var pubs = string.Join(',', Enumerable.Range(0, 3000).Select(i => "{\"data\":{\"i\":" + i + ",\"pad\":\"" + pad + "\"},\"offset\":" + i + "}"));
        await using var server = new TestServer((_, _, s) => Ok(s, "{\"result\":{\"publications\":[" + pubs + "]}}"));
        await using var client = new CentClient(server.Url);
        var history = await client.HistoryAsync(new HistoryRequest { Channel = "c" }, Ct);
        Assert.Equal(2999UL, history.Publications[^1].Offset);
    }

    [Fact]
    public async Task CloseClosesTheIdleConnections()
    {
        await using var server = new TestServer((_, _, s) => Ok(s));
        var client = new CentClient(server.Url);
        await client.InfoAsync(new InfoRequest(), Ct);
        Assert.Equal(0, server.Closed);
        await client.CloseAsync(Ct);
        var watch = Stopwatch.StartNew();
        while (server.Closed == 0 && watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(10, Ct);
        }
        Assert.Equal(1, server.Closed);
    }

    [Fact]
    public async Task NotHttpIsANetworkError()
    {
        await using var server = new TestServer(async (_, _, s) => await s.WriteAsync("garbage\r\n\r\n"u8.ToArray()));
        await using var client = new CentClient(server.Url);
        await Assert.ThrowsAsync<CentNetworkException>(() => client.InfoAsync(new InfoRequest(), Ct));
    }
}
