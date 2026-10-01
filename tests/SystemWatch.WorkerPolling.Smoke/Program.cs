using SystemWatch.Collector;
using SystemWatch.Contracts;

if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux fixture required");
var fixturePath = args.Length == 1 ? args[0] : "tests/fixtures/snapshot.example.json";
var fixtureJson = File.ReadAllText(fixturePath);
var root = Path.Combine(Path.GetTempPath(), "system-watch-polling-" + Guid.NewGuid().ToString("N"));
var output = Path.Combine(root, "snapshot.json");
var clock = new SystemSourceClock();
var slow = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var published = new TaskCompletionSource<SnapshotDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
var ipmiCalls = 0;
var smartCalls = 0;
var zfsCalls = 0;
var documents = new List<SnapshotDocument>();
using var stop = new CancellationTokenSource();

void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAIL " + label);
    Console.WriteLine("PASS " + label);
}

Reading Fresh(string at) => new()
{
    ObservedAt = at, AgeSeconds = 0, StaleAfterSeconds = 3,
    Freshness = "fresh", LastAttemptAt = at, LastAttemptStatus = "ok", Reason = null,
};
SnapshotDocument Fixture() => SnapshotCodec.Deserialize(fixtureJson);
var probes = new SnapshotProbeSet(
    (at, _) =>
    {
        Interlocked.Increment(ref zfsCalls);
        var d = Fixture();
        foreach (var pool in d.Zfs.Pools) pool.Reading = Fresh(at);
        return Task.FromResult(new ZfsInventoryResult(d.Devices, d.Zfs));
    },
    _ => Task.FromResult(Fixture().System),
    async (_, token) =>
    {
        Interlocked.Increment(ref ipmiCalls);
        await slow.Task.WaitAsync(token);
        return new SensorMeasurements(new(), new());
    },
    (_, _) => Task.FromResult(new SensorMeasurements(new(), new())),
    (at, _) =>
    {
        var d = Fixture();
        var temperatures = d.Temperatures.Where(t => t.SourceId == "hwmon:platform").ToList();
        // A synthetic fast CPU channel makes the freshness assertion explicit.
        var cpu = d.Temperatures.First();
        cpu.Id = "polling:cpu";
        cpu.SourceId = "hwmon:platform";
        cpu.Category = "cpu";
        cpu.DeviceId = null;
        cpu.ValueCelsius = 39;
        cpu.Health = "ok";
        cpu.Reading = Fresh(at);
        temperatures.RemoveAll(t => t.Id == cpu.Id);
        temperatures.Add(cpu);
        foreach (var t in temperatures) t.Reading = Fresh(at);
        return Task.FromResult(new HwmonPlatformResult(temperatures, new Source
        {
            Id = "hwmon:platform", Kind = "hwmon", RefreshSeconds = 1, State = "ok",
            LastAttemptAt = null, LastSuccessAt = null, DurationMs = null, Error = null,
        }));
    },
    (_, _, _) => Task.FromResult(new List<Temperature>()),
    async (_, _, token) =>
    {
        Interlocked.Increment(ref smartCalls);
        await slow.Task.WaitAsync(token);
        return new DiskTemperatureResult(new(), new(), new());
    });
var publisher = new AtomicSnapshotPublisher(output, staged =>
{
    var d = SnapshotCodec.Deserialize(File.ReadAllText(staged));
    documents.Add(d);
    if (d.Snapshot.Sequence >= 5) published.TrySetResult(d);
});
var worker = new SnapshotWorker("example-host", clock, probes, publisher);
var run = worker.RunAsync(stop.Token);
try
{
    var completed = await Task.WhenAny(published.Task, run, Task.Delay(TimeSpan.FromSeconds(12)));
    if (completed == run) await run;
    Check(completed == published.Task, "watch publishes five snapshots while slow collectors remain blocked");
    var fifth = await published.Task;
    Check(fifth.Zfs.Pools.Count > 0 && fifth.Zfs.Pools.All(p => p.Reading.Freshness == "fresh") &&
        fifth.Temperatures.Single(t => t.Id == "polling:cpu").Reading.Freshness == "fresh",
        "ZFS and CPU remain fresh beyond their three-second stale deadline");
    Check(Volatile.Read(ref zfsCalls) >= 3 && Volatile.Read(ref ipmiCalls) == 1 &&
        Volatile.Read(ref smartCalls) == 1, "slow probes never overlap or block fast repolling");
    Check(fifth.Sources.Single(s => s.Id == "ipmi").State == "unavailable" &&
        fifth.Sources.Single(s => s.Id == "smart").State == "unavailable",
        "unfinished first collection is honestly unavailable");
}
finally
{
    stop.Cancel();
    try { await run; }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}
Check(documents.Count >= 5 && documents.All(d => SnapshotSemantics.Validate(d).Count == 0),
    "published snapshots validate and cancellation joins all polling tasks");
