// Contract DTOs for schema/snapshot.schema.json (1.0.0-draft.2).
// UTC timestamps and unsigned decimal counters stay strings to preserve wire precision.
using System.Text.Json.Serialization;

namespace SystemWatch.Contracts;

public sealed class PermanentErrors
{
    [JsonPropertyName("state")]
    public required string State { get; set; }

    [JsonPropertyName("count")]
    public required string? Count { get; set; }

    [JsonPropertyName("details_available")]
    public required bool DetailsAvailable { get; set; }

}
