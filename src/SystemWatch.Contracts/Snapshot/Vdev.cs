// Contract DTOs for schema/snapshot.schema.json (1.0.0-draft.2).
// UTC timestamps and unsigned decimal counters stay strings to preserve wire precision.
using System.Text.Json.Serialization;

namespace SystemWatch.Contracts;

public sealed class Vdev
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("parent_id")]
    public required string? ParentId { get; set; }

    [JsonPropertyName("kind")]
    public required string Kind { get; set; }

    [JsonPropertyName("allocation_class")]
    public required string AllocationClass { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("state")]
    public required string State { get; set; }

    [JsonPropertyName("device_id")]
    public required string? DeviceId { get; set; }

    [JsonPropertyName("errors")]
    public required Errors Errors { get; set; }

}
