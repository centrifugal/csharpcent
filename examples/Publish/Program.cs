// Publishing with csharpcent: run Centrifugo with an API key, then
//
//   CENTRIFUGO_API_KEY=... dotnet run --project examples/Publish
using Centrifugal.Cent;

await using var client = new CentClient(new CentClientOptions
{
    ApiEndpoint = Environment.GetEnvironmentVariable("CENTRIFUGO_API_ENDPOINT") ?? "http://localhost:8000/api",
    ApiKey = Environment.GetEnvironmentVariable("CENTRIFUGO_API_KEY"),
});

// A payload is any JSON value: an object of the application's, an anonymous type, a
// JsonElement or JsonNode, or RawJson for JSON text the application already has.
var result = await client.PublishAsync(new PublishRequest
{
    Channel = "news",
    Data = new { text = "hello", id = "9007199254740993" }, // big integers as strings
    IdempotencyKey = "news-hello-1",
});
Console.WriteLine($"published at offset {result.Offset}, epoch {result.Epoch}");

// A broadcast retried in the channels which may still succeed, under one idempotency key.
try
{
    var broadcast = await client.BroadcastWithRetryAsync(
        new BroadcastRequest { Channels = ["news", "sport", "weather"], Data = new { text = "everywhere" } },
        new BroadcastRetryOptions { MaxAttempts = 3 });
    Console.WriteLine($"broadcast into {broadcast.Channels.Count} channels, key {broadcast.IdempotencyKey}");
}
catch (BroadcastFailedChannelsException e)
{
    // Some channels failed; the others got the publication. Never retry the whole broadcast.
    foreach (var failed in e.Failed)
    {
        Console.WriteLine($"{failed.Channel} failed after {failed.Attempts} attempts: {failed.Error!.Message}");
    }
}

// A batch: commands in one request, each with its own outcome.
var batch = client.NewBatch();
var history = batch.History(new HistoryRequest { Channel = "news", Limit = 5 });
var presence = batch.PresenceStats(new PresenceStatsRequest { Channel = "news" });
try
{
    await batch.SendAsync();
    Console.WriteLine($"{history.Result.Publications.Count} publications, {presence.Result.NumClients} clients");
}
catch (BatchFailedCommandsException e)
{
    Console.WriteLine(e.Message);
}
catch (CentApiException e) when (e.Code == ErrorCodes.UnknownChannel)
{
    Console.WriteLine("no such namespace");
}
