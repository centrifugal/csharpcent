using System.Text.RegularExpressions;

namespace Centrifugal.Cent.Generator;

/// <summary>
/// What the generator needs from a proto file: its messages and the methods of
/// its service, each with the comment written above it.
/// </summary>
/// <remarks>
/// The parser accepts exactly the part of proto3 that Centrifugo's
/// <c>api.proto</c> and <c>proxy.proto</c> use - top-level messages with
/// scalar, message, repeated and map fields, and one service whose methods may
/// stream - and rejects anything else, so that a construct it does not
/// understand (a oneof, an enum, a nested message) fails the generation rather
/// than being dropped from the SDK without a word. It is a port of gocent's
/// <c>internal/gen/proto.go</c>, which reads the same files.
/// </remarks>
internal sealed class Schema
{
    public List<Message> Messages { get; } = [];
    public Dictionary<string, Message> ByName { get; } = [];
    public List<Method> Methods { get; } = [];

    public Message? Find(string name) => ByName.GetValueOrDefault(name);
}

internal sealed class Message(string name, List<string> doc)
{
    public string Name { get; set; } = name;
    public List<string> Doc { get; } = doc;
    public List<Field> Fields { get; } = [];
}

internal sealed class Field(string name, string type, bool repeated, string? mapKey, List<string> doc)
{
    /// <summary>As in the proto: snake case.</summary>
    public string Name { get; } = name;

    /// <summary>The scalar or message type; the value type for maps.</summary>
    public string Type { get; set; } = type;

    public bool Repeated { get; } = repeated;
    public string? MapKey { get; } = mapKey;
    public List<string> Doc { get; } = doc;
}

internal sealed class Method(string name, string request, string response, bool streaming, List<string> doc)
{
    public string Name { get; } = name;
    public string Request { get; set; } = request;
    public string Response { get; set; } = response;

    /// <summary>A method streaming its request or response: one only GRPC can call.</summary>
    public bool Streaming { get; } = streaming;

    public List<string> Doc { get; } = doc;
}

internal static partial class Proto
{
    public static readonly string[] Scalars =
        ["string", "bool", "int32", "int64", "uint32", "uint64", "double", "float", "bytes"];

    [GeneratedRegex(@"^message\s+(\w+)\s*\{\s*(\})?$")]
    private static partial Regex MessageOpen();

    [GeneratedRegex(@"^(repeated\s+)?(\w+)\s+(\w+)\s*=\s*\d+\s*;$")]
    private static partial Regex FieldLine();

    [GeneratedRegex(@"^map\s*<\s*(\w+)\s*,\s*(\w+)\s*>\s+(\w+)\s*=\s*\d+\s*;$")]
    private static partial Regex MapField();

    [GeneratedRegex(@"^reserved\s+[\d\s,to]+;$")]
    private static partial Regex Reserved();

    [GeneratedRegex(@"^service\s+\w+\s*\{$")]
    private static partial Regex ServiceOpen();

    [GeneratedRegex(@"^rpc\s+(\w+)\s*\(\s*(stream\s+)?(\w+)\s*\)\s*returns\s*\(\s*(stream\s+)?(\w+)\s*\)\s*(?:\{\s*\}|;)$")]
    private static partial Regex Rpc();

    [GeneratedRegex(@"^(syntax|package|option)\b.*;$")]
    private static partial Regex TopLevelSkip();

    /// <summary>Parses a proto file; <paramref name="name"/> is how errors refer to it.</summary>
    public static Schema Parse(string source, string name)
    {
        var s = new Schema();
        var comment = new List<string>();
        Message? current = null;
        var inService = false;
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var lineNo = i + 1;
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                // A blank line ends a comment block: only a comment directly
                // above a declaration documents it.
                comment.Clear();
                continue;
            }
            if (line.StartsWith("//", StringComparison.Ordinal))
            {
                comment.Add(line[2..].Trim());
                continue;
            }
            var trailing = line.IndexOf("//", StringComparison.Ordinal);
            if (trailing >= 0)
            {
                // A trailing comment documents its line when nothing above does.
                if (comment.Count == 0)
                {
                    comment.Add(line[(trailing + 2)..].Trim());
                }
                line = line[..trailing].Trim();
            }

            if (current is null && !inService)
            {
                if (TopLevelSkip().IsMatch(line))
                {
                    comment.Clear();
                    continue;
                }
                var m = MessageOpen().Match(line);
                if (m.Success)
                {
                    var messageName = m.Groups[1].Value;
                    if (s.ByName.ContainsKey(messageName))
                    {
                        throw Fail(name, lineNo, $"message {messageName} declared twice");
                    }
                    var message = new Message(messageName, [.. comment]);
                    comment.Clear();
                    s.Messages.Add(message);
                    s.ByName[messageName] = message;
                    if (!m.Groups[2].Success)
                    {
                        current = message;
                    }
                    continue;
                }
                if (ServiceOpen().IsMatch(line))
                {
                    inService = true;
                    comment.Clear();
                    continue;
                }
                throw Fail(name, lineNo, $"unsupported top-level declaration: \"{line}\"");
            }
            if (inService)
            {
                if (line == "}")
                {
                    inService = false;
                }
                else if (Rpc().Match(line) is { Success: true } rpc)
                {
                    s.Methods.Add(new Method(
                        rpc.Groups[1].Value,
                        rpc.Groups[3].Value,
                        rpc.Groups[5].Value,
                        rpc.Groups[2].Success || rpc.Groups[4].Success,
                        [.. comment]));
                }
                else
                {
                    throw Fail(name, lineNo, $"unsupported service declaration: \"{line}\"");
                }
                comment.Clear();
                continue;
            }
            if (line == "}")
            {
                current = null;
            }
            else if (Reserved().IsMatch(line))
            {
            }
            else if (MapField().Match(line) is { Success: true } map)
            {
                current!.Fields.Add(new Field(map.Groups[3].Value, map.Groups[2].Value, false, map.Groups[1].Value, [.. comment]));
            }
            else if (FieldLine().Match(line) is { Success: true } field)
            {
                current!.Fields.Add(new Field(field.Groups[3].Value, field.Groups[2].Value, field.Groups[1].Success, null, [.. comment]));
            }
            else
            {
                throw Fail(name, lineNo, $"unsupported declaration in message {current!.Name}: \"{line}\"");
            }
            comment.Clear();
        }
        if (current is not null || inService)
        {
            throw new GeneratorException($"{name}: unexpected end of file");
        }
        Check(s, name);
        return s;
    }

    /// <summary>Verifies every message type a field or method refers to is declared.</summary>
    private static void Check(Schema s, string name)
    {
        foreach (var m in s.Messages)
        {
            foreach (var f in m.Fields)
            {
                if (!Scalars.Contains(f.Type) && s.Find(f.Type) is null)
                {
                    throw new GeneratorException($"{name}: {m.Name}.{f.Name} has unknown type {f.Type}");
                }
                if (f.MapKey is not null && f.MapKey != "string")
                {
                    throw new GeneratorException($"{name}: {m.Name}.{f.Name}: only string map keys are supported");
                }
            }
        }
        foreach (var rpc in s.Methods)
        {
            if (s.Find(rpc.Request) is null || s.Find(rpc.Response) is null)
            {
                throw new GeneratorException($"{name}: rpc {rpc.Name} refers to an undeclared message");
            }
        }
    }

    private static GeneratorException Fail(string name, int line, string message) => new($"{name}:{line}: {message}");
}

internal sealed class GeneratorException(string message) : Exception(message);
