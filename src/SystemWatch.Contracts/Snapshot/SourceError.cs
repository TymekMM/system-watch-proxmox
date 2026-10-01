// Contract DTOs for schema/snapshot.schema.json (1.0.0-draft.2).
// UTC timestamps and unsigned decimal counters stay strings to preserve wire precision.
using System.Text.Json.Serialization;

namespace SystemWatch.Contracts;

public sealed class SourceError
{
    [JsonPropertyName("code")]
    public required string Code { get; set; }

    [JsonPropertyName("message")]
    public required string Message { get; set; }

}
