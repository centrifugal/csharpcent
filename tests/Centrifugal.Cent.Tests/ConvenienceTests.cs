using System.Net;
using Centrifugal.Cent.Proxy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Centrifugal.Cent.Tests;

/// <summary>What .NET applications do with the SDK: mock it, configure it, log through it.</summary>
public sealed class ConvenienceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Application code under test.</summary>
    private sealed class Notifier(CentClient client)
    {
        public async Task<ulong> NotifyAsync(string user, CancellationToken ct) =>
            (await client.PublishAsync($"user:{user}", new { text = "ping" }, ct)).Offset;

        public async Task<IReadOnlyList<string>> NotifyAllAsync(IEnumerable<string> users, CancellationToken ct)
        {
            try
            {
                await client.BroadcastAsync(new BroadcastRequest { Channels = users.Select(u => $"user:{u}").ToList(), Data = 1 }, ct);
                return [];
            }
            catch (BroadcastFailedChannelsException e)
            {
                return e.Failed.Select(c => c.Channel).ToList();
            }
        }
    }

    [Fact]
    public async Task TheClientIsMockedWithNSubstitute()
    {
        var client = Substitute.For<CentClient>();
        client.PublishAsync(Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PublishResult { Offset = 7, Epoch = "e" });
        client.BroadcastAsync(Arg.Any<BroadcastRequest>(), Arg.Any<CancellationToken>())
            .Returns<BroadcastResult>(_ => throw new BroadcastFailedChannelsException(new BroadcastResult(
                [new ChannelResult("user:a", new PublishResult()), new ChannelResult("user:b", new CentApiException(102, "unknown channel"))])));

        var notifier = new Notifier(client);
        // The shorthand calls the request form: mocking that one answers both.
        Assert.Equal(7UL, await notifier.NotifyAsync("42", Ct));
        Assert.Equal(["user:b"], await notifier.NotifyAllAsync(["a", "b"], Ct));
        await client.Received(1).PublishAsync(Arg.Is<PublishRequest>(r => r.Channel == "user:42"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheClientIsMockedWithAClassOfItsOwn()
    {
        var notifier = new Notifier(new RecordingClient());
        Assert.Equal(1UL, await notifier.NotifyAsync("42", Ct));
    }

    private sealed class RecordingClient : CentClient
    {
        public override Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PublishResult { Offset = 1 });
    }

    [Fact]
    public async Task ShorthandsSendWhatTheRequestFormsDo()
    {
        var fake = new FakeCentrifugo();
        await using var client = fake.CreateClient();
        await client.PublishAsync("news", new { text = "hi" }, Ct);
        await client.BroadcastAsync(["a", "b"], RawJson.Parse("1.0"), Ct);
        Assert.Equal("""{"channel":"news","data":{"text":"hi"}}""", fake.Requests[0].Body);
        Assert.Equal("""{"channels":["a","b"],"data":1.0}""", fake.Requests[1].Body);
        await Assert.ThrowsAsync<CentInvalidRequestException>(() => client.PublishAsync("news", null!, Ct));
    }

    [Fact]
    public async Task TheClientIsConfiguredFromConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Centrifugo:ApiEndpoint"] = "http://centrifugo.test/api",
            ["Centrifugo:ApiKey"] = "from-config",
            ["Centrifugo:RequestTimeout"] = "00:00:00.200",
            ["Centrifugo:Headers:X-Tenant"] = "acme",
            ["Centrifugo:AutoBatch:MaxInFlight"] = "4",
        }).Build();
        var fake = new FakeCentrifugo(r => r.ApiMethod == "history"
            ? Answer.After(TimeSpan.FromSeconds(5), Answer.Ok("{}"))
            : Answer.Ok(FakeCentrifugo.DefaultReply(r)));
        var services = new ServiceCollection();
        services.AddCentClient(configuration.GetSection("Centrifugo"), (_, o) => o.HttpClient = new HttpClient(fake));
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<CentClient>();
        await client.InfoAsync(new InfoRequest(), Ct);
        var r = fake.Requests[0];
        Assert.Equal("http://centrifugo.test/api/info", r.Uri.ToString());
        Assert.Equal("from-config", r.Headers["X-API-Key"]);
        Assert.Equal("acme", r.Headers["X-Tenant"]);
        Assert.Equal(TimeSpan.FromMilliseconds(200), client.Timeout);
        await Assert.ThrowsAsync<CentTimeoutException>(() => client.HistoryAsync(new HistoryRequest { Channel = "c" }, Ct));
    }

    [Fact]
    public void AnInvalidConfigurationFailsWhenTheClientIsResolved()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Centrifugo:ApiEndpoint"] = "http://localhost:8000/api/publish",
        }).Build();
        var services = new ServiceCollection();
        services.AddCentClient(configuration.GetSection("Centrifugo"));
        using var provider = services.BuildServiceProvider();
        Assert.Contains("ends in the method publish", Assert.Throws<CentInvalidConfigException>(() => provider.GetRequiredService<CentClient>()).Message, StringComparison.Ordinal);

        var malformed = new ServiceCollection();
        malformed.AddCentClient(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["RequestTimeout"] = "10s" }).Build());
        using var p2 = malformed.BuildServiceProvider();
        Assert.Contains("RequestTimeout \"10s\"", Assert.Throws<CentInvalidConfigException>(() => p2.GetRequiredService<CentClient>()).Message, StringComparison.Ordinal);
    }

    private sealed class ListLoggerProvider : ILoggerProvider
    {
        public List<(string Category, LogLevel Level, string Message, Exception? Error)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(ListLoggerProvider provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (provider.Entries)
                {
                    provider.Entries.Add((category, logLevel, formatter(state, exception), exception));
                }
            }
        }
    }

    [Fact]
    public async Task HandlerFailuresAreLoggedThroughTheApplicationsLogging()
    {
        var logs = new ListLoggerProvider();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders().AddProvider(logs);
        await using var app = builder.Build();
        var proxy = new ProxyHandlers();
        app.MapCentrifugo("/connect", proxy.Connect((_, _) => throw new InvalidOperationException("database down")));
        app.MapCentrifugo("/subscribe", proxy.Subscribe((_, _) => throw new ProxyErrorException(403, "no")));
        await app.StartAsync(Ct);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var http = new HttpClient { BaseAddress = new Uri(address) };

        Assert.Equal(HttpStatusCode.InternalServerError, (await http.PostAsync("/connect", new StringContent("{}"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/connect", new StringContent("nope"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/subscribe", new StringContent("{}"), Ct)).StatusCode);
        await app.StopAsync(Ct);

        List<(string Category, LogLevel Level, string Message, Exception? Error)> ours;
        lock (logs.Entries)
        {
            ours = logs.Entries.Where(e => e.Category == "Centrifugal.Cent").ToList();
        }
        Assert.Equal(2, ours.Count);
        Assert.Equal(LogLevel.Error, ours[0].Level);
        Assert.Equal("Centrifugo handler failed POST /connect with status 500", ours[0].Message);
        Assert.Equal("database down", ours[0].Error!.Message);
        Assert.Equal(LogLevel.Warning, ours[1].Level);
    }

    /// <summary>Application code sending a batch.</summary>
    private static async Task<ulong> PublishBothAsync(CentClient client, CancellationToken ct)
    {
        var batch = client.NewBatch();
        var a = batch.Publish(new PublishRequest { Channel = "a", Data = 1 });
        var b = batch.Publish(new PublishRequest { Channel = "b", Data = 2 });
        try
        {
            await batch.SendAsync(ct);
        }
        catch (BatchFailedCommandsException)
        {
        }
        return (a.Error is null ? a.Result.Offset : 0) + (b.Error is null ? b.Result.Offset : 0);
    }

    [Fact]
    public async Task BatchesAreMockedToo()
    {
        var batch = Substitute.For<CentBatch>();
        batch.Publish(Arg.Is<PublishRequest>(r => r.Channel == "a")).Returns(Pending.FromResult(new PublishResult { Offset = 5 }));
        batch.Publish(Arg.Is<PublishRequest>(r => r.Channel == "b")).Returns(Pending.FromException<PublishResult>(new CentApiException(102, "unknown channel")));
        batch.SendAsync(Arg.Any<CancellationToken>()).Returns<BatchResult>(_ => throw new BatchFailedCommandsException(
            new BatchResult(2, [new FailedCommand(1, "publish", new CentApiException(102, "unknown channel"))])));
        var client = Substitute.For<CentClient>();
        client.NewBatch(Arg.Any<bool>()).Returns(batch);

        Assert.Equal(5UL, await PublishBothAsync(client, Ct));
        Assert.Throws<CentApiException>(() => Pending.FromException<PublishResult>(new CentApiException(102, "x")).Result);
        Assert.True(Pending.FromResult(1).IsSent);
    }

    private sealed class HeaderHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add("X-Traced", "yes");
            return base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task TheHttpClientComesFromTheFactoryWithTheApplicationsHandlers()
    {
        var fake = new FakeCentrifugo();
        var services = new ServiceCollection();
        services.AddCentClient(o => o.ApiEndpoint = "http://centrifugo.test/api")
            .AddHttpMessageHandler(() => new HeaderHandler())
            .ConfigurePrimaryHttpMessageHandler(() => fake);
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<CentClient>().PublishAsync("news", 1, Ct);
        Assert.Equal("yes", Assert.Single(fake.Requests).Headers["X-Traced"]);
    }

    [Fact]
    public async Task TheFactorysHandlerIsTheSdksDefault()
    {
        var requests = 0;
        await using var server = new TestServer((_, _, s) =>
        {
            Interlocked.Increment(ref requests);
            return TestServer.Respond(s, 307, "", "Location: http://127.0.0.1:1/api/info\r\n");
        });
        var services = new ServiceCollection();
        services.AddCentClient(o => o.ApiEndpoint = server.Url);
        await using var provider = services.BuildServiceProvider();
        var e = await Assert.ThrowsAsync<CentHttpException>(() => provider.GetRequiredService<CentClient>().InfoAsync(new InfoRequest(), Ct));
        Assert.Equal(307, e.StatusCode);
        Assert.Equal(1, requests);
    }
}
