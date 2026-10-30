using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Centrifugal.Cent;

/// <summary>A channel's outcome in a broadcast: its publication or its error.</summary>
public sealed class ChannelResult
{
    /// <summary>A channel which got the publication - for test doubles of applications.</summary>
    /// <param name="channel">The channel.</param>
    /// <param name="result">Its publication.</param>
    /// <param name="attempts">The attempts it took.</param>
    public ChannelResult(string channel, PublishResult result, int attempts = 1)
    {
        Channel = channel;
        Result = result;
        Attempts = attempts;
    }

    /// <summary>A channel which failed - for test doubles of applications.</summary>
    /// <param name="channel">The channel.</param>
    /// <param name="error">Its error.</param>
    /// <param name="attempts">The attempts it took.</param>
    public ChannelResult(string channel, Exception error, int attempts = 1)
    {
        Channel = channel;
        Error = error;
        Attempts = attempts;
    }

    [JsonConstructor]
    internal ChannelResult(string channel, PublishResult? result, Exception? error, int attempts)
    {
        Channel = channel;
        Result = result;
        Error = error;
        Attempts = attempts;
    }

    /// <summary>The channel.</summary>
    [JsonPropertyName("channel")]
    public string Channel { get; }

    /// <summary>The publication; null when the channel failed.</summary>
    [JsonPropertyName("result")]
    public PublishResult? Result { get; }

    /// <summary>The channel's error; null when it got the publication.</summary>
    [JsonPropertyName("error")]
    [JsonConverter(typeof(ErrorJsonConverter))]
    public Exception? Error { get; }

    /// <summary>The attempts the channel took: 1 unless retried.</summary>
    [JsonPropertyName("attempts")]
    public int Attempts { get; }
}

/// <summary>
/// The outcome of a broadcast: one entry per requested channel, in order (spec 7.1). A
/// broadcast which some channel failed throws <see cref="BroadcastFailedChannelsException"/>,
/// holding this result.
/// </summary>
public sealed class BroadcastResult
{
    /// <summary>Creates a result - for test doubles of applications.</summary>
    /// <param name="channels">Every channel's outcome, in the request's order.</param>
    /// <param name="idempotencyKey">The broadcast's idempotency key.</param>
    public BroadcastResult(IReadOnlyList<ChannelResult> channels, string idempotencyKey = "")
    {
        ArgumentNullException.ThrowIfNull(channels);
        Channels = channels;
        IdempotencyKey = idempotencyKey;
        Failed = channels.Where(c => c.Error is not null).ToList();
    }

    [JsonConstructor]
    internal BroadcastResult(IReadOnlyList<ChannelResult> channels, string idempotencyKey, bool replied)
        : this(channels, idempotencyKey)
    {
        _ = replied;
    }

    /// <summary>Every channel's outcome, in the request's order.</summary>
    [JsonPropertyName("channels")]
    public IReadOnlyList<ChannelResult> Channels { get; }

    /// <summary>
    /// The idempotency key every attempt carried: the request's, or the one
    /// <see cref="CentClient.BroadcastWithRetryAsync"/> made for it.
    /// </summary>
    [JsonPropertyName("idempotency_key")]
    public string IdempotencyKey { get; }

    /// <summary>
    /// Whether Centrifugo replied: always true for a result the SDK gives, since a
    /// broadcast failing as a whole throws its own error, with no result.
    /// </summary>
    [JsonPropertyName("replied")]
    public bool Replied { get; } = true;

    /// <summary>The channels which failed, in order.</summary>
    [JsonIgnore]
    public IReadOnlyList<ChannelResult> Failed { get; }

    /// <summary>
    /// The channels a retry should go to: those whose error is retryable. Retry with the
    /// same idempotency key, never the whole broadcast: the other channels got it.
    /// </summary>
    /// <returns>The channels, in order.</returns>
    public IReadOnlyList<string> RetryableChannels() =>
        Failed.Where(c => Retry.IsRetryable(c.Error!)).Select(c => c.Channel).ToList();
}

/// <summary>
/// Centrifugo carried a broadcast out, and one or more channels failed - up to all of them:
/// <see cref="Failed"/> holds each one's error, <see cref="Result"/> every channel's outcome.
/// It never matches its channels' errors, even with one channel: for one channel,
/// <c>PublishAsync</c> is the call.
/// </summary>
/// <example>
/// <code>
/// try
/// {
///     await client.BroadcastAsync(request, ct);
/// }
/// catch (BroadcastFailedChannelsException e)
/// {
///     logger.LogWarning("broadcast failed in {Channels}", e.Failed.Select(c => c.Channel));
/// }
/// </code>
/// </example>
public sealed class BroadcastFailedChannelsException : CentException
{
    /// <summary>Creates the error - for test doubles of applications.</summary>
    /// <param name="result">The broadcast's outcome, with at least one failed channel.</param>
    public BroadcastFailedChannelsException(BroadcastResult result)
        : base(Describe(result))
    {
        Result = result;
    }

