using System.Text;
using System.Text.RegularExpressions;

namespace Centrifugal.Cent.Generator;

/// <summary>An API method of the client, a batch and commands.</summary>
internal sealed record ApiCommand(string Method, string Request, string Result, bool ProOnly, List<string> Doc)
{
    /// <summary>The method's C# name: <c>presence_stats</c> is <c>PresenceStats</c>.</summary>
    public string Name => Names.Pascal(Method);
}

/// <summary>How a field is written in C#.</summary>
internal sealed record CsField(string Property, string Type, string? Initializer, List<string> Attributes, bool Required);

internal static partial class Names
{
    /// <summary>C# keywords and names a property cannot take.</summary>
    private static readonly HashSet<string> Reserved = ["Equals", "GetHashCode", "GetType", "ToString"];

    /// <summary><c>presence_stats</c> is <c>PresenceStats</c>; <c>b64data</c> is <c>B64Data</c>.</summary>
    public static string Pascal(string snake)
    {
        var sb = new StringBuilder();
        foreach (var part in snake.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("b64", StringComparison.Ordinal) && part.Length > 3)
            {
                sb.Append("B64").Append(char.ToUpperInvariant(part[3])).Append(part[4..]);
            }
            else
            {
                sb.Append(char.ToUpperInvariant(part[0])).Append(part[1..]);
            }
        }
        var name = sb.ToString();
        if (Reserved.Contains(name))
        {
            throw new GeneratorException($"field {snake} would be a property named {name}");
        }
        return name;
    }

    /// <summary>Proto names to C# ones: <c>RPCRequest</c> is <c>RpcRequest</c>.</summary>
    public static string Type(string proto) => proto.Replace("RPC", "Rpc", StringComparison.Ordinal);

    public static void RenameTypes(Schema s, Func<string, string> rename)
    {
        s.ByName.Clear();
        foreach (var m in s.Messages)
        {
            m.Name = rename(m.Name);
            s.ByName[m.Name] = m;
            foreach (var f in m.Fields)
            {
                if (!Proto.Scalars.Contains(f.Type))
                {
                    f.Type = rename(f.Type);
                }
            }
        }
        foreach (var rpc in s.Methods)
        {
            rpc.Request = rename(rpc.Request);
            rpc.Response = rename(rpc.Response);
        }
    }

    [GeneratedRegex(@"\bRequired\.")]
    private static partial Regex RequiredMark();

    /// <summary>
    /// A field the proto marks plainly "Required." - not "Required unless ..." or "Required
    /// when ..." - is a <c>required</c> member (spec 3.3).
    /// </summary>
    public static bool IsRequired(Field f) => RequiredMark().IsMatch(string.Join(' ', f.Doc));

    /// <summary>
    /// A required field whose comment gives its empty value a meaning after "Required." -
    /// <c>"" clears it</c>, <c>An empty map clears it.</c> It stays a <c>required</c> member,
    /// but is not checked before sending: empty is a value of it (spec 3.3).
    /// </summary>
    public static bool EmptyHasMeaning(Field f)
    {
        var doc = string.Join(' ', f.Doc);
        var at = RequiredMark().Match(doc);
        if (!at.Success)
        {
            return false;
        }
        var after = doc[(at.Index + at.Length)..];
        return after.Contains("\"\"", StringComparison.Ordinal) || after.Contains("empty", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A required field the client checks before sending (spec 3.5).</summary>
    public static bool IsChecked(Field f) => IsRequired(f) && !EmptyHasMeaning(f);
}

/// <summary>Code with indentation.</summary>
internal sealed class CodeWriter
{
    private readonly StringBuilder _sb = new();
    private int _indent;

    public CodeWriter Line(string text = "")
    {
        if (text.Length == 0)
        {
            _sb.Append('\n');
            return this;
        }
        if (text.StartsWith('}'))
        {
            _indent--;
        }
        _sb.Append(' ', _indent * 4).Append(text).Append('\n');
        if (text.EndsWith('{'))
        {
            _indent++;
        }
        return this;
    }

    /// <summary>An XML doc comment: a summary, and remarks after a blank line of the comment.</summary>
    public CodeWriter Summary(IEnumerable<string> lines, string? remarks = null)
    {
        Line("/// <summary>");
        foreach (var l in lines)
        {
            Line(l.Length == 0 ? "/// <para/>" : "/// " + Xml(l));
        }
        Line("/// </summary>");
        if (remarks is not null)
        {
            Line($"/// <remarks>{Xml(remarks)}</remarks>");
        }
        return this;
    }

    public static string Xml(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    public override string ToString() => _sb.ToString();
}

/// <summary>Types of both protos: how messages become C# classes.</summary>
internal static class Types
{
    public static string Header(string proto) => $"// <auto-generated>\n// Generated by tools/Generator from {proto}. DO NOT EDIT.\n// </auto-generated>";

    /// <summary>
    /// A message holding a single <c>value</c> field, like <c>BoolValue</c>: tri-state, absent
    /// leaving the server's setting alone (spec 2). It is a nullable of its scalar.
    /// </summary>
    public static string? WrapperScalar(Schema s, string type) =>
        s.Find(type) is { } m && m.Name.EndsWith("Value", StringComparison.Ordinal) && m.Fields is [{ Name: "value" } f]
            ? f.Type
            : null;

    /// <summary>
    /// A presence wrapper (spec 2): a message with one field which Centrifugo reads by whether
    /// it is there - a <c>*Value</c> message whose field is <c>value</c>, or one whose only
    /// field's comment gives its empty value a meaning, as <c>device_update</c>'s updates. It
    /// is sent whenever it is set, even empty; null leaves it out.
    /// </summary>
    public static bool IsPresenceWrapper(Schema s, string type) =>
        s.Find(type) is { } m && (WrapperScalar(s, type) is not null || m.Fields is [var f] && Names.EmptyHasMeaning(f));

    public static string Scalar(string type) => type switch
    {
        "string" => "string",
        "bool" => "bool",
        "int32" => "int",
        "int64" => "long",
        "uint32" => "uint",
        "uint64" => "ulong",
        "double" => "double",
        "float" => "float",
        _ => throw new GeneratorException($"not a scalar: {type}"),
    };

    /// <summary>The messages reachable from <paramref name="roots"/> through their fields.</summary>
    public static HashSet<string> Closure(Schema s, IEnumerable<string> roots)
    {
        var seen = new HashSet<string>();
        var stack = new Stack<string>(roots);
        while (stack.TryPop(out var name))
        {
            if (!seen.Add(name))
            {
                continue;
            }
            foreach (var f in s.Find(name)?.Fields ?? [])
            {
                if (s.Find(f.Type) is not null && !seen.Contains(f.Type))
                {
                    stack.Push(f.Type);
                }
            }
        }
        return seen;
    }

    /// <summary>
    /// The C# form of a field. A message built by applications and sent (an input) takes
    /// payloads as any JSON value - <c>object?</c> - and leaves unset fields null; one only
    /// received (an output) gives payloads as <c>JsonElement?</c>, and empty collections
    /// and strings rather than null (spec 3.4).
    /// </summary>
    public static CsField Field(Schema s, Message m, Field f, bool input, bool required)
    {
        var property = Names.Pascal(f.Name);
        if (property == m.Name)
        {
            throw new GeneratorException($"{m.Name}.{f.Name}: a property cannot be named as its class");
        }
        var attrs = new List<string> { $"[JsonPropertyName(\"{f.Name}\")]" };
        string Element(string type)
        {
            if (type == "bytes")
            {
                throw new GeneratorException($"{m.Name}.{f.Name}: a list or map of payloads is not supported");
            }
            if (Proto.Scalars.Contains(type))
            {
                return Scalar(type);
            }
            if (WrapperScalar(s, type) is not null)
            {
                throw new GeneratorException($"{m.Name}.{f.Name}: a list or map of {type} is not supported");
            }
            return type;
        }

        if (f.MapKey is not null)
        {
            var v = Element(f.Type);
            return input
                ? new CsField(property, required ? $"IDictionary<string, {v}>" : $"IDictionary<string, {v}>?", null, attrs, required)
                : new CsField(property, $"IReadOnlyDictionary<string, {v}>", $"ReadOnlyDictionary<string, {v}>.Empty", attrs, false);
        }
        if (f.Repeated)
        {
            var v = Element(f.Type);
            return input
                ? new CsField(property, required ? $"IList<{v}>" : $"IList<{v}>?", null, attrs, required)
                : new CsField(property, $"IReadOnlyList<{v}>", "[]", attrs, false);
        }
        if (f.Type == "bytes")
        {
            if (input)
            {
                attrs.Add("[JsonConverter(typeof(PayloadConverter))]");
                return new CsField(property, required ? "object" : "object?", null, attrs, required);
            }
            attrs.Add("[JsonConverter(typeof(ReplyPayloadConverter))]");
            return new CsField(property, "JsonElement?", null, attrs, false);
        }
        if (f.Type == "string")
        {
            return input
                ? new CsField(property, required ? "string" : "string?", null, attrs, required)
                : new CsField(property, "string", "\"\"", attrs, false);
        }
        if (Proto.Scalars.Contains(f.Type))
        {
            return new CsField(property, Scalar(f.Type), null, attrs, required && input);
        }
        if (WrapperScalar(s, f.Type) is { } inner)
        {
            attrs.Add($"[JsonConverter(typeof(WrapperConverter<{Scalar(inner)}>))]");
            return new CsField(property, Scalar(inner) + "?", null, attrs, false);
        }
        return new CsField(property, required && input ? f.Type : f.Type + "?", null, attrs, required && input);
    }

    /// <summary>The documentation of a field: its comment, with its first word its C# name.</summary>
    public static List<string> FieldDoc(Field f)
    {
        if (f.Doc.Count == 0)
        {
            return [$"The {f.Name} field."];
        }
        var lines = new List<string>(f.Doc);
        // "channel is the name..." reads "Channel is the name...".
        if (lines[0].StartsWith(f.Name + " ", StringComparison.Ordinal))
        {
            lines[0] = Names.Pascal(f.Name) + lines[0][f.Name.Length..];
        }
        else if (lines[0].Length > 0)
        {
            lines[0] = char.ToUpperInvariant(lines[0][0]) + lines[0][1..];
        }
        return lines;
    }

    public static List<string> MessageDoc(Message m, string fallback) => m.Doc.Count == 0 ? [fallback] : m.Doc;

    /// <summary>
    /// Writes a message as a sealed class: settable when it is sent, init-only when received.
    /// One which is sent says when it is zero - left out of the message holding it - and
    /// which required field it leaves unset.
    /// </summary>
    public static void Emit(CodeWriter w, Schema s, Message m, bool input, string fallback, SortedSet<string>? required, Checks? checks = null)
    {
        var name = m.Name;
        w.Line();
        w.Summary(MessageDoc(m, fallback));
        w.Line(input ? $"public sealed partial class {name} : IWireMessage" : $"public sealed partial class {name}");
        w.Line("{");
        var first = true;
        foreach (var f in m.Fields)
        {
            var isRequired = input && Names.IsRequired(f);
            if (isRequired)
            {
                required?.Add($"{m.Name}.{f.Name}");
            }
            var cs = Field(s, m, f, input, isRequired);
            if (!first)
            {
                w.Line();
            }
            first = false;
            w.Summary(FieldDoc(f));
            foreach (var a in cs.Attributes)
            {
                w.Line(a);
            }
            // A received field read as null - a server between Centrifugo and the SDK may write
            // zero values so - keeps its zero value: an empty string or list, never null.
            var accessor = input ? "{ get; set; }"
                : cs.Initializer is null ? "{ get; init; }"
                : $"{{ get; init => field = value ?? {cs.Initializer}; }}";
            var init = cs.Initializer is null ? "" : $" = {cs.Initializer};";
            w.Line($"public {(cs.Required ? "required " : "")}{cs.Type} {cs.Property} {accessor}{init}");
        }
        if (input)
        {
            EmitWire(w, s, m, checks ?? new Checks(s));
        }
        w.Line("}");
    }

    /// <summary>The C# condition true when a field of a message which is sent is at its zero value.</summary>
    private static string ZeroCheck(Schema s, Field f)
    {
        var p = Names.Pascal(f.Name);
        if (f.Repeated || f.MapKey is not null)
        {
            return $"{p} is not {{ Count: > 0 }}";
        }
        if (f.Type == "string")
        {
            return $"string.IsNullOrEmpty({p})";
        }
        if (f.Type == "bool")
        {
            return $"!{p}";
        }
        if (f.Type == "bytes" || WrapperScalar(s, f.Type) is not null)
        {
            return $"{p} is null";
        }
        if (Proto.Scalars.Contains(f.Type))
        {
            return $"{p} == 0";
        }
        return $"WireMessage.IsZero({p})";
    }

    /// <summary>Writes <c>IWireMessage</c>'s members: when the message is zero, and the path of a required field it leaves unset.</summary>
    private static void EmitWire(CodeWriter w, Schema s, Message m, Checks checks)
    {
        w.Line();
        if (IsPresenceWrapper(s, m.Name))
        {
            w.Line("// A presence wrapper: sent whenever it is set, even empty (spec 2).");
            w.Line("bool IWireMessage.IsZero => false;");
        }
        else if (m.Fields.Count == 0)
        {
            w.Line("bool IWireMessage.IsZero => true;");
        }
        else
        {
            var conds = m.Fields.Select(f => ZeroCheck(s, f)).ToList();
            w.Line("bool IWireMessage.IsZero =>");
            for (var i = 0; i < conds.Count; i++)
            {
                w.Line($"    {conds[i]}{(i == conds.Count - 1 ? ";" : " &&")}");
            }
        }
        w.Line();
        if (!checks.Needs(m.Name))
        {
            w.Line("string? IWireMessage.Missing() => null;");
            return;
        }
        w.Line("string? IWireMessage.Missing()");
        w.Line("{");
        foreach (var f in m.Fields)
        {
            var p = Names.Pascal(f.Name);
            if (Names.IsChecked(f))
            {
                if (f.Type == "bool" && !f.Repeated && f.MapKey is null)
                {
                    throw new GeneratorException($"{m.Name}.{f.Name}: a bool cannot be required: false and unset are one value");
                }
                w.Line($"if ({ZeroCheck(s, f)})");
                w.Line("{");
                w.Line($"return \"{p}\";");
                w.Line("}");
            }
            if (s.Find(f.Type) is null || IsPresenceWrapper(s, f.Type) || !checks.Needs(f.Type))
            {
                continue;
            }
            if (f.MapKey is not null)
            {
                throw new GeneratorException($"{m.Name}.{f.Name}: a map of messages with required fields is not supported");
            }
            if (f.Repeated)
            {
                w.Line($"if (WireMessage.MissingIn({p}) is {{ }} {Local(f)})");
                w.Line("{");
                w.Line($"return \"{p}\" + {Local(f)};");
                w.Line("}");
                continue;
            }
            w.Line($"if (WireMessage.Missing({p}) is {{ }} {Local(f)})");
            w.Line("{");
            w.Line($"return \"{p}.\" + {Local(f)};");
            w.Line("}");
        }
        w.Line("return null;");
        w.Line("}");
    }

    private static string Local(Field f) => "missing" + Names.Pascal(f.Name);
}

/// <summary>
/// The messages whose required fields the client checks before sending (spec 3.5): those
/// with required fields of their own, or holding such a message - directly or in a list.
/// </summary>
internal sealed class Checks(Schema s)
{
    private readonly Dictionary<string, bool> _need = [];

    public bool Needs(string name)
    {
        if (_need.TryGetValue(name, out var v))
        {
            return v;
        }
        _need[name] = false; // a message holding itself checks nothing more
        var m = s.Find(name)!;
        v = m.Fields.Any(f => Names.IsChecked(f)
            || s.Find(f.Type) is not null && !Types.IsPresenceWrapper(s, f.Type) && Needs(f.Type));
        _need[name] = v;
        return v;
    }
}

/// <summary>The API client's types, methods and tests, from api.proto.</summary>
internal static class ApiEmitter
{
    /// <summary>
    /// API commands the SDK does not offer: <c>rpc</c> calls server API extensions, which
    /// Centrifugo does not provide (spec 1).
    /// </summary>
    private static readonly string[] OmittedCommands = ["rpc"];

    /// <summary>The messages of a batch, naming a command's request and reply types.</summary>
    private static readonly string[] Envelopes = ["Command", "Reply"];

    /// <summary>Requests with rules the client checks before sending (spec 3.5): <c>Validation.{Name}</c>.</summary>
    private static readonly string[] Validated = ["publish", "broadcast"];

    /// <summary>
    /// Messages written by hand, or only used on the wire, where the SDK has its own form
    /// of them.
    /// </summary>
    private static readonly string[] Handwritten = ["Error", "BroadcastResult", "Command", "Reply", "BatchRequest", "BatchResponse"];

    public static List<ApiCommand> Commands(Schema s)
    {
        if (s.Find("Command") is not { } cmd || s.Find("Reply") is not { } reply)
        {
            throw new GeneratorException("api.proto: Command and Reply messages are required");
        }
        var list = new List<ApiCommand>();
        foreach (var f in cmd.Fields)
        {
            if (OmittedCommands.Contains(f.Name))
            {
                continue;
            }
            var result = reply.Fields.FirstOrDefault(r => r.Name == f.Name)
                ?? throw new GeneratorException($"api.proto: Command.{f.Name} has no Reply.{f.Name}");
            var request = s.Find(f.Type) ?? throw new GeneratorException($"api.proto: Command.{f.Name} has no request message");
            list.Add(new ApiCommand(f.Name, f.Type, result.Type, string.Join(' ', f.Doc).Contains("Centrifugo PRO only", StringComparison.Ordinal), request.Doc));
        }
        if (list.All(c => c.Method != "broadcast"))
        {
            throw new GeneratorException("api.proto: no broadcast command");
        }
        return list;
    }

    /// <summary>The method's summary from its request's comment: "PublishRequest publishes data into a channel." becomes "Publishes data into a channel."</summary>
    private static List<string> MethodDoc(ApiCommand c)
    {
        var lines = new List<string>(c.Doc);
        if (lines.Count > 0 && lines[0].StartsWith(c.Request + " ", StringComparison.Ordinal))
        {
            var rest = lines[0][(c.Request.Length + 1)..];
            lines[0] = char.ToUpperInvariant(rest[0]) + rest[1..];
            return lines;
        }
        return [$"Calls the {c.Method} method of the Centrifugo server API."];
    }

    private static string? ProRemark(ApiCommand c) =>
        c.ProOnly ? "Centrifugo PRO only: Centrifugo OSS answers with HTTP status 404, or with error code 104 in a batch." : null;

    private static void Prepare(Schema s) => Names.RenameTypes(s, Names.Type);

    /// <summary>Whether a message of api.proto is a generated type: not one written by hand, an envelope, an omitted command's or a wrapper.</summary>
    private static bool Emitted(Schema s, string name)
    {
        if (Handwritten.Contains(name) || s.Methods.Any(m => m.Response == name) || Cent.Generator.Types.WrapperScalar(s, name) is not null)
        {
            return false;
        }
        return !Envelopes.Any(c => s.Find(c)!.Fields.Any(f => OmittedCommands.Contains(f.Name) && f.Type == name));
    }

    public static string Types(Schema s, SortedSet<string> required, Pins pins)
    {
        Prepare(s);
        var cmds = Commands(s);
        var inputs = Cent.Generator.Types.Closure(s, cmds.Select(c => c.Request));
        var outputs = Cent.Generator.Types.Closure(s, cmds.Select(c => c.Result));
        var checks = new Checks(s);
        var w = new CodeWriter();
        w.Line(Cent.Generator.Types.Header("proto/api.proto"));
        w.Line();
        w.Line("#nullable enable");
        w.Line();
        w.Line("using System.Collections.Generic;");
        w.Line("using System.Collections.ObjectModel;");
        w.Line("using System.Text.Json;");
        w.Line("using System.Text.Json.Serialization;");
        w.Line();
        w.Line("namespace Centrifugal.Cent;");
        foreach (var m in s.Messages)
        {
            if (!Emitted(s, m.Name))
            {
                continue;
            }
            var input = inputs.Contains(m.Name);
            if (input && outputs.Contains(m.Name) && m.Fields.Any(f => f.Type == "bytes"))
            {
                throw new GeneratorException($"api.proto: {m.Name} is both sent and received and has a payload: decide its type");
            }
            Cent.Generator.Types.Emit(w, s, m, input, $"The {m.Name} message of the Centrifugo server API.", required, checks);
        }
        try
        {
            Consts.Emit(w, s, n => Emitted(s, n), pins);
        }
        catch (GeneratorException e)
        {
            throw new GeneratorException("api.proto: " + e.Message);
        }
        return w.ToString();
    }

    public static string Methods(Schema s)
    {
        var cmds = Commands(s);
        var w = new CodeWriter();
        w.Line(Cent.Generator.Types.Header("proto/api.proto"));
        w.Line();
        w.Line("#nullable enable");
        w.Line();
        w.Line("using System.Collections.Frozen;");
        w.Line("using System.Collections.Generic;");
        w.Line("using System.Text.Json.Serialization;");
        w.Line("using System.Text.Json.Serialization.Metadata;");
        w.Line("using System.Threading;");
        w.Line("using System.Threading.Tasks;");
        w.Line();
        w.Line("namespace Centrifugal.Cent;");

        // The JSON metadata of every request and result, generated by System.Text.Json.
        w.Line();
        foreach (var c in cmds)
        {
            w.Line($"[JsonSerializable(typeof({c.Request}))]");
            if (c.Method != "broadcast")
            {
                w.Line($"[JsonSerializable(typeof({c.Result}))]");
            }
        }
        w.Line("internal sealed partial class ApiJsonContext : JsonSerializerContext;");

        w.Line();
        w.Line("/// <summary>Every API method of the SDK, with its request's and result's JSON.</summary>");
        w.Line("internal static class ApiMethods");
        w.Line("{");
        foreach (var c in cmds)
        {
            var result = c.Method == "broadcast" ? "BroadcastWire" : c.Result;
            var validate = Validated.Contains(c.Method) ? $"Validation.{c.Name}" : "null";
            w.Line($"internal static readonly ApiMethod<{c.Request}, {result}> {c.Name} = new(\"{c.Method}\", {(c.ProOnly ? "true" : "false")}, Wire.Info<{c.Request}>(), Wire.Info<{result}>(), {validate});");
        }
        w.Line();
        w.Line("/// <summary>Every API method's name, <c>batch</c> included, and whether only Centrifugo PRO has it.</summary>");
        w.Line("internal static readonly FrozenDictionary<string, bool> ProOnly = new Dictionary<string, bool>");
        w.Line("{");
        w.Line("[\"batch\"] = false,");
        foreach (var c in cmds)
        {
            w.Line($"[\"{c.Method}\"] = {(c.ProOnly ? "true" : "false")},");
        }
        w.Line("}.ToFrozenDictionary();");
        w.Line("}");

        w.Line();
        w.Line("public partial class CentClient");
        w.Line("{");
        var firstMethod = true;
        foreach (var c in cmds.Where(c => c.Method != "broadcast"))
        {
            if (!firstMethod)
            {
                w.Line();
            }
            firstMethod = false;
            w.Summary(MethodDoc(c), ProRemark(c));
            w.Line("/// <param name=\"request\">The request.</param>");
            w.Line("/// <param name=\"cancellationToken\">Cancels the call: it then throws <see cref=\"System.OperationCanceledException\"/>.</param>");
            w.Line("/// <returns>The method's result.</returns>");
            w.Line("/// <exception cref=\"CentException\">The call did not succeed: see the exception's type.</exception>");
            w.Line($"public virtual Task<{c.Result}> {c.Name}Async({c.Request} request, CancellationToken cancellationToken = default) =>");
            w.Line($"    CallAsync(ApiMethods.{c.Name}, request, cancellationToken);");
        }
        w.Line("}");

        w.Line();
        w.Line("public partial class CentBatch");
        w.Line("{");
        firstMethod = true;
        foreach (var c in cmds.Where(c => c.Method != "broadcast"))
        {
            if (!firstMethod)
            {
                w.Line();
            }
            firstMethod = false;
            w.Summary(["Adds a command: " + LowerFirst(MethodDoc(c)[0])], ProRemark(c));
            w.Line("/// <param name=\"request\">The command's request.</param>");
            w.Line("/// <returns>The command's outcome, once the batch is sent.</returns>");
            w.Line($"public virtual Pending<{c.Result}> {c.Name}({c.Request} request) => Add(ApiMethods.{c.Name}, request);");
        }
        w.Line("}");

        w.Line();
        w.Line("public sealed partial class Command");
        w.Line("{");
        firstMethod = true;
        foreach (var c in cmds)
        {
            if (!firstMethod)
            {
                w.Line();
            }
            firstMethod = false;
            w.Summary(["A command for async consumers: " + LowerFirst(MethodDoc(c)[0])], ProRemark(c));
            w.Line("/// <param name=\"request\">The command's request.</param>");
            w.Line("/// <returns>The command, to write to a consumer's queue or table.</returns>");
            w.Line("/// <exception cref=\"CentInvalidRequestException\">The request cannot be sent as it is.</exception>");
            w.Line($"public static Command {c.Name}({c.Request} request) => Create(ApiMethods.{c.Name}, request);");
        }
        w.Line("}");
        return w.ToString();
    }

    private static string LowerFirst(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    /// <summary>
    /// A request with every field set, for the tests: so that the required-field checks let
    /// it through, nested ones included (spec 15). A message holding itself is not filled
    /// below its first level.
    /// </summary>
    private static string Dummy(Schema s, string type, HashSet<string>? filling = null)
    {
        filling ??= [];
        filling.Add(type);
        var m = s.Find(type)!;
        var parts = new List<string>();
        foreach (var f in m.Fields)
        {
            if (filling.Contains(f.Type))
            {
                continue;
            }
            parts.Add($"{Names.Pascal(f.Name)} = {DummyValue(s, f, filling)}");
        }
        filling.Remove(type);
        return parts.Count == 0 ? $"new {type}()" : $"new {type} {{ {string.Join(", ", parts)} }}";
    }

    private static string DummyValue(Schema s, Field f, HashSet<string> filling)
    {
        string One(string type) => type switch
        {
            "string" => "\"x\"",
            "bool" => "true",
            "bytes" => "RawJson.Parse(\"{}\")",
            "int32" or "int64" or "uint32" or "uint64" or "double" or "float" => "1",
            _ when Cent.Generator.Types.WrapperScalar(s, type) is { } inner => One(inner),
            _ => Dummy(s, type, filling),
        };
        if (f.MapKey is not null)
        {
            return $"new Dictionary<string, {(Proto.Scalars.Contains(f.Type) ? Cent.Generator.Types.Scalar(f.Type) : f.Type)}> {{ [\"x\"] = {One(f.Type)} }}";
        }
        return f.Repeated ? $"[{One(f.Type)}]" : One(f.Type);
    }

    public static string Tests(Schema s)
    {
        var cmds = Commands(s);
        var w = new CodeWriter();
        w.Line(Cent.Generator.Types.Header("proto/api.proto"));
        w.Line();
        w.Line("#nullable enable");
        w.Line();
        w.Line("using System.Collections.Generic;");
        w.Line("using System.Threading.Tasks;");
        w.Line("using Xunit;");
        w.Line();
        w.Line("namespace Centrifugal.Cent.Tests;");
        w.Line();
        w.Line("/// <summary>Every API method reaches its endpoint, its batch command and its consumer command.</summary>");
        w.Line("public sealed class GeneratedMethodsTests");
        w.Line("{");
        w.Line("private static readonly string[] Methods =");
        w.Line("[");
        foreach (var c in cmds)
        {
            w.Line($"\"{c.Method}\",");
        }
        w.Line("];");
        w.Line();
        w.Line("[Fact]");
        w.Line("public async Task EveryMethodReachesItsEndpoint()");
        w.Line("{");
        w.Line("var fake = new FakeCentrifugo();");
        w.Line("await using var client = fake.CreateClient();");
        w.Line("var ct = TestContext.Current.CancellationToken;");
        foreach (var c in cmds)
        {
            w.Line($"await client.{c.Name}Async({Dummy(s, c.Request)}, ct);");
        }
        w.Line("Assert.Equal(Methods, fake.Paths.ConvertAll(p => p[\"/api/\".Length..]));");
        w.Line("}");
        w.Line();
        w.Line("[Fact]");
        w.Line("public async Task EveryMethodHasABatchCommand()");
        w.Line("{");
        w.Line("var fake = new FakeCentrifugo();");
        w.Line("await using var client = fake.CreateClient();");
        w.Line("var batch = client.NewBatch();");
        w.Line("var pending = new List<IPending>");
        w.Line("{");
        foreach (var c in cmds)
        {
            w.Line($"batch.{c.Name}({Dummy(s, c.Request)}),");
        }
        w.Line("};");
        w.Line("await batch.SendAsync(TestContext.Current.CancellationToken);");
        w.Line("Assert.Equal([\"/api/batch\"], fake.Paths);");
        w.Line("Assert.Equal(Methods, fake.Requests[0].BatchMethods());");
        w.Line("Assert.All(pending, p => Assert.Null(p.Error));");
        w.Line("}");
        w.Line();
        w.Line("[Fact]");
        w.Line("public void EveryMethodHasACommand()");
        w.Line("{");
        w.Line("var commands = new List<Command>");
        w.Line("{");
        foreach (var c in cmds)
        {
            w.Line($"Command.{c.Name}({Dummy(s, c.Request)}),");
        }
        w.Line("};");
        w.Line("Assert.Equal(Methods, commands.ConvertAll(c => c.Method));");
        w.Line("}");
        w.Line("}");
        return w.ToString();
    }
}

/// <summary>The proxy's types and handlers, from proxy.proto.</summary>
internal static class ProxyEmitter
{
    /// <summary>Which answers a proxy type's response carries: those Centrifugo acts on (spec 14).</summary>
    private static readonly Dictionary<string, string> Answers = new()
    {
        ["Connect"] = "All",
        ["Subscribe"] = "All",
        ["Publish"] = "All",
        ["RPC"] = "All",
        ["MapPublish"] = "All",
        ["MapRemove"] = "All",
        ["SharedPollRefresh"] = "ErrorOrResult",
        ["NotifyChannelState"] = "ErrorOrResult",
        ["Refresh"] = "ResultOnly",
        ["SubRefresh"] = "ResultOnly",
        ["NotifyCacheEmpty"] = "ResultOnly",
    };

    /// <summary>Proxy messages renamed in C#: <c>Error</c> would read as any error.</summary>
    private static readonly Dictionary<string, string> Renamed = new() { ["Error"] = "ProxyError" };

    public static string Generate(Schema s, Pins pins)
    {
        var methods = s.Methods.Where(m => !m.Streaming).ToList();
        foreach (var m in methods)
        {
            if (!Answers.ContainsKey(m.Name))
            {
                throw new GeneratorException($"proxy.proto: new proxy method {m.Name}: decide which answers it takes (spec 14)");
            }
        }
        Names.RenameTypes(s, n => Renamed.GetValueOrDefault(n, Names.Type(n)));
        // A response is {result, error, disconnect}: the SDK writes it by hand.
        var results = new Dictionary<string, string>();
        foreach (var m in methods)
        {
            var response = s.Find(m.Response)!;
            var result = response.Fields.FirstOrDefault(f => f.Name == "result")
                ?? throw new GeneratorException($"proxy.proto: {m.Response} has no result");
            results[m.Name] = result.Type;
        }
        var requests = methods.Select(m => m.Request).ToHashSet();
        var outputs = Cent.Generator.Types.Closure(s, requests);
        var inputs = Cent.Generator.Types.Closure(s, results.Values.Concat(["ProxyError", "Disconnect"]));
        var used = outputs.Union(inputs).ToHashSet();
        var responses = methods.Select(m => m.Response).ToHashSet();

        var w = new CodeWriter();
        w.Line(Cent.Generator.Types.Header("proto/proxy.proto"));
        w.Line();
        w.Line("#nullable enable");
        w.Line();
        w.Line("using System;");
        w.Line("using System.Collections.Generic;");
        w.Line("using System.Collections.ObjectModel;");
        w.Line("using System.Text.Json;");
        w.Line("using System.Text.Json.Serialization;");
        w.Line("using System.Threading;");
        w.Line("using System.Threading.Tasks;");
        w.Line();
        w.Line("namespace Centrifugal.Cent.Proxy;");
        foreach (var m in s.Messages)
        {
            if (!used.Contains(m.Name) || responses.Contains(m.Name) || Cent.Generator.Types.WrapperScalar(s, m.Name) is not null)
            {
                continue;
            }
            var input = inputs.Contains(m.Name);
            if (input && outputs.Contains(m.Name))
            {
                throw new GeneratorException($"proxy.proto: {m.Name} is both received and answered: decide its type");
            }
            if (m.Fields.Any(Names.IsRequired))
            {
                throw new GeneratorException($"proxy.proto marks fields of {m.Name} Required.: decide how the proxy types take them");
            }
            Cent.Generator.Types.Emit(w, s, m, input, $"The {m.Name} message of the Centrifugo proxy.", null);
        }

        // The values of the string fields with a fixed set: what a handler compares a
        // request's fields with, or sets in a result.
        try
        {
            Consts.Emit(w, s, n => used.Contains(n) && !responses.Contains(n) && Cent.Generator.Types.WrapperScalar(s, n) is null, pins);
        }
        catch (GeneratorException e)
        {
            throw new GeneratorException("proxy.proto: " + e.Message);
        }

        w.Line();
        foreach (var m in methods)
        {
            w.Line($"[JsonSerializable(typeof({m.Request}))]");
            w.Line($"[JsonSerializable(typeof({results[m.Name]}))]");
        }
        w.Line("[JsonSerializable(typeof(ProxyError))]");
        w.Line("[JsonSerializable(typeof(Disconnect))]");
        w.Line("internal sealed partial class ProxyJsonContext : JsonSerializerContext;");

        w.Line();
        w.Line("public sealed partial class ProxyHandlers");
        w.Line("{");
        var first = true;
        foreach (var m in methods)
        {
            if (!first)
            {
                w.Line();
            }
            first = false;
            var answers = Answers[m.Name];
            var lines = m.Doc.Count > 0 ? new List<string>(m.Doc) : [$"The {m.Name} proxy."];
            if (lines[0].StartsWith(m.Name + " ", StringComparison.Ordinal))
            {
                lines[0] = "Handles the " + m.Name + " proxy, which " + lines[0][(m.Name.Length + 1)..];
            }
            w.Summary(lines, answers switch
            {
                "All" => "The function returns a result, or throws ProxyErrorException to answer with an error or ProxyDisconnectException to close the connection.",
                "ErrorOrResult" => "The function returns a result, or throws ProxyErrorException to answer with an error.",
                _ => "The function returns a result: this proxy cannot answer with an error or a disconnect.",
            });
            w.Line("/// <param name=\"handle\">Decides the answer: called with the request and its context.</param>");
            w.Line("/// <returns>The handler, to serve with the framework's adapter.</returns>");
            w.Line($"public CentHandler {Names.Type(m.Name)}(Func<{m.Request}, HandlerContext, Task<{results[m.Name]}>> handle) =>");
            w.Line($"    Create(\"{Snake(m.Name)}\", Answers.{answers}, handle, Wire.Info<{m.Request}>(), Wire.Info<{results[m.Name]}>());");
        }
        w.Line("}");
        return w.ToString();
    }

    private static string Snake(string pascal) =>
        pascal == "RPC" ? "rpc" : Regex.Replace(pascal, "(?<=[a-z])([A-Z])", "_$1").ToLowerInvariant();
}

/// <summary>
/// The constants of the fields one pinned name covers: the values they list, and the
/// messages holding them.
/// </summary>
internal sealed class ConstGroup(string name, string field, List<string> values)
{
    /// <summary>The pinned name, such as <c>Op</c>.</summary>
    public string Name { get; } = name;

    /// <summary>A field's proto name, for the doc comment.</summary>
    public string Field { get; } = field;

    public List<string> Values { get; } = values;
    public List<string> Messages { get; } = [];

    /// <summary>The static class holding the constants: <c>OpValues</c>, <c>FilterNodeOpValues</c>.</summary>
    public string ClassName => Name + "Values";
}

/// <summary>
/// The constant names spec 3.3 pins: by <c>Message.field</c>, or by <c>*.field</c> for the
/// field in every message which has it - <c>tools/Generator/constants_api.txt</c> and
/// <c>constants_proxy.txt</c>.
/// </summary>
internal sealed class Pins
{
    private readonly Dictionary<string, string> _names = [];
    private readonly HashSet<string> _used = [];
    private readonly List<string> _order = [];

    public Pins(string file, string text)
    {
        File = file;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                throw new GeneratorException($"{file}: \"{line}\" is not a selector and a name");
            }
            if (!_names.TryAdd(parts[0], parts[1]))
            {
                throw new GeneratorException($"{file}: {parts[0]} pinned twice");
            }
            _order.Add(parts[0]);
        }
    }

    public string File { get; }

    /// <summary>The pinned name of a message's field, or null.</summary>
    public string? Name(string message, string field)
    {
        foreach (var selector in new[] { message + "." + field, "*." + field })
        {
            if (_names.TryGetValue(selector, out var name))
            {
                _used.Add(selector);
                return name;
            }
        }
        return null;
    }

    /// <summary>The selectors no field used.</summary>
    public IEnumerable<string> Unused => _order.Where(s => !_used.Contains(s));
}

/// <summary>
/// Constants for the string fields whose comments list their values, in the sentence
/// <c>One of: add, remove, set.</c>, named as spec 3.3 pins them.
/// </summary>
internal static partial class Consts
{
    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex ValuePattern();

    /// <summary>How a value is spelled where Pascal case alone reads wrong, as .NET's own names do.</summary>
    private static readonly Dictionary<string, string> Spellings = new()
    {
        ["ios"] = "IOS",
        ["webpush"] = "WebPush",
    };

    /// <summary>
    /// The values a field's comment lists after "One of:", up to the period, or null without
    /// the sentence. A malformed one fails the generation rather than being skipped.
    /// </summary>
    public static List<string>? ListedValues(List<string> doc)
    {
        var text = string.Join(' ', doc);
        var at = text.IndexOf("One of:", StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }
        var after = text[(at + "One of:".Length)..];
        var end = after.IndexOf('.', StringComparison.Ordinal);
        if (end < 0)
        {
            throw new GeneratorException("the values after \"One of:\" must end with a period");
        }
        var values = new List<string>();
        foreach (var raw in after[..end].Split(','))
        {
            var v = raw.Trim();
            if (!ValuePattern().IsMatch(v))
            {
                throw new GeneratorException($"\"{v}\" is not a value: lowercase letters, digits and underscores");
            }
            if (values.Contains(v))
            {
                throw new GeneratorException($"value \"{v}\" listed twice");
            }
            values.Add(v);
        }
        return values;
    }

    /// <summary>A value's constant: <c>if_new</c> is <c>IfNew</c>, <c>ios</c> is <c>IOS</c>.</summary>
    public static string ValueName(string value) => Spellings.GetValueOrDefault(value) ?? Names.Pascal(value);

    /// <summary>
    /// The fields with listed values of the messages <paramref name="include"/> accepts, in
    /// groups by their pinned names, in the order of the proto. A field without a pinned
    /// name, a pin no field uses, and one name over different values all fail the
    /// generation: a person then names the field in spec 3.3's table and the list.
    /// </summary>
    public static List<ConstGroup> Groups(Schema s, Func<string, bool> include, Pins pins)
    {
        var byName = new Dictionary<string, ConstGroup>();
        var result = new List<ConstGroup>();
        foreach (var m in s.Messages.Where(m => include(m.Name)))
        {
            foreach (var f in m.Fields)
            {
                List<string>? values;
                try
                {
                    values = ListedValues(f.Doc);
                }
                catch (GeneratorException e)
                {
                    throw new GeneratorException($"{m.Name}.{f.Name}: {e.Message}");
                }
                if (values is null)
                {
                    continue;
                }
                if (f.Type != "string")
                {
                    throw new GeneratorException($"{m.Name}.{f.Name}: values are listed for strings only");
                }
                var name = pins.Name(m.Name, f.Name)
                    ?? throw new GeneratorException($"{m.Name}.{f.Name} lists its values but has no pinned name: name it in {pins.File} and in spec.md section 3.3's table");
                if (!byName.TryGetValue(name, out var g))
                {
                    byName[name] = g = new ConstGroup(name, f.Name, values);
                    result.Add(g);
                }
                else if (!g.Values.SequenceEqual(values))
                {
                    throw new GeneratorException($"{m.Name}.{f.Name} lists {string.Join(", ", values)}, but {name} names {string.Join(", ", g.Values)} in {string.Join(", ", g.Messages)}");
                }
                g.Messages.Add(m.Name);
            }
        }
        if (pins.Unused.FirstOrDefault() is { } unused)
        {
            throw new GeneratorException($"{pins.File}: {unused} names no field with listed values: remove it, and from spec.md section 3.3's table");
        }
        return result;
    }

    /// <summary>Writes a static class of constants per group.</summary>
    public static void Emit(CodeWriter w, Schema s, Func<string, bool> include, Pins pins)
    {
        foreach (var g in Groups(s, include, pins))
        {
            var where = g.Messages.Count == 1 ? g.Messages[0] : string.Join(", ", g.Messages[..^1]) + " and " + g.Messages[^1];
            w.Line();
            w.Summary(
                Wrap($"The values of the {g.Field} field of {where}. The field is a string: a value Centrifugo sends or accepts which these do not name is still a valid one."));
            w.Line($"public static class {g.ClassName}");
            w.Line("{");
            for (var i = 0; i < g.Values.Count; i++)
            {
                if (i > 0)
                {
                    w.Line();
                }
                w.Line($"/// <summary><c>{g.Values[i]}</c>.</summary>");
                w.Line($"public const string {ValueName(g.Values[i])} = \"{g.Values[i]}\";");
            }
            w.Line("}");
        }
    }

    private static List<string> Wrap(string text)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' '))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > 88)
            {
                lines.Add(line);
                line = word;
            }
            else
            {
                line = line.Length == 0 ? word : line + " " + word;
            }
        }
        lines.Add(line);
        return lines;
    }
}

/// <summary>
/// What a person reviews before it changes, because a change breaks applications: the
/// required fields, as <c>Message.field</c>.
/// </summary>
internal static class Reviewed
{
    public static void Check(string path, SortedSet<string> got, bool accept)
    {
        if (accept)
        {
            File.WriteAllText(path, string.Concat(got.Select(f => f + "\n")));
            return;
        }
        var have = new SortedSet<string>(
            File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0),
            StringComparer.Ordinal);
        if (have.SetEquals(got))
        {
            return;
        }
        var msg = new StringBuilder("the fields the protos mark Required. changed:");
        var added = got.Except(have).ToList();
        var removed = have.Except(got).ToList();
        if (added.Count > 0)
        {
            msg.Append("\n  new (breaking for callers): ").AppendJoin(", ", added);
        }
        if (removed.Count > 0)
        {
            msg.Append("\n  gone: ").AppendJoin(", ", removed);
        }
        msg.Append("\nReview them in tools/Generator/required_fields.txt - fix the proto upstream if Centrifugo accepts a field absent - then run dotnet run --project tools/Generator -- --accept-required");
        throw new GeneratorException(msg.ToString());
    }
}
