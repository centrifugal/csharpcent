using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Centrifugal.Cent.Tests;

public sealed class PayloadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Message
    {
        public string Text { get; set; } = "";

        public int Count { get; set; }
    }

    private static async Task<string> SentData(object? data)
    {
        var fake = new FakeCentrifugo();
        await using var client = fake.CreateClient();
        await client.PublishAsync(new PublishRequest { Channel = "c", Data = data }, Ct);
        return fake.Requests[0].Json.GetProperty("data").GetRawText();
    }

    [Fact]
    public async Task ANativeValueAndRawJsonSendTheSameValue()
    {
        var native = await SentData(new Message { Text = "hi", Count = 2 });
        var anonymous = await SentData(new { text = "hi", count = 2 });
        var raw = await SentData(RawJson.Parse("""{"text":"hi","count":2}"""));
        var node = await SentData(new JsonObject { ["text"] = "hi", ["count"] = 2 });
        var element = await SentData(JsonDocument.Parse("""{"text":"hi","count":2}""").RootElement);
        Assert.Equal("""{"text":"hi","count":2}""", native);
        Assert.All(new[] { anonymous, raw, node, element }, s => Assert.Equal(native, s));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("9007199254740993")]
    [InlineData("1e400")]
    [InlineData("-0")]
    [InlineData("""{"b":1,"a":[1.50,{}]}""")]
    [InlineData("null")]
    public async Task RawJsonIsSentAsTheSameValueNumberTextKept(string json) =>
        Assert.Equal(json, await SentData(RawJson.Parse(json)));

    [Fact]
    public async Task AStringIsAJsonString()
    {
        Assert.Equal("\"hello\"", await SentData("hello"));
        Assert.Equal("\"{\\\"a\\\":1}\"", await SentData("""{"a":1}"""));
    }

    [Fact]
    public async Task ScalarsAreJsonScalars()
    {
        Assert.Equal("42", await SentData(42));
        Assert.Equal("true", await SentData(true));
        Assert.Equal("1.5", await SentData(1.5));
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task InvalidPayloadsAreRefusedBeforeSending(object payload, string message)
    {
        var fake = new FakeCentrifugo();
        await using var client = fake.CreateClient();
        var e = await Assert.ThrowsAsync<CentInvalidRequestException>(() => client.PublishAsync(new PublishRequest { Channel = "c", Data = payload }, Ct));
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
        Assert.Empty(fake.Requests);
    }

    public static TheoryData<object, string> Refused() => new()
    {
        { new byte[] { 1, 2 }, "bytes are not a JSON payload" },
        { new ReadOnlyMemory<byte>([1]), "bytes are not a JSON payload" },
        { double.NaN, "valid JSON" },
        { new { x = double.PositiveInfinity }, "valid JSON" },
        { new JsonObject { ["x"] = double.NaN }, "valid JSON" },
        { "\ud800", "unpaired surrogate" },
        { new { s = "a\udc00" }, "unpaired surrogate" },
        { default(JsonElement), "undefined JsonElement" },
    };

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{} {}")]
    [InlineData("NaN")]
    [InlineData("'a'")]
    [InlineData("{\"a\":1,}")]
    [InlineData("// comment\n1")]
    public void InvalidRawJsonIsRefused(string json) =>
        Assert.Throws<ArgumentException>(() => RawJson.Parse(json));

    [Fact]
    public void InvalidUtf8RawJsonIsRefused() =>
        Assert.Throws<ArgumentException>(() => RawJson.Parse(new byte[] { 0x22, 0xff, 0x22 }));

    [Fact]
    public async Task APayloadFromAReplyGoesBackAsTheSameValue()
    {
        const string data = """{"o":{},"k":{"0":"a"},"big":9007199254740993,"f":1.0,"a":[],"n":null,"s":"é😀"}""";
        var fake = new FakeCentrifugo(r => r.ApiMethod == "history"
            ? Answer.Ok("""{"result":{"publications":[{"data":""" + data + ""","offset":1},{"data":null,"offset":2},{"offset":3,"b64data":"AQ=="}]}}""")
            : Answer.Ok(FakeCentrifugo.DefaultReply(r)));
        await using var client = fake.CreateClient();
        var history = await client.HistoryAsync(new HistoryRequest { Channel = "c", Limit = 10 }, Ct);
        var pubs = history.Publications;

        // JSON null is a payload: an element of kind Null, distinct from no payload.
        Assert.Equal(JsonValueKind.Null, pubs[1].Data!.Value.ValueKind);
        Assert.Null(pubs[2].Data);

        await client.PublishAsync(new PublishRequest { Channel = "c", Data = pubs[0].Data }, Ct);
        await client.PublishAsync(new PublishRequest { Channel = "c", Data = pubs[1].Data }, Ct);
        var sent = fake.Requests[1].Json.GetProperty("data").GetRawText();
        // The same JSON value: an encoder may normalize string escapes; number text is kept.
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(data), JsonNode.Parse(sent)), sent);
        Assert.Contains("\"big\":9007199254740993,\"f\":1.0,", sent, StringComparison.Ordinal);
        Assert.Contains("\"o\":{},\"k\":{\"0\":\"a\"}", sent, StringComparison.Ordinal);
        Assert.Equal("null", fake.Requests[2].Json.GetProperty("data").GetRawText());
    }

    [Fact]
    public void PayloadsDecodeIntoApplicationTypes()
    {
        var element = JsonDocument.Parse("""{"text":"hi","count":3}""").RootElement;
        var message = element.Decode<Message>()!;
        Assert.Equal("hi", message.Text);
        Assert.Equal(3, message.Count);
        var viaContext = element.Decode(PayloadJsonContext.Default.ContextMessage)!;
        Assert.Equal("hi", viaContext.Text);
        Assert.Throws<JsonException>(() => JsonDocument.Parse("[1]").RootElement.Decode<Message>());
    }

    [Fact]
    public async Task RawJsonSerializeTakesAJsonContext()
    {
        var raw = RawJson.Serialize(new AotMessage { Text = "aot" }, PayloadJsonContext.Default.AotMessage);
        Assert.Equal("""{"Text":"aot"}""", raw.ToString());
        Assert.Equal("""{"Text":"aot"}""", await SentData(raw));
    }

    [Fact]
    public void RawJsonIsAValue()
    {
        Assert.Equal(RawJson.Parse("[1]"), RawJson.Parse("[1]"));
        Assert.Equal(RawJson.Parse("[1]").GetHashCode(), RawJson.Parse("[1]").GetHashCode());
        Assert.NotEqual(RawJson.Parse("[1]"), RawJson.Parse("[ 1 ]"));
        Assert.Equal("null", RawJson.Null.ToString());
    }

    [Fact]
    public async Task ClaimsAndCommandsTakePayloadsTheSameWay()
    {
        var command = Command.Publish(new PublishRequest { Channel = "c", Data = RawJson.Parse("1.0") });
        Assert.Equal("1.0", command.Payload.GetProperty("data").GetRawText());
        Assert.Equal("\"x\"", await SentData("x"));
    }
}

[JsonSerializable(typeof(ContextMessage))]
[JsonSerializable(typeof(AotMessage))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal sealed partial class PayloadJsonContext : JsonSerializerContext;

internal sealed class ContextMessage
{
    public string Text { get; set; } = "";
}

internal sealed class AotMessage
{
    public string Text { get; set; } = "";
}
