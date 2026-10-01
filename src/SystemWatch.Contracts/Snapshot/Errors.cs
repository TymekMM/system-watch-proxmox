// Contract DTOs for schema/snapshot.schema.json (1.0.0-draft.2).
// UTC timestamps and unsigned decimal counters stay strings to preserve wire precision.
using System.Text.Json.Serialization;

namespace SystemWatch.Contracts;

public sealed class Errors
{
    [JsonPropertyName("read")]
    public required string? Read { get; set; }

    [JsonPropertyName("write")]
    public required string? Write { get; set; }

    [JsonPropertyName("checksum")]
    public required string? Checksum { get; set; }

}
