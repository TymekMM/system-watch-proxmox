// Contract DTOs for schema/snapshot.schema.json (1.0.0-draft.2).
// UTC timestamps and unsigned decimal counters stay strings to preserve wire precision.
using System.Text.Json.Serialization;

namespace SystemWatch.Contracts;

public sealed class Summary
{
    [JsonPropertyName("state")]
    public required string State { get; set; }

    [JsonPropertyName("coverage")]
    public required string Coverage { get; set; }

    [JsonPropertyName("reason_codes")]
    public required List<string> ReasonCodes { get; set; } = new();

}