    /// <summary>Every channel's outcome.</summary>
    public BroadcastResult Result { get; }

    /// <summary>The failed channels, in order.</summary>
    public IReadOnlyList<ChannelResult> Failed => Result.Failed;

    /// <summary>The channels of the broadcast.</summary>
    public int Total => Result.Channels.Count;

    private static string Describe(BroadcastResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var failed = result.Failed.Count;
        var total = result.Channels.Count;
        var text = $"broadcast: {failed} of {total} channels failed, {total - failed} got the publication";
        if (result.Failed.Count > 0 && result.Failed[0] is { Error: { } first } channel)
        {
            text += $": {channel.Channel}: {first.Message}";
        }
        if (result.RetryableChannels().Count > 0)
        {
            text += " (retry only RetryableChannels, with the same IdempotencyKey)";
        }
        return text;
    }
}

/// <summary>The options of <see cref="CentClient.BroadcastWithRetryAsync"/>.</summary>
public sealed class BroadcastRetryOptions
{
    /// <summary>The attempts at most: 2 - one retry - by default.</summary>
    public int MaxAttempts { get; set; } = 2;

    /// <summary>Bounds each attempt; the call's timeout bounds them all, waits included.</summary>
    public TimeSpan? AttemptTimeout { get; set; }

    /// <summary>
    /// Called after each failed attempt a retry may fix, except the last. It returns how long
    /// to wait before the next one - null for the default, an exponential backoff from 100 ms
    /// to 5 s - and throws to stop: the call then throws
    /// <see cref="CentRetryStoppedException"/>, holding what it threw and the broadcast's
    /// error. A callback which only logs cannot stop the retries by accident.
    /// </summary>
    public Func<BroadcastAttempt, TimeSpan?>? OnAttemptError { get; set; }
}

/// <summary>A failed attempt of <see cref="CentClient.BroadcastWithRetryAsync"/>.</summary>
/// <param name="Attempt">The attempt's number, from 1.</param>
/// <param name="Error">Its error.</param>
/// <param name="RetryChannels">The channels the next attempt sends: those which may still succeed.</param>
/// <param name="Result">Every channel's outcome so far.</param>
/// <param name="IdempotencyKey">The key every attempt carries.</param>
public sealed record BroadcastAttempt(int Attempt, Exception Error, IReadOnlyList<string> RetryChannels, BroadcastResult Result, string IdempotencyKey);

