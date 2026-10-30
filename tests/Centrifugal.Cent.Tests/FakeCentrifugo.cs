using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Centrifugal.Cent.Tests;

/// <summary>A request the fake received.</summary>
public sealed record Recorded(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string Body)
{
    /// <summary>The API method: the path's last segment.</summary>
    public string ApiMethod => Uri.AbsolutePath[(Uri.AbsolutePath.LastIndexOf('/') + 1)..];

    public JsonElement Json => JsonDocument.Parse(Body.Length == 0 ? "{}" : Body).RootElement.Clone();

    /// <summary>The methods of a batch's commands, in order.</summary>
    public List<string> BatchMethods() =>
        Json.GetProperty("commands").EnumerateArray().Select(c => c.EnumerateObject().Single().Name).ToList();
}

/// <summary>An answer of the fake.</summary>
public abstract record Answer
{
    public static Answer Ok(string json) => new Reply(200, json);

    public static Answer WithStatus(int status, string body = "", IReadOnlyDictionary<string, string>? headers = null) =>
        new Reply(status, body, headers);

    public static Answer Throw(Exception e) => new Failure(e);

    public static Answer After(TimeSpan delay, Answer then) => new Delayed(delay, then);

    /// <summary>Waits for a task, then answers.</summary>
    public static Answer When(Task gate, Answer then) => new Gated(gate, then);

    public sealed record Reply(int Code, string Body, IReadOnlyDictionary<string, string>? Headers = null) : Answer;

    public sealed record Failure(Exception Exception) : Answer;

    public sealed record Delayed(TimeSpan Delay, Answer Then) : Answer;

    public sealed record Gated(Task Gate, Answer Then) : Answer;
}

/// <summary>
/// A fake Centrifugo: an <see cref="HttpMessageHandler"/> recording every request and
/// answering as the test says - by default as Centrifugo does when nothing fails.
/// </summary>
public sealed class FakeCentrifugo : HttpMessageHandler
{
    private readonly Func<Recorded, Answer> _respond;
    private readonly object _lock = new();
    private readonly List<Recorded> _requests = [];

    public FakeCentrifugo(Func<Recorded, Answer>? respond = null) => _respond = respond ?? (r => Answer.Ok(DefaultReply(r)));

    public List<Recorded> Requests
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    public List<string> Paths => Requests.ConvertAll(r => r.Uri.AbsolutePath);

    public CentClient CreateClient(Action<CentClientOptions>? configure = null)
    {
        var options = new CentClientOptions
        {
            ApiEndpoint = "http://centrifugo.test/api",
            ApiKey = "key",
            HttpClient = new HttpClient(this, disposeHandler: false),
        };
        configure?.Invoke(options);
        return new CentClient(options);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()))
        {
            headers[name] = string.Join(", ", values);
        }
        if (request.Headers.Host is { } host)
        {
            headers["Host"] = host;
        }
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new Recorded(request.Method, request.RequestUri!, headers, body);
        lock (_lock)
        {
            _requests.Add(recorded);
        }
        return await Respond(_respond(recorded), cancellationToken);
    }

    private static async Task<HttpResponseMessage> Respond(Answer answer, CancellationToken cancellationToken)
    {
        switch (answer)
        {
            case Answer.Delayed d:
                await Task.Delay(d.Delay, cancellationToken);
                return await Respond(d.Then, cancellationToken);
            case Answer.Gated g:
                await g.Gate.WaitAsync(cancellationToken);
                return await Respond(g.Then, cancellationToken);
            case Answer.Failure f:
                throw f.Exception;
            case Answer.Reply r:
                var response = new HttpResponseMessage((HttpStatusCode)r.Code)
                {
                    Content = new StringContent(r.Body, Encoding.UTF8, "application/json"),
                };
                foreach (var (name, value) in r.Headers ?? new Dictionary<string, string>())
                {
                    response.Headers.TryAddWithoutValidation(name, value);
                }
                return response;
            default:
                throw new InvalidOperationException(answer.ToString());
        }
    }

    /// <summary>Centrifugo's reply to a request with nothing failing.</summary>
    public static string DefaultReply(Recorded r)
    {
        if (r.ApiMethod == "batch")
        {
            var replies = new JsonArray();
            foreach (var c in r.Json.GetProperty("commands").EnumerateArray())
            {
                var command = c.EnumerateObject().Single();
                replies.Add(new JsonObject { [command.Name] = CommandResult(command.Name, command.Value) });
            }
            return new JsonObject { ["replies"] = replies }.ToJsonString();
        }
        return new JsonObject { ["result"] = CommandResult(r.ApiMethod, r.Json) }.ToJsonString();
    }

    public static JsonNode CommandResult(string method, JsonElement request)
    {
        if (method == "broadcast")
        {
            var responses = new JsonArray();
            var n = request.TryGetProperty("channels", out var ch) ? ch.GetArrayLength() : 0;
            for (var i = 0; i < n; i++)
            {
                responses.Add(new JsonObject { ["result"] = new JsonObject { ["offset"] = i + 1, ["epoch"] = "e" } });
            }
            return new JsonObject { ["responses"] = responses };
        }
        if (method == "publish")
        {
            return new JsonObject { ["offset"] = 1, ["epoch"] = "e" };
        }
        return new JsonObject();
    }
}
