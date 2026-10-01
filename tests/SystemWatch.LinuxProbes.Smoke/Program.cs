using System.Text.Json;
using SystemWatch.Collector;
using SystemWatch.Contracts;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: dotnet run --project tests/SystemWatch.LinuxProbes.Smoke -- tests/fixtures/proc tests/fixtures/platform tests/fixtures/disk-temperatures");
    return 2;
}

void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine($"PASS {description}");
}

try
{
    var runner = new FakeRunner(
        File.ReadAllText(Path.Combine(args[1], "ipmi-synthetic.txt")),
        File.ReadAllText(Path.Combine(args[1], "hba-present.txt")));
    var smartReader = new FakeSmartReader();
    IReadOnlyDictionary<string, IReadOnlyList<NvmeHwmonChannel>> Channels() =>
        new Dictionary<string, IReadOnlyList<NvmeHwmonChannel>>
        {
            ["5"] = new List<NvmeHwmonChannel>
            {
                new("1", "Composite", 42), new("2", "Sensor 2", null),
            },
        };
    const string at = "2026-09-27T12:00:00Z";
    var probes = new LinuxSnapshotProbes(runner, smartReader,
        procRoot: args[0], hwmonRoot: Path.Combine(args[1], "missing-hwmon"),
        nvmeChannels: Channels).Create();

    var system = await probes.System(CancellationToken.None);
    Check(system.UptimeSeconds.HasValue && system.LoadAverages.Count == 3,
          "system reader uses captured proc files");
    var ipmi = await probes.Ipmi(at, CancellationToken.None);
    Check(ipmi.Temperatures.Count == 3 && ipmi.Fans.Count == 2 &&
          runner.Calls.SequenceEqual(new[] { "ipmitool sensor" }),
          "IPMI reads only its independent command");
    var hba = await probes.Hba(at, CancellationToken.None);
    Check(hba.Temperatures.Count == 1 && hba.Temperatures[0].ValueCelsius is not null &&
          runner.Calls.SequenceEqual(new[]
          {
              "ipmitool sensor", "/usr/local/sbin/perccli /c0 show all",
          }), "HBA reads only its independent command");
    var platform = await probes.PlatformHwmon(at, CancellationToken.None);
    Check(platform.Source.State == "ok" && platform.Temperatures.Count == 0,
          "empty hwmon inventory is healthy");

    var disks = JsonSerializer.Deserialize<List<Device>>(
        File.ReadAllText(Path.Combine(args[2], "devices.json")))!;
    var fast = await probes.NvmeHwmon(disks, at, CancellationToken.None);
    Check(fast.Count == 2 && fast.All(t => t.SourceId == "hwmon:nvme") &&
          fast[0].ValueCelsius == 42 && fast[1].Health == "unknown" &&
          fast[1].Reading.Reason == "hwmon_unavailable" && smartReader.Calls == 0,
          "fast NVMe channel path never wakes SMART");
    var slow = await probes.Smart(disks, at, CancellationToken.None);
    Check(smartReader.Calls == disks.Count - 1 &&
          slow.Temperatures.Any(t => t.SourceId == "hwmon:nvme") &&
          slow.Sources.Any(s => s.Id == "smart"),
          "slow SMART path retains one-shot adapter parity and skips hwmon device");
    Check(disks.All(d => d.TemperatureIds.Count == 0),
          "both disk probe functions preserve original inventory");

    runner.HbaMissing = true;
    try
    {
        await probes.Hba(at, CancellationToken.None);
        throw new Exception("missing HBA accepted");
    }
    catch (IOException error) when (error.Message.Contains("missing perccli"))
    {
        Console.WriteLine("PASS missing HBA command fails only its own probe");
    }
    Check((await probes.Ipmi(at, CancellationToken.None)).Temperatures.Count == 3,
          "IPMI still parses after HBA failure");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

internal sealed class FakeRunner(string ipmi, string hba) : ICommandRunner
{
    public bool HbaMissing { get; set; }
    public List<string> Calls { get; } = new();

    public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken = default,
        bool acceptNonZeroExit = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(executable + " " + string.Join(" ", arguments));
        if (timeout != TimeSpan.FromSeconds(15) || acceptNonZeroExit)
            throw new Exception("wrong command boundary");
        if (executable == "ipmitool" && arguments.SequenceEqual(new[] { "sensor" }))
            return Task.FromResult(new CommandResult(ipmi, "", 0));
        if (executable == "/usr/local/sbin/perccli" &&
            arguments.SequenceEqual(new[] { "/c0", "show", "all" }))
        {
            if (HbaMissing) throw new IOException("missing perccli");
            return Task.FromResult(new CommandResult(hba, "", 0));
        }
        throw new Exception("unexpected command: " + executable);
    }
}

internal sealed class FakeSmartReader : ISmartTemperatureReader
{
    public int Calls { get; private set; }
    public Task<SmartTemperature> ReadAsync(string devicePath, bool isHdd,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return Task.FromResult(new SmartTemperature(36, null, false));
    }
}
