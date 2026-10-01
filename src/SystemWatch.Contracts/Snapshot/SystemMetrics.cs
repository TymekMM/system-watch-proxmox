// Contract DTOs for schema/snapshot.schema.json (1.0.0-draft.2).
// UTC timestamps and unsigned decimal counters stay strings to preserve wire precision.
using System.Text.Json.Serialization;

namespace SystemWatch.Contracts;

public sealed class SystemMetrics
{
    [JsonPropertyName("uptime_seconds")]
    public required double? UptimeSeconds { get; set; }

    [JsonPropertyName("load_averages")]
    public required List<double?> LoadAverages { get; set; } = new();

}
