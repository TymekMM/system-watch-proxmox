// Contract DTOs for schema/snapshot.schema.json (1.0.0-draft.2).
// UTC timestamps and unsigned decimal counters stay strings to preserve wire precision.
using System.Text.Json.Serialization;

namespace SystemWatch.Contracts;

public sealed class SnapshotHeader
{
    [JsonPropertyName("hostname")]
    public required string Hostname { get; set; }

    [JsonPropertyName("generated_at")]
    public required string GeneratedAt { get; set; }

    [JsonPropertyName("collector_version")]
    public required string CollectorVersion { get; set; }

    [JsonPropertyName("instance_id")]
    public required string InstanceId { get; set; }

    [JsonPropertyName("sequence")]
    public required long Sequence { get; set; }

}
