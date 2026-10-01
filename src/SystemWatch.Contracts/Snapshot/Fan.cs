// Contract DTOs for schema/snapshot.schema.json (1.0.0-draft.2).
// UTC timestamps and unsigned decimal counters stay strings to preserve wire precision.
using System.Text.Json.Serialization;

namespace SystemWatch.Contracts;

public sealed class Fan
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("label")]
    public required string Label { get; set; }

    [JsonPropertyName("source_id")]
    public required string SourceId { get; set; }

    [JsonPropertyName("rpm")]
    public required double? Rpm { get; set; }

    [JsonPropertyName("reported_status")]
    public required string? ReportedStatus { get; set; }

    [JsonPropertyName("health")]
    public required string Health { get; set; }

    [JsonPropertyName("reading")]
    public required Reading Reading { get; set; }

    [JsonPropertyName("source_thresholds_rpm")]
    public required SourceThresholds? SourceThresholdsRpm { get; set; }

}
