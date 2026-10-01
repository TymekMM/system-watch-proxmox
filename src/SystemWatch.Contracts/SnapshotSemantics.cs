using System.Globalization;

namespace SystemWatch.Contracts;

// Semantic checks complement (and do not replace) schema/snapshot.schema.json.
public static class SnapshotSemantics
{
    public static IReadOnlyList<string> Validate(SnapshotDocument d)
    {
        var errors = new List<string>();
        if (d.SchemaVersion != "1.0.0-draft.2") errors.Add("schema_version: unsupported contract");
        if (d.Snapshot is null || d.Health is null || d.Zfs is null || d.System is null ||
            d.Sources is null || d.Temperatures is null || d.Fans is null || d.Devices is null)
        {
            errors.Add("snapshot: missing required section");
            return errors;
        }
        if (d.Zfs.Pools is null)
        {
            errors.Add("zfs.pools: missing required list");
            return errors;
        }

        Utc(d.Snapshot.GeneratedAt, "snapshot.generated_at", errors);
        if (d.Snapshot.Sequence is < 0 or > 9007199254740991L)
            errors.Add("snapshot.sequence: outside safe JSON integer range");
        if (d.System.LoadAverages is null || d.System.LoadAverages.Count != 3)
            errors.Add("system.load_averages: expected exactly three values");

        var sources = Unique(d.Sources.Select(x => x?.Id), "sources", errors);
        var temps = Unique(d.Temperatures.Select(x => x?.Id), "temperatures", errors);
        var devices = Unique(d.Devices.Select(x => x?.Id), "devices", errors);
        var pools = Unique(d.Zfs.Pools.Select(x => x?.Id), "zfs.pools", errors);

        foreach (var t in d.Temperatures)
        {
            if (t is null) { errors.Add("temperatures: null entry"); continue; }
            Reference(sources, t.SourceId, $"temperature {t.Id}.source_id", errors);
            if (t.DeviceId is not null) Reference(devices, t.DeviceId, $"temperature {t.Id}.device_id", errors);
            ReadingState(t.Reading, t.ValueCelsius, t.Health, $"temperature {t.Id}", errors);
            if (t.WarningCelsius.HasValue && t.CriticalCelsius.HasValue &&
                t.CriticalCelsius.Value < t.WarningCelsius.Value)
                errors.Add($"temperature {t.Id}: critical threshold below warning");
        }
        foreach (var f in d.Fans)
        {
            if (f is null) { errors.Add("fans: null entry"); continue; }
            Reference(sources, f.SourceId, $"fan {f.Id}.source_id", errors);
            ReadingState(f.Reading, f.Rpm, f.Health, $"fan {f.Id}", errors);
        }
        foreach (var device in d.Devices)
        {
            if (device is null) { errors.Add("devices: null entry"); continue; }
            foreach (var id in device.TemperatureIds ?? [])
            {
                Reference(temps, id, $"device {device.Id}.temperature_ids", errors);
                var t = d.Temperatures.FirstOrDefault(t => t?.Id == id);
                if (t is not null && t.DeviceId != device.Id)
                    errors.Add($"device {device.Id}: temperature {id} points to another device");
            }
            foreach (var id in device.PoolIds ?? []) Reference(pools, id, $"device {device.Id}.pool_ids", errors);
            if (device.ZfsMembership == "nonmember" && device.PoolIds is { Count: > 0 })
                errors.Add($"device {device.Id}: nonmember has pool IDs");
        }
        foreach (var t in d.Temperatures.Where(t => t?.DeviceId is not null))
        {
            var device = d.Devices.FirstOrDefault(x => x?.Id == t.DeviceId);
            if (device is not null && (device.TemperatureIds is null || !device.TemperatureIds.Contains(t.Id)))
                errors.Add($"temperature {t.Id}: device lacks reciprocal temperature ID");
        }

        if (d.Health.HottestTemperatureId is not null)
            Reference(temps, d.Health.HottestTemperatureId, "health.hottest_temperature_id", errors);
        if (d.Zfs.InventoryState == "ok" && d.Zfs.PoolCount != d.Zfs.Pools.Count)
            errors.Add("zfs.pool_count: does not match pools");
        if (d.Zfs.InventoryState != "ok" && d.Zfs.PoolCount is not null)
            errors.Add("zfs.pool_count: expected null when inventory failed");

        foreach (var pool in d.Zfs.Pools)
        {
            if (pool is null) { errors.Add("zfs.pools: null entry"); continue; }
            ReadingState(pool.Reading, pool.Reading?.Freshness == "unavailable" ? null : 1,
                pool.Health, $"pool {pool.Id}", errors);
            DecimalCounter(pool.AllocatedBytes, $"pool {pool.Id}.allocated_bytes", errors);
            DecimalCounter(pool.SizeBytes, $"pool {pool.Id}.size_bytes", errors);
            DecimalCounter(pool.FreeBytes, $"pool {pool.Id}.free_bytes", errors);
            if (pool.Vdevs is null) { errors.Add($"pool {pool.Id}: missing vdevs"); continue; }
            var vdevIds = Unique(pool.Vdevs.Select(v => v?.Id), $"pool {pool.Id}.vdevs", errors);
            foreach (var v in pool.Vdevs)
            {
                if (v is null) { errors.Add($"pool {pool.Id}: null vdev"); continue; }
                if (v.ParentId is not null) Reference(vdevIds, v.ParentId, $"vdev {v.Id}.parent_id", errors);
                if (v.DeviceId is not null)
                {
                    Reference(devices, v.DeviceId, $"vdev {v.Id}.device_id", errors);
                    var device = d.Devices.FirstOrDefault(x => x?.Id == v.DeviceId);
                    if (device is not null && (device.PoolIds is null || !device.PoolIds.Contains(pool.Id)))
                        errors.Add($"vdev {v.Id}: device lacks reciprocal pool ID");
                }
                if (v.Errors is not null)
                {
                    DecimalCounter(v.Errors.Read, $"vdev {v.Id}.errors.read", errors);
                    DecimalCounter(v.Errors.Write, $"vdev {v.Id}.errors.write", errors);
                    DecimalCounter(v.Errors.Checksum, $"vdev {v.Id}.errors.checksum", errors);
                }
                var seen = new HashSet<string>();
                Vdev? cursor = v;
                while (cursor is { ParentId: { } parentId })
                {
                    if (!seen.Add(cursor.Id)) { errors.Add($"pool {pool.Id}: vdev cycle at {v.Id}"); break; }
                    cursor = pool.Vdevs.FirstOrDefault(x => x?.Id == parentId);
                }
            }
        }
        return errors;
    }

