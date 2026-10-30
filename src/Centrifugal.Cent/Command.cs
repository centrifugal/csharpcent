using System.Text.Json;
using System.Text.Json.Serialization;

namespace Centrifugal.Cent;

/// <summary>
/// An API call written for Centrifugo's async consumers (spec 9): Kafka, NATS JetStream,
/// Redis Streams, a PostgreSQL outbox table. A method per API method makes one -
/// <c>Command.Publish(request)</c> - validating the request as a call would, so that a
/// command Centrifugo could not run fails when written rather than in the consumer, which
/// would drop it.
/// </summary>
/// <remarks>
/// <para>
/// Encoded as JSON - <see cref="ToJson"/>, or <c>JsonSerializer.Serialize</c> of it or of a
/// value holding it - it is the <c>{"method": ..., "payload": {...}}</c> message consumers
/// take whole, <see cref="Payload"/> the JSON object, never a string of it.
/// </para>
/// <para>
/// A consumer keeps commands in order only within a partition: a channel's commands keep
/// their order only when every writer derives their partition from the channel the same way.
/// </para>
/// </remarks>
/// <example>
/// Writing to a PostgreSQL outbox table with Npgsql, payload into a <c>jsonb</c> column:
/// <code>
/// var command = Command.Publish(new PublishRequest { Channel = "news", Data = data });
/// await using var insert = new NpgsqlCommand(
///     "INSERT INTO centrifugo_outbox (method, payload, partition) VALUES ($1, $2, $3)", connection, transaction);
/// insert.Parameters.Add(new() { Value = command.Method });
/// insert.Parameters.Add(new() { Value = command.Payload.GetRawText(), NpgsqlDbType = NpgsqlDbType.Jsonb });
/// insert.Parameters.Add(new() { Value = partition });
/// await insert.ExecuteNonQueryAsync(ct);
/// </code>
/// </example>
public sealed partial class Command
{
    [JsonConstructor]
    internal Command(string method, JsonElement payload)
    {
        Method = method;
        Payload = payload;
    }

    /// <summary>The API method, such as <c>publish</c>.</summary>
    [JsonPropertyName("method")]
    public string Method { get; }

    /// <summary>The request as a JSON object.</summary>
    [JsonPropertyName("payload")]
    public JsonElement Payload { get; }

    /// <summary>The command as the JSON message consumers take.</summary>
    /// <returns>The JSON text.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, Wire.Info<Command>());

    /// <summary>The command as the JSON message consumers take, UTF-8 encoded.</summary>
    /// <returns>The JSON.</returns>
    public byte[] ToUtf8Bytes() => JsonSerializer.SerializeToUtf8Bytes(this, Wire.Info<Command>());

    /// <inheritdoc/>
    public override string ToString() => ToJson();

    internal static Command Create<TRequest, TResult>(ApiMethod<TRequest, TResult> method, TRequest request)
    {
        using var doc = JsonDocument.Parse(method.Encode(request));
        return new Command(method.Name, doc.RootElement.Clone());
    }
}
