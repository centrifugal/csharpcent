using System.Text.Json;

namespace Centrifugal.Cent.Tests;

/// <summary>How requests go out: presence wrappers, zero messages, required fields (spec 2, 3.3, 3.5).</summary>
public sealed class RequestTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> Sent<TResult>(Func<CentClient, Task<TResult>> call)
    {
        var fake = new FakeCentrifugo();
        await using var client = fake.CreateClient();
        await call(client);
        return Assert.Single(fake.Requests).Json;
    }

    [Fact]
    public async Task AnEmptyPresenceWrapperIsSentAsAnEmptyObject()
    {
        var body = await Sent(c => c.DeviceUpdateAsync(new DeviceUpdateRequest
        {
            Users = ["42"],
            UserUpdate = new DeviceUserUpdate { User = "" },
            TimezoneUpdate = new DeviceTimezoneUpdate { Timezone = "" },
            LocaleUpdate = new DeviceLocaleUpdate { Locale = "" },
            MetaUpdate = new DeviceMetaUpdate { Meta = new Dictionary<string, string>() },
        }, Ct));
        Assert.Equal(
            """{"users":["42"],"user_update":{},"meta_update":{},"timezone_update":{},"locale_update":{}}""",
            body.GetRawText());
    }

    [Fact]
    public async Task AnUnsetPresenceWrapperIsNotSent()
    {
        var body = await Sent(c => c.DeviceUpdateAsync(new DeviceUpdateRequest
        {
            Users = ["42"],
            TimezoneUpdate = new DeviceTimezoneUpdate { Timezone = "Europe/Berlin" },
        }, Ct));
        Assert.Equal("""{"users":["42"],"timezone_update":{"timezone":"Europe/Berlin"}}""", body.GetRawText());
    }

    [Fact]
    public async Task ABoolValueIsSentWhenSetFalseToo()
    {
        var body = await Sent(c => c.SubscribeAsync(new SubscribeRequest
        {
            Channel = "c",
            User = "42",
            Override = new SubscribeOptionOverride { Presence = false },
        }, Ct));
        Assert.Equal("""{"channel":"c","user":"42","override":{"presence":{"value":false}}}""", body.GetRawText());
    }

    // A message every field of which is zero is not sent, as a zero field is not (spec 2):
    // an empty limit strategy is no limit strategy.
    [Fact]
    public async Task AZeroMessageIsNotSent()
    {
        var body = await Sent(c => c.SendPushNotificationAsync(new SendPushNotificationRequest
        {
            Recipient = new PushRecipient { FcmTokens = ["t"] },
            Notification = new PushNotification { Fcm = new FcmPushNotification { Message = RawJson.Parse("{}") } },
            LimitStrategy = new PushLimitStrategy { RateLimit = new PushRateLimitStrategy() },
        }, Ct));
        Assert.False(body.TryGetProperty("limit_strategy", out _));
    }

    public static TheoryData<string, Func<CentClient, Task>> Missing => new()
    {
        { "publish: Channel is required", c => c.PublishAsync(new PublishRequest { Channel = "", Data = 1 }, Ct) },
        { "publish: Channel is required", c => c.PublishAsync(new PublishRequest { Channel = null!, Data = 1 }, Ct) },
        { "broadcast: Channels is required", c => c.BroadcastAsync(new BroadcastRequest { Channels = [], Data = 1 }, Ct) },
        { "presence: Channel is required", c => c.PresenceAsync(new PresenceRequest { Channel = "" }, Ct) },
        { "shared_poll_publish: Version is required", c => c.SharedPollPublishAsync(new SharedPollPublishRequest { Channel = "c", Key = "k", Version = 0 }, Ct) },
        {
            "device_update: TopicsUpdate.Op is required",
            c => c.DeviceUpdateAsync(new DeviceUpdateRequest { Users = ["42"], TopicsUpdate = new DeviceTopicsUpdate { Op = "", Topics = ["t"] } }, Ct)
        },
        {
            "send_push_notification: Notification is required",
            c => c.SendPushNotificationAsync(new SendPushNotificationRequest { Recipient = new PushRecipient { FcmTokens = ["t"] }, Notification = new PushNotification() }, Ct)
        },
        {
            "send_push_notification: Notification.Apns.Payload is required",
            c => c.SendPushNotificationAsync(new SendPushNotificationRequest
            {
                Recipient = new PushRecipient { FcmTokens = ["t"] },
                Notification = new PushNotification { Apns = new ApnsPushNotification { Headers = new Dictionary<string, string> { ["apns-priority"] = "10" }, Payload = null! } },
            }, Ct)
        },
        {
            "send_push_notification: LimitStrategy.RateLimit.Policies[1].Rate is required",
            c => c.SendPushNotificationAsync(new SendPushNotificationRequest
            {
                Recipient = new PushRecipient { FcmTokens = ["t"] },
                Notification = new PushNotification { Fcm = new FcmPushNotification { Message = RawJson.Parse("{}") }, ExpireAt = 1 },
                LimitStrategy = new PushLimitStrategy
                {
                    RateLimit = new PushRateLimitStrategy
                    {
                        Policies = [new RateLimitPolicy { Rate = 1, IntervalMs = 1000 }, new RateLimitPolicy { Rate = 0, IntervalMs = 1000 }],
                    },
                },
            }, Ct)
        },
    };

    [Theory]
    [MemberData(nameof(Missing))]
    public async Task AnUnsetRequiredFieldFailsNamingItsPathAndNothingIsSent(string message, Func<CentClient, Task> call)
    {
        var fake = new FakeCentrifugo();
        await using var client = fake.CreateClient();
        var e = await Assert.ThrowsAsync<CentInvalidRequestException>(() => call(client));
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task ABatchOrCommandWithAnUnsetRequiredFieldFails()
    {
        var fake = new FakeCentrifugo();
        await using var client = fake.CreateClient();
        var batch = client.NewBatch();
        batch.Presence(new PresenceRequest { Channel = "a" });
        batch.Presence(new PresenceRequest { Channel = "" });
        var e = await Assert.ThrowsAsync<CentInvalidRequestException>(() => batch.SendAsync(Ct));
        Assert.Contains("Channel is required", e.Message, StringComparison.Ordinal);
        Assert.Empty(fake.Requests);
        var c = Assert.Throws<CentInvalidRequestException>(() => Command.Presence(new PresenceRequest { Channel = "" }));
        Assert.Contains("Channel is required", c.Message, StringComparison.Ordinal);
    }

    // An optional nested message left unset - or empty, so not sent - is not checked.
    [Fact]
    public async Task AnUnsetOptionalMessageIsNotChecked()
    {
        await Sent(c => c.DeviceUpdateAsync(new DeviceUpdateRequest { Users = ["42"], TopicsUpdate = new DeviceTopicsUpdate { Op = "" } }, Ct));
        await Sent(c => c.SendPushNotificationAsync(new SendPushNotificationRequest
        {
            Recipient = new PushRecipient { FcmTokens = ["t"] },
            Notification = new PushNotification { Fcm = new FcmPushNotification { Message = RawJson.Parse("{}") } },
        }, Ct));
    }

    // A required field whose empty value has a meaning is sent empty: here, anonymous users
    // and the global binding.
    [Fact]
    public async Task ARequiredFieldWhoseEmptyValueHasAMeaningIsSentEmpty()
    {
        var body = await Sent(c => c.InvalidateUserTokensAsync(new InvalidateUserTokensRequest { User = "" }, Ct));
        Assert.Equal("{}", body.GetRawText());
        body = await Sent(c => c.UserTopicUpdateAsync(new UserTopicUpdateRequest { User = "", Op = "add", Topics = ["t"] }, Ct));
        Assert.Equal("""{"op":"add","topics":["t"]}""", body.GetRawText());
    }
}
