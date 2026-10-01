using SystemWatch.Collector;
using SystemWatch.Contracts;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project tests/SystemWatch.Worker.Smoke -- tests/fixtures/snapshot.example.json");
    return 2;
}
if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("The offline publisher smoke test requires Linux.");
    return 2;
}

void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine($"PASS {description}");
}

var root = Path.Combine(Path.GetTempPath(), "system-watch-worker-" + Guid.NewGuid().ToString("N"));
var target = Path.Combine(root, "private", "snapshot.json");
try
{
    var fixture = SnapshotCodec.Deserialize(File.ReadAllText(args[0]));
    var clock = new FakeClock();
    var zfsCalls = 0;
    var systemCalls = 0;
    var ipmiCalls = 0;
    var hbaCalls = 0;
    var platformCalls = 0;
    var nvmeCalls = 0;
    var smartCalls = 0;
    var probes = new SnapshotProbeSet(
        (_, _) =>
        {
            zfsCalls++;
            if (zfsCalls == 2) throw new IOException("temporary pool error");
            return Task.FromResult(new ZfsInventoryResult(fixture.Devices, fixture.Zfs));
        },
        _ => { systemCalls++; return Task.FromResult(fixture.System); },
        (_, _) =>
        {
            ipmiCalls++;
            return Task.FromResult(new SensorMeasurements(
                fixture.Temperatures.Where(t => t.SourceId == "ipmi").ToList(), fixture.Fans));
        },
        (_, _) => { hbaCalls++; return Task.FromResult(new SensorMeasurements(new(), new())); },
        (_, _) =>
        {
            platformCalls++;
            return Task.FromResult(new HwmonPlatformResult(new(), Source("hwmon:platform", "hwmon", 1)));
        },
        (disks, _, _) =>
        {
            nvmeCalls++;
            Check(disks.Count == fixture.Devices.Count, "NVMe poll sees current ZFS inventory");
            return Task.FromResult(new List<Temperature>());
        },
        (disks, _, _) =>
        {
            smartCalls++;
            if (smartCalls > 1) throw new IOException("temporary SMART error");
            return Task.FromResult(new DiskTemperatureResult(
                disks.ToList(), fixture.Temperatures.Where(t => t.SourceId == "smart").ToList(),
                new() { Source("smart", "smart", 10) }));
        });

    var publisher = new AtomicSnapshotPublisher(target);
    var worker = new SnapshotWorker("example-host", clock, probes, publisher, "worker-process-1");
    var first = await worker.TickAsync();
    Check(first.Snapshot.Sequence == 1 && first.Sources.Count == 7 &&
          first.Health.System.State == "ok" && first.Health.Zfs.State == "ok" &&
          SnapshotSemantics.Validate(SnapshotCodec.Deserialize(File.ReadAllText(target))).Count == 0,
          "first tick publishes complete validated document");
    Check(zfsCalls == 1 && systemCalls == 1 && ipmiCalls == 1 && hbaCalls == 1 &&
          platformCalls == 1 && nvmeCalls == 1 && smartCalls == 1,
          "all seven probes sampled once");

    clock.Advance(1);
    var failedZfs = await worker.TickAsync();
    Check(zfsCalls == 2 && ipmiCalls == 1 && hbaCalls == 1 && smartCalls == 1 &&
          failedZfs.Zfs.InventoryState == "error" && failedZfs.Zfs.Pools.Count == 1 &&
          failedZfs.Health.Zfs.State == "unknown" &&
          failedZfs.Sources.Single(s => s.Id == "hwmon:nvme").State == "error",
          "ZFS failure retains inventory and blocks dependent NVMe poll");

    clock.Advance(1);
    var recovered = await worker.TickAsync();
    Check(zfsCalls == 3 && nvmeCalls == 2 && smartCalls == 1 &&
          recovered.Zfs.InventoryState == "ok" && recovered.Health.Zfs.State == "ok",
          "ZFS retry recovers fast source without early SMART poll");

    clock.Advance(3);
    await worker.TickAsync();
    Check(ipmiCalls == 2 && hbaCalls == 3 && smartCalls == 1,
          "IPMI, HBA and SMART respect separate five, two and ten second intervals");

    clock.Advance(5);
    var smartError = await worker.TickAsync();
    Check(smartCalls == 2 && smartError.Sources.Single(s => s.Id == "smart").State == "error" &&
          smartError.Temperatures.Any(t => t.SourceId == "smart" &&
              t.Reading.LastAttemptStatus == "error" && t.Reading.Freshness == "fresh") &&
          smartError.Sources.Single(s => s.Id == "hwmon:nvme").State == "ok",
          "SMART error retains recent reading while NVMe polls independently");

    clock.Advance(21);
    var stale = await worker.TickAsync();
    Check(stale.Temperatures.Where(t => t.SourceId == "smart").All(t =>
              t.Reading.Freshness == "stale" && t.Health == "unknown") &&
          stale.Snapshot.InstanceId == first.Snapshot.InstanceId &&
          SnapshotCodec.Deserialize(File.ReadAllText(target)).Snapshot.Sequence == stale.Snapshot.Sequence,
          "old SMART cache becomes unknown in the published snapshot");

    var restartClock = new FakeClock();
    var restarted = new SnapshotWorker("example-host", restartClock, probes,
        new AtomicSnapshotPublisher(target), "worker-process-2");
    var restart = await restarted.TickAsync();
    Check(restart.Snapshot.Sequence == 1 && restart.Snapshot.InstanceId != first.Snapshot.InstanceId &&
          restart.Sources.Count == 7 && SnapshotSemantics.Validate(restart).Count == 0,
          "restart uses new instance ID and fresh cache state");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

static Source Source(string id, string kind, int seconds) => new()
{
    Id = id, Kind = kind, RefreshSeconds = seconds, State = "ok",
    LastAttemptAt = "2026-09-23T14:00:00Z", LastSuccessAt = "2026-09-23T14:00:00Z",
    DurationMs = null, Error = null,
};

internal sealed class FakeClock : ISourceClock
{
    public double MonotonicSeconds { get; private set; }
    public string UtcNow => DateTimeOffset.Parse("2026-09-23T14:00:00Z")
        .AddSeconds(MonotonicSeconds).ToString("yyyy-MM-ddTHH:mm:ssZ");
    public void Advance(double seconds) => MonotonicSeconds += seconds;
}
