// Contract DTOs for schema/snapshot.schema.json (1.0.0-draft.2).
// UTC timestamps and unsigned decimal counters stay strings to preserve wire precision.
using System.Text.Json.Serialization;

namespace SystemWatch.Contracts;

public sealed class Pool
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("state")]
    public required string State { get; set; }

    [JsonPropertyName("health")]
    public required string Health { get; set; }

    [JsonPropertyName("reading")]
    public required Reading Reading { get; set; }

    [JsonPropertyName("allocated_bytes")]
    public required string? AllocatedBytes { get; set; }

    [JsonPropertyName("size_bytes")]
    public required string? SizeBytes { get; set; }

    [JsonPropertyName("free_bytes")]
    public required string? FreeBytes { get; set; }

    [JsonPropertyName("capacity_percent")]
    public required double? CapacityPercent { get; set; }

    [JsonPropertyName("fragmentation_percent")]
    public required double? FragmentationPercent { get; set; }

    [JsonPropertyName("vdevs")]
    public required List<Vdev> Vdevs { get; set; } = new();

    [JsonPropertyName("scan")]
    public required Scan Scan { get; set; }

    [JsonPropertyName("permanent_errors")]
    public required PermanentErrors PermanentErrors { get; set; }

}
