using SystemWatch.Collector;
using SystemWatch.Contracts;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: dotnet run --project tests/SystemWatch.SnapshotAssembly.Smoke -- tests/fixtures/snapshot.example.json tests/fixtures/node-fixture-sanitized.json");
    return 2;
}

void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine($"PASS {description}");
}

var exampleJson = File.ReadAllText(args[0]);
SnapshotDocument Example() => SnapshotCodec.Deserialize(exampleJson);
Source Status(string id, string kind, double seconds, string state = "ok") => new()
{
    Id = id, Kind = kind, RefreshSeconds = seconds, State = state,
    LastAttemptAt = "2026-09-23T14:00:00Z", LastSuccessAt = "2026-09-23T14:00:00Z",
    DurationMs = null, Error = null,
};
CachedSnapshotPart<T> Part<T>(T? payload, string id, string kind, double seconds,
    double? age = 0, string state = "ok") where T : class =>
    new(payload, age, Status(id, kind, seconds, state));
SnapshotParts MakeParts(SnapshotDocument d) => new(
    Part(new ZfsInventoryResult(d.Devices, d.Zfs), "zfs", "zfs", 1),
    Part(d.System, "system", "system", 1),
    Part(new SensorMeasurements(d.Temperatures.Where(t => t.SourceId == "ipmi").ToList(), d.Fans),
        "ipmi", "ipmi", 5),
    Part(new SensorMeasurements(new(), new()), "hba:perccli", "perccli", 2),
    Part(new HwmonPlatformResult(new(), Status("hwmon:platform", "hwmon", 1)),
        "hwmon:platform", "hwmon", 1),
    Part(new List<Temperature>(), "hwmon:nvme", "hwmon", 1),
    Part(new DiskTemperatureResult(d.Devices,
        d.Temperatures.Where(t => t.SourceId == "smart").ToList(), new()), "smart", "smart", 10));

try
{
    var clock = new FakeClock();
    var assembler = new SnapshotAssembler("example-host", "assembly-process-1", clock);
    var original = Example();
    var parts = MakeParts(original);
    var first = assembler.Assemble(parts);
    Check(first.Snapshot.Sequence == 1 && first.Snapshot.InstanceId == "assembly-process-1" &&
          first.Health.System.State == "ok" && first.Health.Zfs.State == "ok" &&
          first.Sources.Count == 7 && SnapshotSemantics.Validate(first).Count == 0,
          "complete example snapshot has seven sources and valid summary");
    Check(original.Devices[0].TemperatureIds.Count == 1 &&
          first.Devices[0].TemperatureIds.Count == 1 &&
          !ReferenceEquals(original.Devices[0], first.Devices[0]),
          "assembly preserves cached disk inventory");

    clock.Advance(31);
    var stale = assembler.Assemble(parts with
    {
        Zfs = parts.Zfs with { AgeSeconds = 31 },
        Ipmi = parts.Ipmi with { AgeSeconds = 31 },
        Smart = parts.Smart with { AgeSeconds = 31 },
    });
    Check(stale.Snapshot.Sequence == 2 && stale.Snapshot.InstanceId == first.Snapshot.InstanceId &&
          stale.Zfs.Pools[0].Health == "unknown" && stale.Health.Zfs.State == "unknown" &&
          stale.Temperatures.All(t => t.Health == "unknown") &&
          stale.System.UptimeSeconds == first.System.UptimeSeconds &&
          SnapshotSemantics.Validate(stale).Count == 0,
          "aged caches yield stale readings with stable process identity");

    var failed = parts with
    {
        Zfs = parts.Zfs with { Status = Status("zfs", "zfs", 1, "error") },
    };
    var zfsError = assembler.Assemble(failed);
    Check(zfsError.Zfs.InventoryState == "error" && zfsError.Zfs.PoolCount is null &&
          zfsError.Zfs.Pools.Count == 1 && zfsError.Health.Zfs.State == "unknown" &&
          SnapshotSemantics.Validate(zfsError).Count == 0,
          "failed poll retains pool but marks inventory and summary error");

    var nvmeFixture = Example();
    var slowNvme = nvmeFixture.Temperatures.Single(t => t.DeviceId == "disk:example-b");
    slowNvme.Category = "nvme";
    var fastNvme = Example().Temperatures.Single(t => t.DeviceId == "disk:example-b");
    fastNvme.Id = "disk:example-b:hwmon:temp1";
    fastNvme.Category = "nvme";
    fastNvme.SourceId = "hwmon:nvme";
    fastNvme.ValueCelsius = 42;
    fastNvme.Health = "ok";
    fastNvme.Reading.Freshness = "fresh";
    var nvmeParts = MakeParts(nvmeFixture);
    nvmeParts = nvmeParts with { NvmeHwmon = nvmeParts.NvmeHwmon with
        { Payload = new() { fastNvme } } };
    var overlaid = assembler.Assemble(nvmeParts);
    Check(overlaid.Temperatures.Count(t => t.DeviceId == "disk:example-b") == 1 &&
          overlaid.Temperatures.Any(t => t.Id == fastNvme.Id) &&
          overlaid.Devices.Single(d => d.Id == "disk:example-b").TemperatureIds
              .SequenceEqual(new[] { fastNvme.Id }) &&
          SnapshotSemantics.Validate(overlaid).Count == 0,
          "fast NVMe channel overlays SMART and repairs reciprocal device link");

    var duplicate = Example();
    duplicate.Temperatures.Add(duplicate.Temperatures[0]);
    try
    {
        assembler.Assemble(MakeParts(duplicate));
        throw new Exception("duplicate identity accepted");
    }
    catch (InvalidDataException error) when (error.Message.Contains("duplicate temperature identity"))
    {
        Console.WriteLine("PASS duplicate sensor blocks snapshot publication");
    }
    Check(assembler.Assemble(parts).Snapshot.Sequence == 5,
          "rejected snapshot does not consume a sequence number");

    var unavailable = parts with
    {
        Zfs = parts.Zfs with { Payload = null, AgeSeconds = null,
            Status = Status("zfs", "zfs", 1, "unavailable") },
        System = parts.System with { Payload = null, AgeSeconds = null,
            Status = Status("system", "system", 1, "unavailable") },
    };
    var empty = assembler.Assemble(unavailable);
    Check(empty.Zfs.InventoryState == "unavailable" && empty.Devices.Count == 0 &&
          empty.Temperatures.All(t => t.DeviceId is null) &&
          empty.System.UptimeSeconds is null && empty.System.LoadAverages.All(x => x is null) &&
          SnapshotSemantics.Validate(empty).Count == 0,
          "uninitialized inventory and system emit valid unavailable snapshot");

    var vm = SnapshotCodec.Deserialize(File.ReadAllText(args[1]));
    var vmParts = parts with { Zfs = parts.Zfs with
        { Payload = new ZfsInventoryResult(vm.Devices, vm.Zfs) } };
    var vmSnapshot = assembler.Assemble(vmParts);
    Check(vmSnapshot.Zfs.Pools.Count == vm.Zfs.Pools.Count &&
          vmSnapshot.Devices.Count == vm.Devices.Count &&
          SnapshotSemantics.Validate(vmSnapshot).Count == 0,
          "sanitized VM ZFS inventory survives complete snapshot assembly");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

internal sealed class FakeClock : ISourceClock
{
    public double MonotonicSeconds { get; private set; }
    public string UtcNow => DateTimeOffset.Parse("2026-09-23T14:00:00Z")
        .AddSeconds(MonotonicSeconds).ToString("yyyy-MM-ddTHH:mm:ssZ");
    public void Advance(double seconds) => MonotonicSeconds += seconds;
}