    private static HashSet<string> Unique(IEnumerable<string?> ids, string path, List<string> errors)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
            if (string.IsNullOrEmpty(id) || !result.Add(id)) errors.Add($"{path}: empty or duplicate ID");
        return result;
    }

    private static void Reference(HashSet<string> ids, string? id, string path, List<string> errors)
    {
        if (id is null || !ids.Contains(id)) errors.Add($"{path}: unresolved ID {id ?? "null"}");
    }

    private static void DecimalCounter(string? value, string path, List<string> errors)
    {
        if (value is not null && (value.Length == 0 || (value.Length > 1 && value[0] == '0') ||
            value.Any(c => c is < '0' or > '9')))
            errors.Add($"{path}: expected unsigned decimal string");
    }

    private static void Utc(string? value, string path, List<string> errors)
    {
        if (value is null || !value.EndsWith('Z') ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out _))
            errors.Add($"{path}: expected UTC Z timestamp");
    }

    private static void ReadingState(Reading? reading, double? value, string? health,
        string path, List<string> errors)
    {
        if (reading is null) { errors.Add($"{path}: missing reading"); return; }
        if (reading.ObservedAt is not null) Utc(reading.ObservedAt, $"{path}.observed_at", errors);
        if (reading.LastAttemptAt is not null) Utc(reading.LastAttemptAt, $"{path}.last_attempt_at", errors);
        if (reading.StaleAfterSeconds < 0 || !double.IsFinite(reading.StaleAfterSeconds))
            errors.Add($"{path}: invalid stale limit");
        if (reading.AgeSeconds is < 0 || (reading.AgeSeconds.HasValue && !double.IsFinite(reading.AgeSeconds.Value)))
            errors.Add($"{path}: invalid reading age");
        if (reading.Freshness == "fresh" &&
            (value is null || reading.ObservedAt is null || reading.AgeSeconds is null ||
             reading.AgeSeconds > reading.StaleAfterSeconds))
            errors.Add($"{path}: inconsistent fresh reading");
        if (reading.Freshness == "stale" &&
            (value is null || reading.ObservedAt is null || reading.AgeSeconds is null ||
             reading.AgeSeconds <= reading.StaleAfterSeconds))
            errors.Add($"{path}: inconsistent stale reading");
        if (reading.Freshness == "unavailable" &&
            (value is not null || reading.ObservedAt is not null || reading.AgeSeconds is not null))
            errors.Add($"{path}: unavailable reading contains a value");
        if (reading.Freshness != "fresh" && health is not (null or "unknown"))
            errors.Add($"{path}: non-fresh reading claims current health");
    }
}
