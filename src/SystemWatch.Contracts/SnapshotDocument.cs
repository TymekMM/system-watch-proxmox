// Contract DTOs for schema/snapshot.schema.json (1.0.0-draft.2).
// UTC timestamps and unsigned decimal counters stay strings to preserve wire precision.
using System.Text.Json.Serialization;

namespace SystemWatch.Contracts;

public sealed class SnapshotDocument
{
    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; set; }

    [JsonPropertyName("snapshot")]
    public required SnapshotHeader Snapshot { get; set; }

    [JsonPropertyName("system")]
    public required SystemMetrics System { get; set; }

    [JsonPropertyName("health")]
    public required HealthOverview Health { get; set; }

    [JsonPropertyName("sources")]
    public required List<Source> Sources { get; set; } = new();

    [JsonPropertyName("temperatures")]
    public required List<Temperature> Temperatures { get; set; } = new();

    [JsonPropertyName("fans")]
    public required List<Fan> Fans { get; set; } = new();

    [JsonPropertyName("devices")]
    public required List<Device> Devices { get; set; } = new();

    [JsonPropertyName("zfs")]
    public required ZfsInventory Zfs { get; set; }

}