public partial class CentClient
{
    private static readonly Func<int, TimeSpan> DefaultBackoff = Retry.ExponentialBackoff(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5));

    /// <summary>
    /// Publishes the same data into many channels: one entry per channel in the result, in
    /// order (spec 7.1).
    /// </summary>
    /// <remarks>
    /// When some channels fail it throws <see cref="BroadcastFailedChannelsException"/>
    /// holding every channel's outcome. Never retry the whole broadcast: the other channels
    /// got the publication. Retry only <see cref="BroadcastResult.RetryableChannels"/> with
    /// the same idempotency key, or use <see cref="BroadcastWithRetryAsync"/>.
    /// </remarks>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the call: it then throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>Every channel's publication.</returns>
    /// <exception cref="BroadcastFailedChannelsException">Some channels failed.</exception>
    /// <exception cref="CentException">The broadcast failed as a whole.</exception>
    public virtual async Task<BroadcastResult> BroadcastAsync(BroadcastRequest request, CancellationToken cancellationToken = default)
    {
        var channels = request?.Channels?.ToList() ?? [];
        var wire = await CallAsync(ApiMethods.Broadcast, request!, cancellationToken).ConfigureAwait(false);
        return Broadcasts.Complete(channels, wire, request!.IdempotencyKey ?? "");
    }

    /// <summary>
    /// Broadcasts, retrying the channels which may still succeed (spec 7.3): every attempt
    /// carries the same idempotency key - the request's, or one made for the call and
    /// returned in the result - so that a channel never gets the publication twice.
    /// </summary>
    /// <remarks>
    /// A channel which failed for good is not retried, and a failure no retry can fix - a
    /// 401, an invalid request - stops it at once. No attempt starts once the call's
    /// timeout has passed. The memory, Redis and PostgreSQL brokers keep idempotency keys,
    /// five minutes by default; the NATS broker ignores them, and a retry publishes again.
    /// </remarks>
    /// <param name="request">The request.</param>
    /// <param name="options">The retries: 2 attempts by default.</param>
    /// <param name="cancellationToken">Cancels the call: it then throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>Every channel's publication, and how many attempts each took.</returns>
    /// <exception cref="BroadcastFailedChannelsException">Some channels still failed: every channel's last outcome.</exception>
    /// <exception cref="CentRetryStoppedException">The retries were stopped by <see cref="BroadcastRetryOptions.OnAttemptError"/>.</exception>
    /// <exception cref="CentException">Every attempt failed as a whole: the last one's error.</exception>
    public virtual async Task<BroadcastResult> BroadcastWithRetryAsync(BroadcastRequest request, BroadcastRetryOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new BroadcastRetryOptions();
        if (options.MaxAttempts <= 0)
        {
            throw new CentInvalidRequestException("broadcast", $"MaxAttempts is {options.MaxAttempts}: give at least 1");
        }
        if (options.AttemptTimeout is { } t && t <= TimeSpan.Zero)
        {
            throw new CentInvalidRequestException("broadcast", "AttemptTimeout is not positive");
        }
        if (request is null)
        {
            throw new CentInvalidRequestException("broadcast", "no request");
        }
        var time = _core.Time;
        var deadline = Deadline.After(time, _timeout);
        var key = string.IsNullOrEmpty(request.IdempotencyKey) ? Broadcasts.RandomKey() : request.IdempotencyKey;
        var channels = request.Channels?.ToList() ?? [];
        var outcomes = channels.Select(c => new ChannelResult(c, null, null, 0)).ToArray();
        var replied = false;
        // Positions in channels, not names: a channel listed twice keeps both.
        var pending = Enumerable.Range(0, channels.Count).ToList();
        Exception? last = null;
        for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            if (attempt > 1 && deadline.Passed(time))
            {
                break;
            }
            var attemptRequest = request.ForAttempt(pending.Select(i => channels[i]).ToList(), key);
            var timeout = deadline.Remaining(time);
            if (options.AttemptTimeout is { } at && (timeout == System.Threading.Timeout.InfiniteTimeSpan || at < timeout))
            {
                timeout = at;
            }
            Exception? error = null;
            try
            {
                var r = await WithTimeoutUnchecked(timeout).BroadcastAsync(attemptRequest, cancellationToken).ConfigureAwait(false);
                Record(r.Channels);
            }
            catch (BroadcastFailedChannelsException e)
            {
                Record(e.Result.Channels);
                error = e;
            }
            catch (CentException e)
            {
                // Failed as a whole: every channel sent failed with it.
                var sent = e is CentInvalidRequestException ? 0 : 1;
                foreach (var i in pending)
                {
                    outcomes[i] = new ChannelResult(channels[i], null, e, outcomes[i].Attempts + sent);
                }
                error = e;
            }
            if (error is null)
            {
                last = null;
                break;
            }
            last = error;
            if (!Retry.IsRetryable(error) || attempt == options.MaxAttempts || deadline.Passed(time))
            {
                break;
            }
            pending = pending.Where(i => outcomes[i].Error is { } e && Retry.IsRetryable(e)).ToList();
            var soFar = new BroadcastResult(outcomes.ToList(), key);
            TimeSpan wait;
            try
            {
                wait = options.OnAttemptError?.Invoke(new BroadcastAttempt(attempt, error, pending.Select(i => channels[i]).ToList(), soFar, key))
                    ?? DefaultBackoff(attempt);
            }
            catch (Exception reason)
            {
                throw new CentRetryStoppedException(reason, Final() ?? error);
            }
            var left = deadline.Remaining(time);
            if (left != System.Threading.Timeout.InfiniteTimeSpan && wait > left)
            {
                wait = left;
            }
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, time, cancellationToken).ConfigureAwait(false);
            }
            if (deadline.Passed(time))
            {
                var final = Final() ?? error;
                if (final is CentTimeoutException)
                {
                    throw final;
                }
                throw new CentRetryStoppedException(new CentTimeoutException("broadcast", _timeout), final);
            }
        }
        if (Final() is { } failure)
        {
            throw failure;
        }
        return new BroadcastResult(outcomes, key);

        void Record(IReadOnlyList<ChannelResult> results)
        {
            replied = true;
            for (var j = 0; j < pending.Count && j < results.Count; j++)
            {
                var i = pending[j];
                outcomes[i] = new ChannelResult(channels[i], results[j].Result, results[j].Error, outcomes[i].Attempts + 1);
            }
        }

        // Every channel's outcome when some attempt got a reply; otherwise the last
        // attempt's error: no channel got a reply.
        Exception? Final()
        {
            if (!replied)
            {
                return last;
            }
            var result = new BroadcastResult(outcomes, key);
            return result.Failed.Count > 0 ? new BroadcastFailedChannelsException(result) : null;
        }
    }

    private CentClient WithTimeoutUnchecked(TimeSpan timeout) =>
        new(_core, timeout <= TimeSpan.Zero && timeout != System.Threading.Timeout.InfiniteTimeSpan ? TimeSpan.FromTicks(1) : timeout);
}

