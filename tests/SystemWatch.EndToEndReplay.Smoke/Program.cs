using System.Text.Json;
using SystemWatch.Collector;
using SystemWatch.Contracts;
using SystemWatch.Worker;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: dotnet run --project tests/SystemWatch.EndToEndReplay.Smoke -- tests/fixtures/node-fixture-zfs tests/fixtures/proc");
    return 2;
}
if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("The offline publisher replay requires Linux.");
    return 2;
}

void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine($"PASS {description}");
}

var directory = Path.Combine(Path.GetTempPath(), "system-watch-replay-" + Guid.NewGuid().ToString("N"));
var output = Path.Combine(directory, "private", "snapshot.json");
try
{
    var parsed = CliOptions.Parse(new[] { "--once", "--output", output });
    Check(!parsed.Watch && parsed.Output == output &&
          CliOptions.Parse(new[] { "--watch", "--output", output }).Watch,
          "CLI requires explicit absolute output for once and watch modes");
    var live = "/run/system-watch-proxmox/snapshot.json";
    var approved = CliOptions.Parse(new[]
    {
        "--watch", "--output", live, "--allow-live-output", "--expected-host", "node-fixture",
    }, hostname: "node-fixture");
    Check(approved.Watch && approved.AllowLiveOutput && approved.Output == live,
          "exact live target can be selected only with explicit VM watch opt-in");
    var cleanVm = CliOptions.Parse(new[]
    {
        "--watch", "--output", live, "--allow-live-output", "--expected-host", "node-other",
    }, hostname: "node-other");
    Check(cleanVm.Watch && cleanVm.AllowLiveOutput && cleanVm.Output == live,
          "separately named test VM requires an explicit matching host");
    var epyc = CliOptions.Parse(new[] { "--watch", "--output", live, "--allow-live-output", "--expected-host", "node-production" }, hostname: "node-production");
    Check(epyc.Watch && epyc.AllowLiveOutput && epyc.Output == live,
          "node-production live output requires explicit matching hostname");
    foreach (var rejected in new[]
    {
        (new[] { "--once", "--output", live, "--allow-live-output" }, "node-fixture"),
        (new[] { "--watch", "--output", live, "--allow-live-output" }, "node-production"),
        (new[] { "--watch", "--output", output, "--allow-live-output" }, "node-fixture"),
        (new[] { "--watch", "--output", live + ".bak", "--allow-live-output" }, "node-fixture"),
        (new[] { "--watch", "--output", live, "--unsafe" }, "node-fixture"),
        (new[] { "--watch", "--output", live, "--allow-live-output", "--expected-host", "node-other" }, "node-fixture"),
        (new[] { "--once", "--output", live, "--allow-live-output", "--expected-host", "node-other" }, "node-other"),
        (new[] { "--watch", "--output", live + ".bak", "--allow-live-output", "--expected-host", "node-other" }, "node-other"),
        (new[] { "--watch", "--output", live, "--allow-live-output", "--expected-host", "PVE 0102" }, "PVE 0102"),
    })
    {
        try
        {
            CliOptions.Parse(rejected.Item1, rejected.Item2);
            throw new Exception("unsafe live selection accepted");
        }
        catch (ArgumentException) { }
    }
    Console.WriteLine("PASS live opt-in rejects once, implicit or mismatched hosts and alternate paths");
    foreach (var forbidden in new[]
    {
        "/run/system-watch-proxmox/snapshot.json",
        "/run/system-watch-proxmox/subdir/other.json",
        "/run/system-watch-proxmox/../system-watch-proxmox/snapshot.json",
    })
    {
        try
        {
            CliOptions.Parse(new[] { "--once", "--output", forbidden });
            throw new Exception("live directory accepted: " + forbidden);
        }
        catch (ArgumentException) { }
    }
    try
    {
        CliOptions.Parse(new[] { "--once", "--output", "relative.json" });
        throw new Exception("relative output accepted");
    }
    catch (ArgumentException) { }
    Check(!Directory.Exists(directory), "CLI rejects live and relative paths without writing");

    var map = JsonSerializer.Deserialize<Dictionary<string, string>>(
        File.ReadAllText(Path.Combine(args[0], "realpaths.json")))!;
    var runner = new ReplayRunner(args[0]);
    var clock = new FakeClock();
    var probes = new LinuxSnapshotProbes(runner, new UnavailableSmart(),
        new FixtureResolver(map), procRoot: args[1],
        hwmonRoot: Path.Combine(directory, "no-hwmon")).Create();
    var worker = new SnapshotWorker("example-vm", clock, probes,
        new AtomicSnapshotPublisher(parsed.Output), "replay-process-1");
    var first = await worker.TickAsync();
    var published = SnapshotCodec.Deserialize(File.ReadAllText(output));
    Check(first.Zfs.Pools.Count == 2 && first.Devices.Count == 3 &&
          first.Devices.Count(d => d.ZfsMembership == "member") == 2 &&
          first.Zfs.Pools.All(p => p.Health == "ok") && first.Health.Zfs.State == "ok" &&
          runner.ZfsCalls == 3 && SnapshotSemantics.Validate(published).Count == 0,
          "sanitized VM commands produce valid two-pool published snapshot");
    Check(published.Sources.Count == 7 &&
          published.Sources.Single(s => s.Id == "ipmi").State == "error" &&
          published.Sources.Single(s => s.Id == "hba:perccli").State == "error" &&
          published.Sources.Single(s => s.Id == "smart").State == "partial" &&
          published.Health.System.State == "unknown" &&
          published.Temperatures.All(t => t.Health == "unknown"),
          "missing platform tools and unavailable disk SMART do not publish green sensors");
    Check(published.Devices.All(d => d.TemperatureIds.All(id =>
          published.Temperatures.Any(t => t.Id == id && t.DeviceId == d.Id))) &&
          published.Zfs.Pools.Select(p => p.Id).Order(StringComparer.Ordinal).SequenceEqual(
              published.Devices.SelectMany(d => d.PoolIds).Distinct()
                  .Order(StringComparer.Ordinal)),
          "published disk and pool references remain reciprocal");

    clock.Advance(1);
    var second = await worker.TickAsync();
    Check(second.Snapshot.Sequence == 2 &&
          second.Snapshot.InstanceId == first.Snapshot.InstanceId &&
          SnapshotCodec.Deserialize(File.ReadAllText(output)).Snapshot.Sequence == 2 &&
          runner.ZfsCalls == 6 && runner.IpmiCalls == 1 && runner.HbaCalls == 1,
          "second publication refreshes ZFS without repolling slow sources");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
finally
{
    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
}

internal sealed class ReplayRunner(string root) : ICommandRunner
{
    public int ZfsCalls { get; private set; }
    public int IpmiCalls { get; private set; }
    public int HbaCalls { get; private set; }

    public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken = default,
        bool acceptNonZeroExit = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (executable == "ipmitool") { IpmiCalls++; throw new IOException("ipmitool missing"); }
        if (executable == "/usr/local/sbin/perccli")
        { HbaCalls++; throw new IOException("perccli missing"); }
        string file = (executable, arguments.ToArray()) switch
        {
            ("zpool", ["list", "-H", "-p", "-o", "name,size,alloc,free,cap,frag,health"]) =>
                "zpool-list.tsv",
            ("zpool", ["status", "-j", "-p", "-P"]) => "zpool-status.json",
            ("lsblk", ["-J", "-b", "-o",
                "NAME,PATH,TYPE,WWN,SERIAL,TRAN,ROTA,MODEL,FSTYPE,MOUNTPOINTS"]) => "lsblk.json",
            _ => throw new InvalidDataException("unexpected ZFS command"),
        };
        if (timeout != TimeSpan.FromSeconds(20) || acceptNonZeroExit)
            throw new InvalidDataException("unexpected ZFS command options");
        ZfsCalls++;
        return Task.FromResult(new CommandResult(File.ReadAllText(Path.Combine(root, file)), "", 0));
    }
}

internal sealed class FixtureResolver(IReadOnlyDictionary<string, string> paths) : IPathResolver
{
    public string Resolve(string path) => paths[path];
}

internal sealed class UnavailableSmart : ISmartTemperatureReader
{
    public Task<SmartTemperature> ReadAsync(string devicePath, bool isHdd,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new SmartTemperature(null, "smartctl_error", false));
}

internal sealed class FakeClock : ISourceClock
{
    public double MonotonicSeconds { get; private set; }
    public string UtcNow => DateTimeOffset.Parse("2026-09-27T12:00:00Z")
        .AddSeconds(MonotonicSeconds).ToString("yyyy-MM-ddTHH:mm:ssZ");
    public void Advance(double seconds) => MonotonicSeconds += seconds;
}
