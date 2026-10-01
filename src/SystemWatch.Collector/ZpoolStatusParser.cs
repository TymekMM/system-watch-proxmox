using System.Globalization;
using System.Text.Json;
using SystemWatch.Contracts;

namespace SystemWatch.Collector;

/// <summary>Parsed ZFS leaf before lsblk supplies a physical device identity.</summary>
public sealed record ZpoolStatusVdev(
    string Id, string? ParentId, string Kind, string AllocationClass,
    string Name, string State, string? Path,
    string? ReadErrors, string? WriteErrors, string? ChecksumErrors);

public sealed record ZpoolStatusPool(
    string Id, string Name, string State, string Health,
    ZpoolListEntry ListEntry, IReadOnlyList<ZpoolStatusVdev> Vdevs,
    Scan Scan, PermanentErrors PermanentErrors);

/// <summary>Pure parser for zpool status -j -p -P, checked against zpool list.</summary>
public static class ZpoolStatusParser
{
    public static IReadOnlyList<ZpoolStatusPool> Parse(
        string json, IReadOnlyDictionary<string, ZpoolListEntry> listed)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(listed);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var version = Property(Property(root, "output_version"), "vers_major");
        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var major) || major != 0)
            throw new InvalidDataException("unsupported zpool status JSON major version");
        var pools = Property(root, "pools");
        if (pools.ValueKind != JsonValueKind.Object ||
            pools.EnumerateObject().Count() != listed.Count ||
            pools.EnumerateObject().Any(p => !listed.ContainsKey(p.Name)))
            throw new InvalidDataException("pool inventory mismatch between zpool list and status");

        var result = new List<ZpoolStatusPool>();
        foreach (var poolProperty in pools.EnumerateObject())
        {
            var raw = poolProperty.Value;
            var name = poolProperty.Name;
            var list = listed[name];
            var state = String(raw, "state") ?? throw new InvalidDataException($"missing pool state: {name}");
            if (String(raw, "name") != name || state != list.State)
                throw new InvalidDataException($"pool state/name mismatch: {name}");
            var guid = Unsigned(Property(raw, "pool_guid"));
            var roots = Property(raw, "vdevs");
            if (guid is null || roots.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"missing pool GUID/topology: {name}");

            var nodes = new List<ZpoolStatusVdev>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            Walk(roots, null, name, nodes, seen);

            var count = Unsigned(Optional(raw, "error_count"));
            var hasErrors = count is not null && count != "0";
            var health = Severity(state);
            if (hasErrors && health == "ok") health = "warning";
            result.Add(new ZpoolStatusPool(
                "zpool:" + guid, name, state, health, list, nodes,
                MapScan(Optional(raw, "scan_stats")),
                new PermanentErrors
                {
                    State = hasErrors ? "present" : count == "0" ? "none" : "unknown",
                    Count = count == "0" ? count : null,
                    DetailsAvailable = false,
                }));
        }
        return result;
    }

    private static void Walk(JsonElement entries, string? parent, string pool,
        List<ZpoolStatusVdev> nodes, HashSet<string> seen)
    {
        if (entries.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"invalid vdev topology in {pool}");
        foreach (var child in entries.EnumerateObject())
        {
            var entry = child.Value;
            var guid = Unsigned(Optional(entry, "guid"));
            if (guid is null || !seen.Add(guid))
                throw new InvalidDataException($"missing/duplicate vdev GUID in {pool}");
            var kind = String(entry, "vdev_type") ?? "";
            if (kind is not ("root" or "mirror" or "raidz" or "draid" or "disk" or
                "file" or "replacing" or "spare")) kind = "other";
            var rawClass = String(entry, "class") ?? "";
            var allocationClass = rawClass == "normal" ? "data" :
                rawClass is "log" or "cache" or "spare" or "special" or "dedup" ? rawClass : "other";
            var path = String(entry, "path", null);
            var displayName = String(entry, "name", null);
            nodes.Add(new ZpoolStatusVdev(
                "vdev:" + guid, parent is null ? null : "vdev:" + parent,
                kind, allocationClass, !string.IsNullOrEmpty(displayName) ? displayName :
                    !string.IsNullOrEmpty(path) ? path : guid,
                String(entry, "state") ?? "UNKNOWN", path,
                Unsigned(Optional(entry, "read_errors")),
                Unsigned(Optional(entry, "write_errors")),
                Unsigned(Optional(entry, "checksum_errors"))));
            var descendants = Optional(entry, "vdevs");
            if (descendants.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                Walk(descendants, guid, pool, nodes, seen);
        }
    }

    private static Scan MapScan(JsonElement stats)
    {
        var type = String(stats, "function", "") switch
        {
            "SCRUB" => "scrub", "RESILVER" => "resilver", "NONE" => "none", _ => "unknown",
        };
        var state = String(stats, "state", "") switch
        {
            "SCANNING" => "running", "FINISHED" => "completed", "CANCELED" => "canceled",
            "PAUSED" => "paused", "NONE" => "none", _ => "unknown",
        };
        return new Scan
        {
            Type = type,
            State = state,
            StartedAt = Timestamp(Optional(stats, "start_time")),
            FinishedAt = state == "running" ? null : Timestamp(Optional(stats, "end_time")),
            ProgressPercent = null,
            ScannedBytes = Unsigned(Optional(stats, "examined")),
            IssuedBytes = Unsigned(Optional(stats, "issued")),
            TotalBytes = Unsigned(Optional(stats, "to_examine")),
            ScanBytesPerSecond = null,
            IssueBytesPerSecond = null,
            EtaSeconds = null,
            RepairedBytes = null,
            Errors = Unsigned(Optional(stats, "errors")),
            RawText = state == "unknown" && stats.ValueKind == JsonValueKind.Object
                ? stats.GetRawText() : null,
        };
    }

    private static string? Timestamp(JsonElement value)
    {
        var seconds = Unsigned(value);
        if (seconds is null || seconds == "0") return null;
        if (!long.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out var unix))
            throw new InvalidDataException($"timestamp out of range: {seconds}");
        try { return DateTimeOffset.FromUnixTimeSeconds(unix).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture); }
        catch (ArgumentOutOfRangeException error)
        { throw new InvalidDataException($"timestamp out of range: {seconds}", error); }
    }

    private static string Severity(string state) => state switch
    {
        "ONLINE" or "AVAIL" => "ok",
        "DEGRADED" or "OFFLINE" => "warning",
        "FAULTED" or "UNAVAIL" or "SUSPENDED" or "REMOVED" => "critical",
        _ => "unknown",
    };

    private static JsonElement Property(JsonElement element, string key)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out var value))
            throw new InvalidDataException($"missing {key} in zpool status");
        return value;
    }

    private static JsonElement Optional(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
            ? value : default;

    private static string? String(JsonElement element, string key, string? fallback = null)
    {
        var value = Optional(element, key);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : fallback;
    }

    private static string? Unsigned(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
            throw new InvalidDataException("invalid unsigned zpool counter");
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        if (text == "-") return null;
        if (text.Length == 0 || (text.Length > 1 && text[0] == '0') ||
            text.Any(c => c is < '0' or > '9'))
            throw new InvalidDataException($"invalid unsigned integer: '{text}'");
        return text;
    }
}
