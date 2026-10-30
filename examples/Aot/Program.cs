// csharpcent in a native AOT application: payloads go in as JsonNode, JsonElement or
// RawJson.Serialize with a JsonSerializerContext - never through reflection.
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Centrifugal.Cent;
using Centrifugal.Cent.Tokens;

var key = SigningKey.Hmac("secret");
Console.WriteLine(Token.Connection(key, new ConnectionClaims { User = "42", Expiry = Expiry.After(TimeSpan.FromHours(1)) }));

var command = Command.Publish(new PublishRequest
{
    Channel = "news",
    Data = RawJson.Serialize(new Message("hello"), AppJsonContext.Default.Message),
    Tags = new Dictionary<string, string> { ["source"] = "aot" },
});
Console.WriteLine(command.ToJson());
Console.WriteLine(Command.Publish(new PublishRequest { Channel = "news", Data = new JsonObject { ["text"] = "node" } }).ToJson());

await using var client = new CentClient(Environment.GetEnvironmentVariable("CENTRIFUGO_API_ENDPOINT") ?? "http://localhost:8000/api");
try
{
    await client.WithTimeout(TimeSpan.FromSeconds(1)).InfoAsync(new InfoRequest());
}
catch (CentException e)
{
    Console.WriteLine($"{e.GetType().Name}: retryable {Retry.IsRetryable(e)}");
}

internal sealed record Message(string Text);

[JsonSerializable(typeof(Message))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
