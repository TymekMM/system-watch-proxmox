using System.Text.Json;
using SystemWatch.Contracts;

namespace SystemWatch.Collector;

public sealed record LsblkInventory(List<Device> Devices, IReadOnlyDictionary<string, string> DeviceIdByPath);

/// <summary>Parse captured lsblk -J -b output without executing lsblk.</summary>
public static class LsblkInventoryParser
{
    public static LsblkInventory Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("blockdevices", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("missing lsblk blockdevices array");

        var devices = new List<Device>();
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in blocks.EnumerateArray())
        {
            if (Field(node, "type") != "disk") continue;
            var transport = (Field(node, "tran") ?? "").ToLowerInvariant();
            var wwn = Field(node, "wwn");
            var serial = Field(node, "serial");
            if (transport is not ("ata" or "sata" or "sas" or "nvme" or "usb") &&
                string.IsNullOrEmpty(wwn) && string.IsNullOrEmpty(serial)) continue;

            var path = Field(node, "path") ?? throw new InvalidDataException("physical disk missing lsblk path");
            var model = Field(node, "model");
            var id = !string.IsNullOrEmpty(wwn) ? "disk:wwn:" + wwn.ToLowerInvariant() :
                !string.IsNullOrEmpty(serial) ? "disk:serial:" + (model ?? "").Trim() + ":" + serial :
                "disk:session:" + path;
            if (!ids.Add(id)) throw new InvalidDataException($"colliding disk identity: {id}");

            var mounts = new HashSet<string>(StringComparer.Ordinal);
            var device = new Device
            {
                Id = id,
                IdentityStability = !string.IsNullOrEmpty(wwn) || !string.IsNullOrEmpty(serial)
                    ? "persistent" : "session",
                Path = path,
                Aliases = new List<string>(),
                Model = model,
                Serial = serial,
                Transport = transport == "sata" ? "ata" :
                    transport is "ata" or "sas" or "nvme" or "usb" ? transport : "unknown",
                Media = Boolean(node, "rota") switch { true => "hdd", false => "ssd", null => "unknown" },
                PowerState = "unknown",
                ZfsMembership = "nonmember",
                PoolIds = new List<string>(),
                TemperatureIds = new List<string>(),
                Usage = "unknown",
            };
            Visit(node, device.Id, paths, mounts);
            if (mounts.Contains("/") || mounts.Contains("/boot/efi")) device.Usage = "system";
            devices.Add(device);
        }
        return new LsblkInventory(devices, paths);
    }

    private static void Visit(JsonElement node, string id, Dictionary<string, string> paths,
        HashSet<string> mounts)
    {
        var path = Field(node, "path");
        if (!string.IsNullOrEmpty(path)) paths[path] = id;
        if (node.TryGetProperty("mountpoints", out var points) && points.ValueKind == JsonValueKind.Array)
            foreach (var point in points.EnumerateArray())
                if (point.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(point.GetString()))
                    mounts.Add(point.GetString()!);
        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray()) Visit(child, id, paths, mounts);
    }

    private static string? Field(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? Boolean(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.True => true, JsonValueKind.False => false, _ => null,
        } : null;
}
