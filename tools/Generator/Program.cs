// Writes the generated C# of the SDK from proto/api.proto and proto/proxy.proto:
//
//   dotnet run --project tools/Generator               writes the files
//   dotnet run --project tools/Generator -- --check    fails when they are stale
//   dotnet run --project tools/Generator -- --accept-required
//                                                       accepts a changed list of
//                                                       required fields
using Centrifugal.Cent.Generator;

var check = args.Contains("--check");
var acceptRequired = args.Contains("--accept-required");
var unknown = args.Where(a => a is not ("--check" or "--accept-required")).ToList();
if (unknown.Count > 0)
{
    Console.Error.WriteLine($"unknown arguments: {string.Join(' ', unknown)}");
    return 2;
}

try
{
    var root = FindRoot();
    var api = Proto.Parse(File.ReadAllText(Path.Combine(root, "proto", "api.proto")), "proto/api.proto");
    var proxy = Proto.Parse(File.ReadAllText(Path.Combine(root, "proto", "proxy.proto")), "proto/proxy.proto");
    var required = new SortedSet<string>(StringComparer.Ordinal);
    var files = new Dictionary<string, string>
    {
        ["src/Centrifugal.Cent/Generated/Api.g.cs"] = ApiEmitter.Types(api, required),
        ["src/Centrifugal.Cent/Generated/Methods.g.cs"] = ApiEmitter.Methods(api),
        ["src/Centrifugal.Cent/Generated/Proxy.g.cs"] = ProxyEmitter.Generate(proxy),
        ["tests/Centrifugal.Cent.Tests/Generated/MethodsTests.g.cs"] = ApiEmitter.Tests(api),
    };
    Reviewed.Check(Path.Combine(root, "tools", "Generator", "required_fields.txt"), required, acceptRequired);

    var stale = new List<string>();
    foreach (var (path, content) in files)
    {
        var full = Path.Combine(root, path);
        var current = File.Exists(full) ? File.ReadAllText(full) : null;
        if (current == content)
        {
            continue;
        }
        if (check)
        {
            stale.Add(path);
            continue;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        Console.WriteLine($"wrote {path}");
    }
    if (stale.Count > 0)
    {
        Console.Error.WriteLine($"generated code is stale: {string.Join(", ", stale)}");
        Console.Error.WriteLine("run: dotnet run --project tools/Generator");
        return 1;
    }
    return 0;
}
catch (GeneratorException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

static string FindRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "proto", "api.proto")))
        {
            return dir.FullName;
        }
    }
    throw new GeneratorException("run from inside the repository: proto/api.proto not found");
}
