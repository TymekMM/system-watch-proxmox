using System.Text.Json;
using SystemWatch.Collector;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project tests/SystemWatch.ZfsProbe.Smoke -- tests/fixtures/node-fixture-zfs");
    return 2;
}

void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine($"PASS {description}");
}

try
{
    var root = args[0];
    var responses = new[]
    {
        ("zpool", new[] { "list", "-H", "-p", "-o", "name,size,alloc,free,cap,frag,health" },
            File.ReadAllText(Path.Combine(root, "zpool-list.tsv"))),
        ("zpool", new[] { "status", "-j", "-p", "-P" },
            File.ReadAllText(Path.Combine(root, "zpool-status.json"))),
        ("lsblk", new[] { "-J", "-b", "-o", "NAME,PATH,TYPE,WWN,SERIAL,TRAN,ROTA,MODEL,FSTYPE,MOUNTPOINTS" },
            File.ReadAllText(Path.Combine(root, "lsblk.json"))),
    };
    var runner = new FixtureRunner(responses);
    var map = JsonSerializer.Deserialize<Dictionary<string, string>>(
        File.ReadAllText(Path.Combine(root, "realpaths.json")))!;
    var inventory = await new ZfsProbe(runner, new FixtureResolver(map))
        .CollectAsync("2026-09-27T12:00:00Z");
    Check(runner.Calls == 3, "three read-only commands have exact argv and deadlines");
    Check(inventory.Zfs.PoolCount == 2 && inventory.Devices.Count == 3 &&
          inventory.Devices.Count(d => d.ZfsMembership == "member") == 2 &&
          inventory.Zfs.Pools.All(p => p.Health == "ok" && p.Reading.Freshness == "fresh"),
          "sanitized VM inputs assemble into healthy pool inventory");

    try
    {
        await new ZfsProbe(new FailedRunner(), new FixtureResolver(map))
            .CollectAsync("2026-09-27T12:00:00Z");
        throw new Exception("failed command accepted");
    }
    catch (IOException)
    { Console.WriteLine("PASS failed command propagates for source error reporting"); }

    var scratch = Path.Combine(Path.GetTempPath(), "system-watch-link-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(scratch);
    try
    {
        var target = Path.Combine(scratch, "disk1");
        File.WriteAllText(target, "");
        var link = Path.Combine(scratch, "disk-by-id");
        File.CreateSymbolicLink(link, target);
        var resolver = new PhysicalPathResolver();
        Check(resolver.Resolve(link) == target &&
              resolver.Resolve(Path.Combine(scratch, "missing")) == Path.Combine(scratch, "missing"),
              "real symlink and missing leaf path resolved safely");
    }
    finally { Directory.Delete(scratch, recursive: true); }

    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

internal sealed class FixtureRunner((string Executable, string[] Arguments, string Output)[] fixture)
    : ICommandRunner
{
    public int Calls { get; private set; }

    public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken = default,
        bool acceptNonZeroExit = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Calls >= fixture.Length || fixture[Calls].Executable != executable ||
            !arguments.SequenceEqual(fixture[Calls].Arguments) || timeout != TimeSpan.FromSeconds(20) ||
            acceptNonZeroExit)
            throw new Exception("command argv, order or deadline changed");
        return Task.FromResult(new CommandResult(fixture[Calls++].Output, "", 0));
    }
}

internal sealed class FixtureResolver(IReadOnlyDictionary<string, string> paths) : IPathResolver
{
    public string Resolve(string path) => paths[path];
}

internal sealed class FailedRunner : ICommandRunner
{
    public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken = default,
        bool acceptNonZeroExit = false) =>
        throw new IOException("zpool unavailable");
}