public sealed partial class BroadcastRequest
{
    /// <summary>A copy for one attempt of a retried broadcast: its channels and key.</summary>
    internal BroadcastRequest ForAttempt(IList<string> channels, string idempotencyKey)
    {
        var copy = (BroadcastRequest)MemberwiseClone();
        copy.Channels = channels;
        copy.IdempotencyKey = idempotencyKey;
        return copy;
    }
}

/// <summary>A broadcast's reply on the wire: a response per channel.</summary>
internal sealed class BroadcastWire
{
    [JsonPropertyName("responses")]
    public IReadOnlyList<JsonElement> Responses { get; init => field = value ?? []; } = [];
}

/// <summary>A Centrifugo error on the wire.</summary>
internal sealed class ApiErrorWire
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init => field = value ?? ""; } = "";

    [JsonPropertyName("temporary")]
    public bool Temporary { get; init; }
}

internal static class Broadcasts
{
    private const string Base32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>A broadcast's result from its reply: <see cref="BroadcastFailedChannelsException"/> when some channel failed.</summary>
    public static BroadcastResult Complete(IReadOnlyList<string> channels, BroadcastWire wire, string idempotencyKey)
    {
        var result = Build(channels, wire, idempotencyKey);
        return result.Failed.Count > 0 ? throw new BroadcastFailedChannelsException(result) : result;
    }

    public static BroadcastResult Build(IReadOnlyList<string> channels, BroadcastWire wire, string idempotencyKey)
    {
        if (wire.Responses.Count != channels.Count)
        {
            throw new CentDecodeException("broadcast", $"{wire.Responses.Count} responses to {channels.Count} channels");
        }
        var list = new List<ChannelResult>(channels.Count);
        for (var i = 0; i < channels.Count; i++)
        {
            var r = wire.Responses[i];
            if (r.ValueKind != JsonValueKind.Object)
            {
                throw new CentDecodeException("broadcast", $"a response is a JSON {ClientCore.Kind(r)}, not an object");
            }
            if (r.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            {
                list.Add(new ChannelResult(channels[i], ClientCore.ApiError("broadcast", false, error, inBatch: false)));
                continue;
            }
            var element = ClientCore.ResultElement("broadcast", r, "result");
            PublishResult publication;
            try
            {
                publication = element.Deserialize(Wire.Info<PublishResult>())!;
            }
            catch (JsonException e)
            {
                throw new CentDecodeException("broadcast", e.Message, e);
            }
            list.Add(new ChannelResult(channels[i], publication));
        }
        return new BroadcastResult(list, idempotencyKey);
    }

    /// <summary>A random idempotency key: <c>csharpcent-</c> and 26 base32 characters, 128 bits.</summary>
    public static string RandomKey()
    {
        Span<byte> b = stackalloc byte[16];
        RandomNumberGenerator.Fill(b);
        var n = new UInt128(BitConverter.ToUInt64(b[8..]), BitConverter.ToUInt64(b[..8]));
        return string.Create(11 + 26, n, static (span, value) =>
        {
            "csharpcent-".AsSpan().CopyTo(span);
            for (var i = 0; i < 26; i++)
            {
                span[11 + i] = Base32[(int)((value >> (i * 5)) & 31)];
            }
        });
    }
}

/// <summary>An error in a result as JSON (spec 3.7): <c>{"code", "message"}</c> for a Centrifugo error, <c>{"message"}</c> for any other.</summary>
internal sealed class ErrorJsonConverter : JsonConverter<Exception?>
{
    public override Exception? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        if (root.TryGetProperty("code", out var c) && c.TryGetInt32(out var code))
        {
            var temporary = root.TryGetProperty("temporary", out var t) && t.ValueKind == JsonValueKind.True;
            return new CentApiException(code, message, temporary);
        }
        return new InvalidOperationException(message);
    }

    public override void Write(Utf8JsonWriter writer, Exception? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        if (value is CentApiException api)
        {
            writer.WriteNumber("code", api.Code);
            writer.WriteString("message", api.ApiMessage);
        }
        else
        {
            writer.WriteString("message", value.Message);
        }
        writer.WriteEndObject();
    }
}
