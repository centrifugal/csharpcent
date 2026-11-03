using Centrifugal.Cent.Generator;

namespace Centrifugal.Cent.Tests;

/// <summary>The generator: what it reads in the protos' comments.</summary>
public sealed class GeneratorTests
{
    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "proto", "api.proto")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("proto/api.proto not found");
    }

    private static Schema ApiProto() =>
        Proto.Parse(File.ReadAllText(Path.Combine(Root(), "proto", "api.proto")), "proto/api.proto");

    // The presence wrappers and the required fields left unchecked are found in api.proto's
    // comments. The sets are pinned here, so that a changed comment shows as a failed test
    // rather than as a field changing how it is sent or checked.
    [Fact]
    public void PresenceWrappersAndUncheckedFieldsFromComments()
    {
        var s = ApiProto();
        var wrappers = s.Messages.Where(m => Types.IsPresenceWrapper(s, m.Name)).Select(m => m.Name).ToList();
        Assert.Equal(["BoolValue", "Int32Value", "DeviceUserUpdate", "DeviceTimezoneUpdate", "DeviceLocaleUpdate", "DeviceMetaUpdate"], wrappers);
        var notChecked = s.Messages.SelectMany(m => m.Fields.Where(f => Names.IsRequired(f) && Names.EmptyHasMeaning(f)).Select(f => $"{m.Name}.{f.Name}")).ToList();
        Assert.Equal(
            [
                "InvalidateUserTokensRequest.user",
                "DeviceUserUpdate.user", "DeviceTimezoneUpdate.timezone", "DeviceLocaleUpdate.locale", "DeviceMetaUpdate.meta",
                "UserTopicUpdateRequest.user",
            ],
            notChecked);
    }

    [Theory]
    [InlineData("channel is the channel. Required.", true)]
    [InlineData("data is the payload. Required unless b64data is set.", false)]
    [InlineData("expire_at is when. Required when the request sets limit_strategy.", false)]
    [InlineData("not required at all.", false)]
    public void OnlyTheExactRequiredMarksAField(string doc, bool required)
    {
        var f = new Field("x", "string", false, null, [doc]);
        Assert.Equal(required, Names.IsRequired(f));
    }
}
