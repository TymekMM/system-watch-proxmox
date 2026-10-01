using SystemWatch.Contracts;

namespace SystemWatch.Collector;

public sealed record CachedSnapshotPart<T>(T? Payload, double? AgeSeconds, Source Status)
    where T : class
{
    public static CachedSnapshotPart<T> From(SourceCache<T> cache) =>
        cache.Capture();
}

public sealed record SensorMeasurements(List<Temperature> Temperatures, List<Fan> Fans);

public sealed record SnapshotParts(
    CachedSnapshotPart<ZfsInventoryResult> Zfs,
    CachedSnapshotPart<SystemMetrics> System,
    CachedSnapshotPart<SensorMeasurements> Ipmi,
    CachedSnapshotPart<SensorMeasurements> Hba,
    CachedSnapshotPart<HwmonPlatformResult> PlatformHwmon,
    CachedSnapshotPart<List<Temperature>> NvmeHwmon,
    CachedSnapshotPart<DiskTemperatureResult> Smart);

/// <summary>Compose one validated draft.2 document from independent source caches.</summary>
public sealed class SnapshotAssembler(string hostname, string instanceId, ISourceClock clock)
{
    private long sequence;

    public SnapshotDocument Assemble(SnapshotParts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var at = clock.UtcNow;
        var sources = new[]
        {
            parts.Zfs.Status, parts.System.Status, parts.Ipmi.Status,
            parts.Hba.Status, parts.PlatformHwmon.Status,
            parts.NvmeHwmon.Status, parts.Smart.Status,
        }.ToList();
        var expected = new[] { "zfs", "system", "ipmi", "hba:perccli",
            "hwmon:platform", "hwmon:nvme", "smart" };
        for (var i = 0; i < expected.Length; i++)
            if (sources[i].Id != expected[i])
                throw new InvalidDataException($"expected {expected[i]} source, got {sources[i].Id}");

        var zfsSource = parts.Zfs;
        var disks = zfsSource.Payload?.Devices.Select(CloneDevice).ToList() ?? new();
        var byDisk = disks.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var pools = zfsSource.Payload?.Zfs.Pools.Select(p => ReadingAger.Pool(
            p, zfsSource.AgeSeconds, zfsSource.Status, at)).ToList() ?? new();
        var zfs = new ZfsInventory
        {
            InventoryState = zfsSource.Status.State == "error" ? "error" :
                zfsSource.Payload?.Zfs.InventoryState ?? "unavailable",
            PoolCount = zfsSource.Status.State == "error" ? null : zfsSource.Payload?.Zfs.PoolCount,
            Pools = pools,
        };
        var slow = parts.Smart;
        if (slow.Payload is not null)
        {
            var slowByDisk = slow.Payload.Devices.ToDictionary(d => d.Id, StringComparer.Ordinal);
            foreach (var disk in disks)
                if (slowByDisk.TryGetValue(disk.Id, out var cached)) disk.PowerState = cached.PowerState;
        }

        var temperatures = new List<Temperature>();
        void Add(IEnumerable<Temperature>? items, double? age, Source status)
        {
            if (items is null) return;
            foreach (var item in items)
                if (item.DeviceId is null || byDisk.ContainsKey(item.DeviceId))
                    temperatures.Add(ReadingAger.Temperature(item, age, status, at));
        }
        Add(parts.Ipmi.Payload?.Temperatures, parts.Ipmi.AgeSeconds, parts.Ipmi.Status);
        Add(parts.Hba.Payload?.Temperatures, parts.Hba.AgeSeconds, parts.Hba.Status);
        Add(parts.PlatformHwmon.Payload?.Temperatures,
            parts.PlatformHwmon.AgeSeconds, parts.PlatformHwmon.Status);
        Add(slow.Payload?.Temperatures, slow.AgeSeconds, slow.Status);

        var fast = parts.NvmeHwmon;
        if (fast.Payload is { Count: > 0 })
        {
            var fastDisks = fast.Payload.Select(t => t.DeviceId).ToHashSet(StringComparer.Ordinal);
            temperatures.RemoveAll(t => t.DeviceId is not null &&
                fastDisks.Contains(t.DeviceId) && t.Category == "nvme");
            Add(fast.Payload.Where(t => t.DeviceId is not null && byDisk.ContainsKey(t.DeviceId)),
                fast.AgeSeconds, fast.Status);
        }
        temperatures.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in temperatures)
        {
            if (!ids.Add(item.Id)) throw new InvalidDataException($"duplicate temperature identity: {item.Id}");
            if (item.DeviceId is not null) byDisk[item.DeviceId].TemperatureIds.Add(item.Id);
        }
        var fans = parts.Ipmi.Payload?.Fans.Select(f => ReadingAger.Fan(
            f, parts.Ipmi.AgeSeconds, parts.Ipmi.Status, at)).ToList() ?? new();
        var system = parts.System.Payload;
        var document = new SnapshotDocument
        {
            SchemaVersion = "1.0.0-draft.2",
            Snapshot = new SnapshotHeader
            {
                Hostname = hostname, GeneratedAt = at, CollectorVersion = "snapshot-v0",
                InstanceId = instanceId, Sequence = checked(sequence + 1),
            },
            System = new SystemMetrics
            {
                UptimeSeconds = system?.UptimeSeconds,
                LoadAverages = system is null ? new() { null, null, null } :
                    new(system.LoadAverages),
            },
            Health = SnapshotHealthAggregator.Aggregate(temperatures, pools, sources),
            Sources = sources, Temperatures = temperatures, Fans = fans,
            Devices = disks, Zfs = zfs,
        };
        var errors = SnapshotSemantics.Validate(document);
        if (errors.Count > 0)
            throw new InvalidDataException("snapshot validation: " + string.Join("; ", errors));
        sequence = document.Snapshot.Sequence;
        return document;
    }

    private static Device CloneDevice(Device d) => new()
    {
        Id = d.Id, IdentityStability = d.IdentityStability, Path = d.Path,
        Aliases = new(d.Aliases), Model = d.Model, Serial = d.Serial,
        Transport = d.Transport, Media = d.Media, PowerState = d.PowerState,
        ZfsMembership = d.ZfsMembership, PoolIds = new(d.PoolIds),
        TemperatureIds = new(), Usage = d.Usage,
    };
}
