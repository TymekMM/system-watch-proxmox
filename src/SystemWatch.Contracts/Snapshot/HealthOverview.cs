// Contract DTOs for schema/snapshot.schema.json (1.0.0-draft.2).
// UTC timestamps and unsigned decimal counters stay strings to preserve wire precision.
using System.Text.Json.Serialization;

namespace SystemWatch.Contracts;

public sealed class HealthOverview
{
    [JsonPropertyName("system")]
    public required Summary System { get; set; }

    [JsonPropertyName("system_policy")]
    public required string SystemPolicy { get; set; }

    [JsonPropertyName("zfs")]
    public required Summary Zfs { get; set; }

    [JsonPropertyName("hottest_temperature_id")]
    public required string? HottestTemperatureId { get; set; }

}
