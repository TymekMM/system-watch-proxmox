using SystemWatch.Contracts;

namespace SystemWatch.Collector;

public sealed record ZfsInventoryResult(List<Device> Devices, ZfsInventory Zfs);

/// <summary>Join parsed ZFS topology to physical lsblk devices.</summary>
public static class ZfsInventoryAssembler
{
    public static ZfsInventoryResult Assemble(IReadOnlyList<ZpoolStatusPool> pools,
        LsblkInventory inventory, Func<string, string> resolvePath, string observedAt)
    {
        ArgumentNullException.ThrowIfNull(pools);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(resolvePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(observedAt);

        // Do not mutate the caller's parsed lsblk inventory when a join fails.
        var devices = inventory.Devices.Select(Clone).ToList();
        var byId = devices.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var output = new List<Pool>();
        foreach (var raw in pools)
        {
            var vdevs = new List<Vdev>();
            foreach (var node in raw.Vdevs)
            {
                string? deviceId = null;
                if (node.Kind == "disk" && !string.IsNullOrEmpty(node.Path))
                {
                    inventory.DeviceIdByPath.TryGetValue(resolvePath(node.Path), out deviceId);
                    if (deviceId is null && node.State == "ONLINE")
                        throw new InvalidDataException($"unmapped ONLINE ZFS leaf in {raw.Name}: {node.Path}");
                    if (deviceId is not null)
                    {
                        var device = byId[deviceId];
                        device.ZfsMembership = "member";
                        if (!device.PoolIds.Contains(raw.Id)) device.PoolIds.Add(raw.Id);
                        if (!device.Aliases.Contains(node.Path)) device.Aliases.Add(node.Path);
                    }
                }
                vdevs.Add(new Vdev
                {
                    Id = node.Id, ParentId = node.ParentId, Kind = node.Kind,
                    AllocationClass = node.AllocationClass, Name = node.Name,
                    State = node.State, DeviceId = deviceId,
                    Errors = new Errors
                    {
                        Read = node.ReadErrors, Write = node.WriteErrors,
                        Checksum = node.ChecksumErrors,
                    },
                });
            }
            output.Add(new Pool
            {
                Id = raw.Id, Name = raw.Name, State = raw.State, Health = raw.Health,
                Reading = new Reading
                {
                    ObservedAt = observedAt, AgeSeconds = 0, StaleAfterSeconds = 3,
                    Freshness = "fresh", LastAttemptAt = observedAt,
                    LastAttemptStatus = "ok", Reason = null,
                },
                SizeBytes = raw.ListEntry.SizeBytes,
                AllocatedBytes = raw.ListEntry.AllocatedBytes,
                FreeBytes = raw.ListEntry.FreeBytes,
                CapacityPercent = raw.ListEntry.CapacityPercent,
                FragmentationPercent = raw.ListEntry.FragmentationPercent,
                Vdevs = vdevs, Scan = raw.Scan, PermanentErrors = raw.PermanentErrors,
            });
        }
        return new ZfsInventoryResult(devices, new ZfsInventory
        {
            InventoryState = "ok", PoolCount = output.Count, Pools = output,
        });
    }

    private static Device Clone(Device device) => new()
    {
        Id = device.Id, IdentityStability = device.IdentityStability,
        Path = device.Path, Aliases = new List<string>(device.Aliases),
        Model = device.Model, Serial = device.Serial,
        Transport = device.Transport, Media = device.Media,
        PowerState = device.PowerState, ZfsMembership = device.ZfsMembership,
        PoolIds = new List<string>(device.PoolIds),
        TemperatureIds = new List<string>(device.TemperatureIds), Usage = device.Usage,
    };
}
