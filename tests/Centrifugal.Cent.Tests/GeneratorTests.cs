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

    [Theory]
    [InlineData("op is the operation. Required. One of: add, remove, set.", "add,remove,set")]
    [InlineData("provider is the provider. Required. One of: fcm,|hms, apns, webpush.", "fcm,hms,apns,webpush")]
    [InlineData("channel is the channel. Required.", null)]
    public void ListedValues(string doc, string? want)
    {
        var got = Consts.ListedValues([.. doc.Split('|')]);
        Assert.Equal(want, got is null ? null : string.Join(',', got));
    }

    [Theory]
    [InlineData("One of: a, b")]
    [InlineData("One of: A, b.")]
    [InlineData("One of: a, a.")]
    [InlineData("One of: .")]
    public void AMalformedListFailsTheGeneration(string doc) =>
        Assert.Throws<GeneratorException>(() => Consts.ListedValues([doc]));

    private static List<string> ConstNames(string proto) =>
        Consts.Groups(Proto.Parse(proto, "test.proto"), _ => true)
            .SelectMany(g => g.Values.Select(v => g.ClassName + "." + Consts.ValueName(v)))
            .ToList();

    [Fact]
    public void ConstantsAreNamedByFieldAndValue()
    {
        var names = ConstNames("""
            message A {
                string op = 1; // One of: add, remove.
            }
            message B {
                string op = 1; // One of: add, remove.
            }
            message C {
                string op = 1; // One of: and, or.
                string mode = 2; // One of: if_new, ios.
            }
            """);
        Assert.Equal(["OpValues.Add", "OpValues.Remove", "COpValues.And", "COpValues.Or", "ModeValues.IfNew", "ModeValues.IOS"], names);
    }

    [Fact]
    public void ATieNamesEveryGroupByItsMessage()
    {
        var names = ConstNames("""
            message A {
                string op = 1; // One of: add.
            }
            message C {
                string op = 1; // One of: and.
            }
            """);
        Assert.Equal(["AOpValues.Add", "COpValues.And"], names);
    }

    // Every value the protos list has its constant, equal to it.
    [Theory]
    [InlineData("api.proto", "Centrifugal.Cent")]
    [InlineData("proxy.proto", "Centrifugal.Cent.Proxy")]
    public void EveryListedValueIsAConstant(string proto, string ns)
    {
        var s = Proto.Parse(File.ReadAllText(Path.Combine(Root(), "proto", proto)), proto);
        var groups = Consts.Groups(s, _ => true);
        Assert.NotEmpty(groups);
        foreach (var g in groups)
        {
            var type = typeof(CentClient).Assembly.GetType($"{ns}.{g.ClassName}");
            Assert.True(type is not null, $"{ns}.{g.ClassName}");
            foreach (var v in g.Values)
            {
                Assert.Equal(v, type.GetField(Consts.ValueName(v))?.GetRawConstantValue());
            }
        }
        Assert.Equal("ios", PlatformValues.IOS);
        Assert.Equal("if_new", Proxy.KeyModeValues.IfNew);
    }
}
