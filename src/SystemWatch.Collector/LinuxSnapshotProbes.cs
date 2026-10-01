using SystemWatch.Contracts;

namespace SystemWatch.Collector;

/// <summary>Bind independent worker schedules to read-only Linux readers.</summary>
public sealed class LinuxSnapshotProbes(
    ICommandRunner runner, ISmartTemperatureReader smartReader,
    IPathResolver? paths = null, string procRoot = "/proc",
    string hwmonRoot = "/sys/class/hwmon",
    string perccliPath = "/usr/local/sbin/perccli",
    Func<IReadOnlyDictionary<string, IReadOnlyList<NvmeHwmonChannel>>>? nvmeChannels = null)
{
    private static readonly TimeSpan PlatformDeadline = TimeSpan.FromSeconds(15);
    private readonly Func<IReadOnlyDictionary<string, IReadOnlyList<NvmeHwmonChannel>>> channels =
        nvmeChannels ?? (() => NvmeHwmonProbe.Collect(hwmonRoot));

    public SnapshotProbeSet Create() => new(
        (at, token) => new ZfsProbe(runner, paths ?? new PhysicalPathResolver())
            .CollectAsync(at, token),
        _ => Task.FromResult(ProcSystemProbe.ReadFrom(procRoot)),
        async (at, token) =>
        {
            var output = await runner.RunAsync("ipmitool", ["sensor"],
                PlatformDeadline, token);
            var parsed = IpmiSensorParser.Parse(output.Stdout, at);
            return new SensorMeasurements(parsed.Temperatures, parsed.Fans);
        },
        async (at, token) =>
        {
            var output = await runner.RunAsync(perccliPath, ["/c0", "show", "all"],
                PlatformDeadline, token);
            var parsed = HbaRocParser.Parse(output.Stdout, at);
            if (parsed.Temperature.ValueCelsius is null)
                throw new InvalidDataException("HBA ROC temperature missing");
            return new SensorMeasurements(new() { parsed.Temperature }, new());
        },
        (at, _) => Task.FromResult(HwmonPlatformProbe.Collect(hwmonRoot, at)),
        (disks, at, token) => Task.FromResult(CollectNvme(disks, channels(), at, token)),
        (disks, at, token) => new DiskTemperatureCollector(smartReader, channels)
            .CollectAsync(disks, at, token));

    private static List<Temperature> CollectNvme(IReadOnlyList<Device> disks,
        IReadOnlyDictionary<string, IReadOnlyList<NvmeHwmonChannel>> channels,
        string timestamp, CancellationToken cancellationToken)
    {
        var temperatures = new List<Temperature>();
        foreach (var disk in disks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (disk.Transport != "nvme" || disk.Path is null) continue;
            var controller = NvmeHwmonProbe.ControllerForNamespace(disk.Path);
            if (controller is null || !channels.TryGetValue(controller, out var sensors)) continue;
            foreach (var sensor in sensors)
            {
                var value = sensor.ValueCelsius;
                temperatures.Add(new Temperature
                {
                    Id = disk.Id + ":hwmon:temp" + sensor.Number,
                    Label = sensor.Label, Category = "nvme", SourceId = "hwmon:nvme",
                    DeviceId = disk.Id, ValueCelsius = value,
                    WarningCelsius = 65, CriticalCelsius = 75,
                    ThresholdOrigin = "baseline",
                    Health = value is null ? "unknown" : value >= 75 ? "critical" :
                        value >= 65 ? "warning" : "ok",
                    LegacySystemMember = false,
                    Reading = new Reading
                    {
                        ObservedAt = value is null ? null : timestamp,
                        AgeSeconds = value is null ? null : 0, StaleAfterSeconds = 30,
                        Freshness = value is null ? "unavailable" : "fresh",
                        LastAttemptAt = timestamp,
                        LastAttemptStatus = value is null ? "error" : "ok",
                        Reason = value is null ? "hwmon_unavailable" : null,
                    },
                    ReportedStatus = null, SourceThresholdsCelsius = null,
                });
            }
        }
        return temperatures;
    }
}
